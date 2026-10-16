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
    private readonly VnavmeshIpc navigation;
    private bool backendActive;
    private long? nextDiagnostic;
    private string lastDiagnostic = "";
    private Vector3 anchor;
    public int Attempts { get; private set; }
    public bool Succeeded { get; private set; }
    public bool Failed { get; private set; }
    public string Message { get; private set; } = "等待传送条件和位置稳定";
    public OperationReason Reason { get; private set; }
    public TransferSession(FieldNavigationHostServices services, TransferRequest request, RuntimeFrame frame, TimeProvider clock,
        VnavmeshIpc navigation)
    {
        this.services = services; this.request = request; this.clock = clock;
        this.navigation = navigation;
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
        if (clock.GetElapsedTime(started) >= request.TotalTimeout) { Fail(OperationReason.BudgetExceeded, $"传送总期限耗尽；最后状态：{Message}"); return; }
        if (!busy.HasFunction || (request.Kind == TransferKind.Aetheryte ? !teleport.HasFunction : !aethernet.HasFunction))
        { Fail(OperationReason.BackendUnavailable, "Lifestream 传送接口不可用"); return; }
        if (frame.Loading) { if (Attempts > 0) transitionSeen = true; stable = null; Message = "等待传送切图完成"; return; }
        if (!frame.Valid || frame.Dead || frame.Job != job) { Fail(OperationReason.ContextChanged, "传送期间角色或职业状态失效"); return; }
        uint territory = frame.Travel.Territory;
        if (territory != origin && territory != request.TerritoryId) { Fail(OperationReason.ContextChanged, "传送进入了意外区域"); return; }
        bool active = backendActive = busy.InvokeFunc();
        bool arrived = territory == request.TerritoryId
            && (arrivalPosition is not { } point || (request.ArrivalUse3DDistance ? Vector3.Distance(frame.Travel.Position, point) : TravelGeometry.HorizontalDistance(frame.Travel.Position, point)) <= request.ArrivalRadius)
            && (!request.ForceTransfer || Attempts > 0 && transitionSeen);
        if (arrived && !active && !frame.Casting && !frame.Occupied && !frame.Travel.Flight.MountTransition && !frame.Travel.Flight.Jumping)
        {
            Message = "已到目标位置，等待稳定";
            if (Stable(frame.Travel.Position, request.SettleDuration)) { Succeeded = true; Message = "实际传送抵达并稳定"; }
            return;
        }
        string? blocked = frame.Casting ? "等待咏唱结束"
            : frame.Occupied ? "等待角色占用结束"
            : frame.InCombat ? "等待脱离战斗"
            : active ? "等待 Lifestream 当前操作结束"
            : frame.Travel.Flight.MountTransition ? "等待坐骑过渡结束"
            : frame.Travel.Flight.Jumping ? "等待跳跃结束" : null;
        if (blocked is not null)
        {
            stable = null; Message = blocked; return;
        }
        if (navigation.Raw.IsMoveActive)
        { stable = null; Message = "传送前等待外部导航停止"; return; }
        if (Attempts > 0 && clock.GetElapsedTime(submitted) < request.ResponseTimeout)
        { stable = null; Message = "等待传送实际响应"; return; }
        if (transitionSeen) { Fail(OperationReason.TransferNotConfirmed, "传送切图后未到达指定目的地，未重复发起"); return; }
        if (Attempts >= request.MaximumAttempts) { Fail(OperationReason.TransferNotConfirmed, "传送尝试次数耗尽"); return; }
        if (!Stable(frame.Travel.Position, TimeSpan.FromSeconds(1)))
        { Message = "传送前等待位置连续稳定1秒"; return; }
        Attempts++; submitted = clock.GetTimestamp(); stable = null;
        bool accepted = request.Kind == TransferKind.Aetheryte
            ? teleport.InvokeFunc(request.DestinationId, request.SubIndex) : aethernet.InvokeFunc(request.DestinationId);
        Message = accepted ? "传送请求已接受，等待实际抵达" : "传送请求被拒绝，按预算等待后重试";
    }
    public void ReportStatus(RuntimeFrame frame)
    {
        long now = clock.GetTimestamp();
        string summary = $"{Message}; flight={frame.Travel.Flight.InFlight}; mounted={!frame.Travel.Dismounted}; "
            + $"casting={frame.Casting}; occupied={frame.Occupied}; combat={frame.InCombat}; backendBusy={backendActive}";
        if (nextDiagnostic is not null && now < nextDiagnostic && summary == lastDiagnostic) return;
        // Position and continuously changing counters are deliberately omitted from the change key.
        nextDiagnostic = now + 5 * clock.TimestampFrequency; lastDiagnostic = summary;
        string occupiedFlags = frame.Occupied ? string.Join(",", new[] {
            Dalamud.Game.ClientState.Conditions.ConditionFlag.Occupied,
            Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInEvent,
            Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInQuestEvent,
            Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInCutSceneEvent,
            Dalamud.Game.ClientState.Conditions.ConditionFlag.WatchingCutscene,
            Dalamud.Game.ClientState.Conditions.ConditionFlag.Crafting,
            Dalamud.Game.ClientState.Conditions.ConditionFlag.Gathering,
        }.Where(flag => services.Condition[flag])) : "";
        services.Log.Information("[FN_TRANSFER] {Status}; origin={Origin}; current={Current}; destination={Destination}; attempts={Attempts}/{Maximum}; elapsed={Elapsed:F1}s; position={Position}; occupiedFlags=[{Flags}]",
            summary, origin, frame.Travel.Territory, request.TerritoryId, Attempts, request.MaximumAttempts,
            clock.GetElapsedTime(started).TotalSeconds, frame.Travel.Position, occupiedFlags);
    }
    private bool Stable(Vector3 position, TimeSpan duration)
    {
        if (stable is null || Vector3.Distance(position, anchor) > .1f) { stable = clock.GetTimestamp(); anchor = position; }
        return clock.GetElapsedTime(stable.Value) >= duration;
    }
    private void Fail(OperationReason reason, string message) { Failed = true; Reason = reason; Message = message; }
    // No global Abort: Lifestream has no owned cancellation token. Already-issued transfer cannot be safely undone.
}
