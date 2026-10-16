using System.Numerics;
using Dalamud.Plugin.Ipc;

namespace FieldNavigation;

/// <summary>One Lifestream contract and arrival state machine for every consumer.</summary>
internal sealed class TransferSession
{
    private readonly ICallGateSubscriber<bool> busy;
    private readonly ICallGateSubscriber<uint, byte, bool> teleport;
    private readonly ICallGateSubscriber<uint, bool> aethernet;
    private readonly FieldNavigationHostServices services;
    private readonly TransferRequest request;
    private readonly Vector3? arrivalPosition;
    public bool IsBusy => busy.HasFunction && busy.InvokeFunc();
    private readonly TimeProvider clock;
    private readonly long started;
    private readonly uint origin;
    private readonly uint job;
    private bool transitionSeen;
    private long submitted;
    private long? stable;
    private Vector3 anchor;
    public int Attempts { get; private set; }
    public bool Succeeded { get; private set; }
    public bool Failed { get; private set; }
    public string Message { get; private set; } = "等待传送条件和位置稳定";
    public OperationReason Reason { get; private set; }
    public TransferSession(FieldNavigationHostServices services, TransferRequest request, RuntimeFrame frame, TimeProvider clock)
    {
        this.services = services; this.request = request; this.clock = clock;
        arrivalPosition = request.ArrivalPosition ?? TransferDestinationResolver.Find(services.DataManager, request.Kind, request.DestinationId, request.TerritoryId);
        started = clock.GetTimestamp(); origin = frame.Travel.Territory; job = frame.Job;
        busy = services.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        teleport = services.PluginInterface.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
        aethernet = services.PluginInterface.GetIpcSubscriber<uint, bool>("Lifestream.AethernetTeleportByPlaceNameId");
        if (request.Kind == TransferKind.Aetheryte)
        {
            var row = services.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>().GetRowOrDefault(request.DestinationId);
            if (row is null || row.Value.Territory.RowId != request.TerritoryId) Fail(OperationReason.InvalidRequest, "传送目的地与目标区域不匹配");
        }
        if ((origin == request.TerritoryId || request.Kind == TransferKind.Aethernet) && arrivalPosition is null)
            Fail(OperationReason.InvalidRequest, "同地图/都市传送必须提供实际抵达位置");
    }
    public void Tick(RuntimeFrame frame)
    {
        if (Succeeded || Failed) return;
        if (clock.GetElapsedTime(started) >= request.TotalTimeout) { Fail(OperationReason.BudgetExceeded, "传送总期限耗尽"); return; }
        if (!busy.HasFunction || (request.Kind == TransferKind.Aetheryte ? !teleport.HasFunction : !aethernet.HasFunction))
        { Fail(OperationReason.BackendUnavailable, "Lifestream 传送接口不可用"); return; }
        if (frame.Loading) { if (Attempts > 0) transitionSeen = true; stable = null; Message = "等待传送切图完成"; return; }
        if (!frame.Valid || frame.Dead || frame.Job != job) { Fail(OperationReason.ContextChanged, "传送期间角色或职业状态失效"); return; }
        uint territory = frame.Travel.Territory;
        if (territory != origin && territory != request.TerritoryId) { Fail(OperationReason.ContextChanged, "传送进入了意外区域"); return; }
        bool active = busy.InvokeFunc();
        bool arrived = territory == request.TerritoryId
            && (arrivalPosition is not { } point || (request.ArrivalUse3DDistance ? Vector3.Distance(frame.Travel.Position, point) : TravelGeometry.HorizontalDistance(frame.Travel.Position, point)) <= request.ArrivalRadius)
            && (!request.ForceTransfer || Attempts > 0 && transitionSeen);
        if (arrived && !active && !frame.Casting && !frame.Occupied && !frame.Travel.Flight.MountTransition && !frame.Travel.Flight.Jumping)
        {
            Message = "已到目标位置，等待稳定";
            if (Stable(frame.Travel.Position, request.SettleDuration)) { Succeeded = true; Message = "实际传送抵达并稳定"; }
            return;
        }
        if (frame.Casting || frame.Occupied || frame.InCombat || active || frame.Travel.Flight.InFlight
            || frame.Travel.Flight.Jumping || frame.Travel.Flight.MountTransition)
        { stable = null; Message = "等待传送/咏唱/占用结束"; return; }
        if (Attempts > 0 && clock.GetElapsedTime(submitted) < request.ResponseTimeout)
        { stable = null; Message = "等待传送实际响应"; return; }
        if (transitionSeen) { Fail(OperationReason.TransferNotConfirmed, "传送切图后未到达指定目的地，未重复发起"); return; }
        if (Attempts >= request.MaximumAttempts) { Fail(OperationReason.TransferNotConfirmed, "传送尝试次数耗尽"); return; }
        if (!Stable(frame.Travel.Position, TimeSpan.FromSeconds(1))) return;
        Attempts++; submitted = clock.GetTimestamp(); stable = null;
        bool accepted = request.Kind == TransferKind.Aetheryte
            ? teleport.InvokeFunc(request.DestinationId, request.SubIndex) : aethernet.InvokeFunc(request.DestinationId);
        Message = accepted ? "传送请求已接受，等待实际抵达" : "传送请求被拒绝，按预算等待后重试";
    }
    private bool Stable(Vector3 position, TimeSpan duration)
    {
        if (stable is null || Vector3.Distance(position, anchor) > .1f) { stable = clock.GetTimestamp(); anchor = position; }
        return clock.GetElapsedTime(stable.Value) >= duration;
    }
    private void Fail(OperationReason reason, string message) { Failed = true; Reason = reason; Message = message; }
    // No global Abort: Lifestream has no owned cancellation token. Already-issued transfer cannot be safely undone.
}
