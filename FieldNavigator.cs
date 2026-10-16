using System.Numerics;
using System.Diagnostics;

namespace FieldNavigation;

/// <summary>Framework-owned navigation, transfers and relocation. UI reads managed handles only.</summary>
public sealed class FieldNavigator : IDisposable, IAsyncDisposable
{
    private readonly FieldNavigationHostServices services;
    private readonly TimeProvider clock;
    private readonly VnavmeshIpc navigation;
    private readonly MountAdapter mount;
    private readonly LandingAdapter descent;
    private readonly bool ownsDescent;
    private readonly TravelJourney journey;
    private readonly NavigationRuntime runtime;
    private readonly NoFlyZoneCatalog zones;
    private readonly List<Vector3> rejected = [];
    private readonly Queue<FnOperation> notifications = new();
    private FnOperation? active;
    private NavigateRequest? navigate;
    private RelocateRequest? relocate;
    private TransferRequest? transfer;
    private DestinationSearch? search;
    private BuiltInRelocation? relocation;
    private TransferSession? transferSession;
    private RuntimeFrame initial;
    private Vector3 target, operationOrigin, lastPosition, backwards, candidate;
    private long started, jumpStarted, recoveryStarted;
    private int recoveries, attempts;
    private bool initialized, journeyStarted, exitReached, forceGround, jumpWaiting;
    private RecoveryMode recoveryMode;
    private string phase = "Pending", message = "等待 Framework 推进";
    private bool disposed;
    private int disposalRequested;
    private readonly object disposalGate = new();
    private Task? disposalTask;
    private (OperationStatus Status, OperationReason Reason, string Message)? pendingFinish;
    private int targetReplans;
    private FieldNavigator(FieldNavigationHostServices services, TimeProvider? clock)
    {
        this.services = services; this.clock = clock ?? TimeProvider.System;
        navigation = services.SharedNavigation ?? new VnavmeshIpc(services.PluginInterface, () => services.Objects.LocalPlayer?.Position);
        mount = services.SharedMount ?? new MountAdapter(services.Condition, services.Objects);
        descent = services.SharedLanding ?? new LandingAdapter(services.Interop);
        ownsDescent = services.SharedLanding is null;
        zones = new(services.DataManager); runtime = new(services, mount, zones);
        journey = new(navigation, descent, new TravelMovementControl(mount, new TakeoffAdapter(services.Condition, services.Objects)), this.clock, externalRecovery: true);
    }
    public static FieldNavigator Create(FieldNavigationHostServices hostServices, TimeProvider? clock = null) => new(hostServices, clock);
    public FnOperation Navigate(NavigateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CheckFinite(request.Target);
        if (request.Arrival is null || request.Movement is null || request.Recovery is null
            || !float.IsFinite(request.Arrival.Radius) || request.Arrival.Radius <= 0
            || float.IsNaN(request.Arrival.HeightTolerance) || request.Arrival.HeightTolerance < 0
            || request.Arrival.SettleDuration < TimeSpan.Zero || request.Recovery.TotalTimeout <= TimeSpan.Zero
            || request.Recovery.MaximumRecoveries < 0 || request.Recovery.StallTimeout <= TimeSpan.Zero
            || request.Recovery.PathfindTimeout <= TimeSpan.Zero || !float.IsFinite(request.LandingRadius) || request.LandingRadius <= 0
            || !float.IsFinite(request.FlightHeight) || request.FlightHeight < 0)
            throw new ArgumentOutOfRangeException(nameof(request));
        var handle = Begin(); navigate = request with { CandidatePositions = request.CandidatePositions.ToArray() }; target = request.Target;
        return handle;
    }
    public FnOperation Transfer(TransferRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DestinationId == 0 || request.TerritoryId == 0 || request.MaximumAttempts <= 0
            || request.TotalTimeout <= TimeSpan.Zero || request.ResponseTimeout <= TimeSpan.Zero || request.SettleDuration < TimeSpan.Zero
            || !float.IsFinite(request.ArrivalRadius) || request.ArrivalRadius <= 0
            || request.ArrivalPosition is { } point && !TravelGeometry.IsFinite(point)) throw new ArgumentOutOfRangeException(nameof(request));
        var handle = Begin(); transfer = request; return handle;
    }
    public FnOperation Relocate(RelocateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!float.IsFinite(request.Distance) || request.Distance <= 0 || !float.IsFinite(request.MinimumDisplacement) || request.MinimumDisplacement <= 0
            || !float.IsFinite(request.MaximumDisplacement) || request.MaximumDisplacement < request.MinimumDisplacement
            || !float.IsFinite(request.MaximumPathLength) || request.MaximumPathLength <= 0 || request.TotalTimeout <= TimeSpan.Zero
            || request.SettleDuration < TimeSpan.Zero || request.AwayFrom is { } point && !TravelGeometry.IsFinite(point))
            throw new ArgumentOutOfRangeException(nameof(request));
        var handle = Begin(); relocate = request; return handle;
    }
    private FnOperation Begin()
    {
        AssertFramework(); ObjectDisposedException.ThrowIf(disposed || Volatile.Read(ref disposalRequested) != 0, this);
        if (active is not null) throw new InvalidOperationException("先取消并等待当前 FN 操作完成清理，再发起新操作。");
        active = new(); navigate = null; transfer = null; relocate = null;
        recoveries = attempts = targetReplans = 0; pendingFinish = null; initialized = journeyStarted = exitReached = forceGround = jumpWaiting = false;
        recoveryMode = RecoveryMode.None; rejected.Clear(); phase = "Pending"; message = "等待 Framework 推进";
        started = clock.GetTimestamp();
        return active;
    }
    public void Tick()
    {
        AssertFramework(); if (disposed || Volatile.Read(ref disposalRequested) != 0) return;
        // Dispatch last tick's completions before processing new operations. New callback requests only run next tick.
        var current = active;
        while (notifications.TryDequeue(out var completed)) completed.Notify(ex => services.Log.Error(ex, "FN 完成通知异常"));
        if (current is null || !ReferenceEquals(current, active)) return;
        try
        {
            if (pendingFinish is { } pending) { Finish(pending.Status, pending.Reason, pending.Message); return; }
            if (current.CancellationRequested) { Finish(OperationStatus.Cancelled, OperationReason.CancelledByHost, "宿主取消操作"); return; }
            RuntimeFrame frame = runtime.Read();
            if (!initialized)
            {
                if (!frame.Valid || frame.Loading || frame.Dead) { Finish(OperationStatus.Failed, OperationReason.ContextChanged, "启动时角色或地图未就绪"); return; }
                initial = frame; operationOrigin = lastPosition = frame.Travel.Position; initialized = true;
            }
            if (frame.Travel.Character != initial.Travel.Character || frame.Dead
                || !frame.Valid && !(transfer is not null && frame.Loading))
            { Finish(OperationStatus.Failed, OperationReason.ContextChanged, "角色状态已失效"); return; }
            if (transfer is not null)
            {
                transferSession ??= new(services, transfer, initial, clock, navigation);
                transferSession.Tick(frame); transferSession.ReportStatus(frame); attempts = transferSession.Attempts;
                phase = "Transferring"; message = transferSession.Message;
                if (transferSession.Succeeded) Finish(OperationStatus.Succeeded, OperationReason.None, message);
                else if (transferSession.Failed) Finish(OperationStatus.Failed, transferSession.Reason, message);
                else Publish();
                return;
            }
            if (frame.Loading || frame.Travel.Territory != initial.Travel.Territory)
            { Finish(OperationStatus.Failed, OperationReason.ContextChanged, "区域加载或地图变化，旧路径已取消"); return; }
            TimeSpan limit = navigate?.Recovery.TotalTimeout ?? relocate!.TotalTimeout;
            if (clock.GetElapsedTime(started) >= limit) { Finish(OperationStatus.Failed, OperationReason.BudgetExceeded, "操作总期限耗尽"); return; }
            if (!navigation.IsAvailable) { phase = "WaitingBackend"; message = "等待导航后端就绪"; Publish(); return; }
            if (navigation.Raw.IsPathRunning && !navigation.Paths.IsPathRunning)
            { Finish(OperationStatus.Failed, OperationReason.MovementConflict, "外部路径接管移动"); return; }
            if (relocate is not null)
            {
                if (!frame.Travel.MovementReady || frame.Travel.Flight.MountTransition || frame.Travel.Flight.Jumping)
                { phase = "WaitingReady"; message = "等待短移动作条件"; Publish(); return; }
                relocation ??= new(navigation, zones, relocate, frame.Travel, Backwards(frame, relocate.AwayFrom), clock);
                relocation.Tick(frame.Travel); recoveryMode = relocation.Mode; phase = "Relocating"; message = relocation.Message;
                if (relocation.Completed) Finish(OperationStatus.Succeeded, OperationReason.None, message);
                else if (relocation.Failed) Finish(OperationStatus.Failed, OperationReason.MovementStalled, message);
                else Publish();
                return;
            }
            TickNavigate(frame);
            lastPosition = frame.Travel.Position;
            if (active is not null) Publish();
        }
        catch (Exception ex)
        {
            services.Log.Warning(ex, "FN 操作失败，正在清理所属移动");
            Finish(OperationStatus.Failed, OperationReason.BackendUnavailable, ex.Message);
        }
    }
    private void TickNavigate(RuntimeFrame frame)
    {
        var request = navigate!;
        if (request.Movement.AllowDuringCombat && frame.InCombat && !frame.Casting && !frame.Occupied)
            frame = frame with { Travel = frame.Travel with { MovementReady = true } };
        if (active!.TakeTarget() is { } fresh && Vector3.Distance(target, fresh) > 1)
        {
            if (++targetReplans > 64) { Finish(OperationStatus.Failed, OperationReason.BudgetExceeded, "移动目标重规划次数耗尽"); return; }
            if (journeyStarted && journey.IsMountPreparationActive)
            {
                // A moving FATE marker must not cancel an already accepted mount cast.
                // Keep the newest target queued and apply it after the cast completes.
                active.UpdateTarget(fresh);
                phase = "Preparing";
                message = "等待坐骑读条完成后更新目标";
            }
            else
            {
                target = fresh; search?.Dispose(); search = null; rejected.Clear();
                if (journeyStarted) { exitReached |= journey.IntermediateWaypointReached; journey.Cancel(); journeyStarted = false; }
            }
        }
        if (!frame.Travel.MovementReady)
        {
            if (journeyStarted) { exitReached |= journey.IntermediateWaypointReached; journey.Cancel(); journeyStarted = false; }
            relocation?.Dispose(); relocation = null; jumpWaiting = false; recoveryMode = RecoveryMode.None;
            phase = "WaitingReady"; message = "等待施法/战斗/占用结束"; return;
        }
        if (jumpWaiting)
        {
            if (clock.GetElapsedTime(recoveryStarted) >= TimeSpan.FromSeconds(20)) { Finish(OperationStatus.Failed, OperationReason.MovementStalled, "地面跳跃恢复超时"); return; }
            if (frame.Travel.Flight.InFlight) { jumpWaiting = false; StartRelocation(frame); return; }
            if (frame.Travel.Flight.Jumping || frame.Travel.Flight.MountTransition || clock.GetElapsedTime(jumpStarted) < TimeSpan.FromMilliseconds(800)) return;
            jumpWaiting = false;
            if (Vector3.Distance(frame.Travel.Position, lastRecoveryPosition) >= .5f) { Replan(); return; }
            StartRelocation(frame); return;
        }
        if (relocation is not null)
        {
            relocation.Tick(frame.Travel); phase = "Recovering"; message = relocation.Message;
            if (relocation.Completed) { relocation.Dispose(); relocation = null; Replan(); }
            else if (relocation.Failed)
            {
                string failure = relocation.Message; relocation.Dispose(); relocation = null;
                if (request.Recovery.AllowGroundFallback && frame.Travel.Flight.InFlight && !forceGround)
                { forceGround = true; Replan(); }
                else Finish(OperationStatus.Failed, OperationReason.MovementStalled, failure);
            }
            return;
        }
        if (!journeyStarted)
        {
            search ??= new(navigation, zones, request, target, operationOrigin, frame.Travel.Territory, rejected, clock);
            search.Tick(frame.Travel.Position, frame.Travel.CanFly && !forceGround, frame.Travel.Flight.InFlight);
            phase = "SelectingDestination"; message = search.Message;
            if (search.Exhausted) { Finish(OperationStatus.Failed, OperationReason.NoPath, message); return; }
            if (search.Result is not { } point) return;
            candidate = point; search.Dispose(); search = null;
            StartJourney(frame);
        }
        var observation = Observe(frame);
        Vector3 delta = frame.Travel.Position - lastPosition;
        if (delta.LengthSquared() >= .0025f) backwards = -delta;
        var result = journey.Tick(observation);
        phase = result.State.ToString(); message = result.Reason;
        exitReached |= journey.IntermediateWaypointReached;
        if (result.Outcome == NavigationTravelOutcome.Arrived)
        {
            if (!request.Arrival.Policy.Contains(frame.Travel.Position, target)) { RejectCandidate(frame); return; }
            Finish(OperationStatus.Succeeded, OperationReason.None, result.Reason); return;
        }
        if (result.Outcome != NavigationTravelOutcome.Failed) return;
        bool movingStall = journey.LastFailureStage is NavigationFailureStage.GroundStall or NavigationFailureStage.FlightStall;
        if (!ConsumeRecovery()) return;
        journey.Cancel(); journeyStarted = false;
        if (!movingStall) { rejected.Add(candidate); Replan(); return; }
        recoveryStarted = clock.GetTimestamp();
        recoveryMode = frame.Travel.Flight.InFlight ? RecoveryMode.Airborne : RecoveryMode.Ground;
        if (!frame.InCombat && !frame.Travel.Flight.InFlight && !frame.Travel.Flight.MountTransition && request.Recovery.AllowJump && mount.TryJump())
        {
            jumpWaiting = true; jumpStarted = clock.GetTimestamp(); lastRecoveryPosition = frame.Travel.Position;
            phase = "JumpRecovery"; message = "地面跳跃并观察有效位移"; return;
        }
        StartRelocation(frame);
    }
    private Vector3 lastRecoveryPosition;
    private void RejectCandidate(RuntimeFrame frame)
    {
        if (!ConsumeRecovery()) return;
        journey.Cancel(); journeyStarted = false; rejected.Add(candidate); Replan();
    }
    private bool ConsumeRecovery()
    {
        if (recoveries >= navigate!.Recovery.MaximumRecoveries) { Finish(OperationStatus.Failed, OperationReason.BudgetExceeded, "恢复次数耗尽"); return false; }
        recoveries++; return true;
    }
    private void StartRelocation(RuntimeFrame frame)
    {
        if (frame.InCombat) { Finish(OperationStatus.Failed, OperationReason.MovementStalled, "战斗位移卡住，不强行跳跃或短移"); return; }
        if (frame.Travel.Flight.InFlight && !navigate!.IgnoreNoFlyZones
            && zones.Find(frame.Travel.Territory, frame.Travel.Position) is not null
            && !zones.IsRouteExempt(frame.Travel.Territory, operationOrigin, target) && navigate.Recovery.AllowGroundFallback)
        { forceGround = true; Replan(); return; }
        if (!navigate!.Recovery.AllowRelocation) { Finish(OperationStatus.Failed, OperationReason.MovementStalled, "未允许短移且跳跃恢复无效"); return; }
        var options = new RelocateRequest { IgnoreNoFlyZones = navigate.IgnoreNoFlyZones };
        relocation = new(navigation, zones, options, frame.Travel, backwards.LengthSquared() > .01f ? backwards : Backwards(frame, target), clock);
        recoveryMode = relocation.Mode; phase = "Recovering"; message = "选择内置短移恢复路径";
    }
    private TravelObservation Observe(RuntimeFrame frame) => frame.Travel with
    {
        NoFlyExempt = navigate!.IgnoreNoFlyZones || zones.IsRouteExempt(frame.Travel.Territory, operationOrigin, candidate),
        CanFly = frame.Travel.CanFly && navigate.Movement.AllowFlight && navigate.Movement.AllowMount && !forceGround,
    };
    private void StartJourney(RuntimeFrame frame)
    {
        var request = navigate!;
        bool mountAllowed = request.Movement.AllowMount && !frame.InCombat && services.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()
            .GetRowOrDefault(frame.Travel.Territory) is { Mount: true };
        TravelMovementMode mode = !mountAllowed ? TravelMovementMode.Foot : request.Movement.Mode;
        if ((!request.Movement.AllowFlight || forceGround) && mode == TravelMovementMode.Fly) mode = TravelMovementMode.GroundMount;
        var observation = Observe(frame);
        Vector3? waypoint = !exitReached && observation.CanFly && mode is TravelMovementMode.Automatic or TravelMovementMode.Fly
            ? zones.FindFlightExitWaypoint(frame.Travel.Territory, operationOrigin, target) : null;
        navigation.Paths.MaximumPathLength = float.PositiveInfinity;
        navigation.Paths.QueryTimeout = request.Recovery.PathfindTimeout;
        journey.Start(new TravelRequest
        {
            Destination = candidate, ApproachPoint = candidate, LandingPoint = candidate,
            GroundKind = request.GroundKind == GroundDestinationKind.MapWaypoint ? GroundDestinationKind.Raw : request.GroundKind,
            Movement = new() { Preferred = mode, WalkDistance = request.Movement.WalkDistance, GroundMountDistance = request.Movement.GroundMountDistance },
            Arrival = request.Arrival.Policy with { Radius = Math.Min(request.Arrival.Radius, 1), HeightTolerance = Math.Min(request.Arrival.HeightTolerance, 2) },
            LandingRadius = request.LandingRadius, FlightHeight = request.FlightHeight,
            IntermediateWaypoint = waypoint, IntermediateWaypointIsMapCoordinate = true,
            OperationTimeout = request.Recovery.TotalTimeout - clock.GetElapsedTime(started),
            StallTimeout = request.Recovery.StallTimeout, StopOnStall = true,
        }, observation);
        attempts++; journeyStarted = true; recoveryMode = RecoveryMode.None;
    }
    private void Replan()
    {
        search?.Dispose(); search = null; journeyStarted = false; recoveryMode = RecoveryMode.None;
        phase = "Replanning"; message = "从当前位置重新导航到原目标";
    }
    private Vector3 Backwards(RuntimeFrame frame, Vector3? awayFrom)
    {
        if (awayFrom is { } point && Vector3.Distance(point, frame.Travel.Position) > .1f) return frame.Travel.Position - point;
        return new(-MathF.Sin(frame.Rotation), 0, -MathF.Cos(frame.Rotation));
    }
    private void Publish(OperationStatus status = OperationStatus.Running, OperationReason reason = OperationReason.None)
    {
        active?.Publish(new(active.Snapshot.OperationId, status, phase, reason, message, attempts, recoveries,
            clock.GetElapsedTime(started), relocation?.Destination ?? (navigate is not null && attempts > 0 ? candidate : null), recoveryMode));
    }
    private void Finish(OperationStatus status, OperationReason reason, string text)
    {
        if (active is null) return;
        message = text;
        pendingFinish = (status, reason, text);
        // Retain the operation on cleanup failure, and retry on the next Framework tick.
        try { Cleanup(); }
        catch (Exception ex) { phase = "CleaningUp"; message = $"{text}；清理等待重试：{ex.Message}"; Publish(OperationStatus.Cancelling, reason); return; }
        phase = status.ToString(); Publish(status, reason); pendingFinish = null;
        notifications.Enqueue(active); active = null;
    }
    private void Cleanup()
    {
        search?.Dispose(); search = null;
        if (journeyStarted) { journey.Cancel(); journeyStarted = false; }
        relocation?.Dispose(); relocation = null;
        descent.StopDescending();
        if (transferSession is { Attempts: > 0 } pending && (pending.IsBusy || services.Condition.Any(
            Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas,
            Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51,
            Dalamud.Game.ClientState.Conditions.ConditionFlag.Casting)))
            throw new InvalidOperationException("等待已提交的传送结束；后端不支持所属请求取消");
        transferSession = null;
        navigation.Paths.MaximumPathLength = float.PositiveInfinity;
    }
    private void AssertFramework()
    {
        if (!services.Framework.IsInFrameworkUpdateThread) throw new InvalidOperationException("FN 启动、更新和卸载必须在 Framework 线程调用。");
    }
    private static void CheckFinite(Vector3 position) { if (!TravelGeometry.IsFinite(position)) throw new ArgumentOutOfRangeException(nameof(position)); }
    /// <summary>Await from an async plugin disposer; never blocks the Framework thread.</summary>
    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            if (disposalTask is not null) return new(disposalTask);
            Interlocked.Exchange(ref disposalRequested, 1);
            active?.Cancel();
            disposalTask = services.Framework.RunOnFrameworkThread(() =>
            {
                if (services.Framework.IsInFrameworkUpdateThread) Dispose();
                else if (services.Framework.IsFrameworkUnloading) DisposeAfterFrameworkShutdown();
                else throw new InvalidOperationException("Framework 未将 FN 卸载调度到框架线程。");
            });
            return new(disposalTask);
        }
    }

    /// <summary>Synchronous cleanup only on Framework. Other threads must await DisposeAsync.</summary>
    public void Dispose()
    {
        if (disposed) return;
        AssertFramework();
        DisposeCore(frameworkAvailable: true);
    }

    // The Framework dispatcher may execute inline after it has been destroyed.
    // Only invalidate managed work and release hooks then; no IPC or game-state reads.
    private void DisposeAfterFrameworkShutdown() => DisposeCore(frameworkAvailable: false);

    private void DisposeCore(bool frameworkAvailable)
    {
        if (disposed) return;
        Interlocked.Exchange(ref disposalRequested, 1);
        disposed = true; // Invalidate first: no Tick, late result, or completion handler may restart work.
        List<Exception> errors = [];
        void Release(Action action) { try { action(); } catch (Exception ex) { errors.Add(ex); } }
        Release(() => search?.Dispose()); search = null;
        Release(navigation.Paths.CancelCalculation);
        descent.StopDescending(); // Managed volatile flag, safe even if Framework is gone.
        if (frameworkAvailable)
        {
            Release(() => relocation?.Dispose());
            Release(journey.Dispose);
            Release(navigation.Stop);
        }
        relocation = null; transferSession = null;
        notifications.Clear(); pendingFinish = null;
        if (ownsDescent) Release(descent.Dispose); // Never skip the hook if earlier cleanup failed.
        if (active is { } operation)
        {
            message = errors.Count == 0 ? "FN 已卸载；已提交的外部传送不会续发操作" : "FN 已停止，但部分资源释放失败";
            phase = errors.Count == 0 ? "Cancelled" : "DisposalFailed";
            Publish(errors.Count == 0 ? OperationStatus.Cancelled : OperationStatus.Failed,
                errors.Count == 0 ? OperationReason.CancelledByHost : OperationReason.BackendUnavailable);
            active = null;
        }
        if (errors.Count > 0) throw new AggregateException("FN 释放部分资源失败；其余资源均已尝试释放。", errors);
    }
}
