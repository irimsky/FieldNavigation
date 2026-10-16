using System.Diagnostics;
using System.Numerics;

namespace FieldNavigation;

public enum TravelLandingStrategy { ExactPoint, AroundTarget }

public enum TravelJourneyState { Idle, Preparing, Travelling, Landing, Dismounting, FinalApproach, Settling, Arrived, Failed, Cancelled }

/// <summary>One business travel operation. Internal replans share the original operation budget.</summary>
public sealed record TravelRequest
{
    public required Vector3 Destination { get; init; }
    /// <summary>Required airborne checkpoint used before continuing to Destination.</summary>
    public Vector3? IntermediateWaypoint { get; init; }
    /// <summary>Resolve a horizontal map waypoint's floor and retain at least the current flight altitude.</summary>
    public bool IntermediateWaypointIsMapCoordinate { get; init; }
    public float IntermediateWaypointRadius { get; init; } = 3;
    public TravelModePolicy Movement { get; init; } = new();
    public TravelArrivalPolicy Arrival { get; init; } = new();
    public bool FindApproachPoint { get; init; }
    public bool StopOnStall { get; init; }
    public GroundDestinationKind GroundKind { get; init; } = GroundDestinationKind.Raw;
    public bool HorizontalArrivalOnGroundFallback { get; init; }
    public Vector3? LandingPoint { get; init; }
    public Vector3? ApproachPoint { get; init; }
    public Vector3? RequiredArrivalPoint { get; init; }
    public float RequiredArrivalPointRadius { get; init; } = 1;
    public float LandingRadius { get; init; } = 5;
    public TravelLandingStrategy LandingStrategy { get; init; }
    public float LandingRecoveryRadius { get; init; } = 4;
    public float FlightHeight { get; init; } = 4;
    public float PathArrivalRadius { get; init; } = .5f;
    public bool DirectGroundApproach { get; init; }
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromMinutes(6);
    public TimeSpan PreparationTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan LandingTimeout { get; init; } = TimeSpan.FromSeconds(12);
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan NoPathTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int MaximumMountRequests { get; init; } = 2;
    public int MaximumReplans { get; init; } = 8;
}

/// <summary>Immutable observations, read by the host on Framework; no native objects are retained.</summary>
public readonly record struct TravelObservation(DateTime Now, Vector3 Position, FlightState Flight,
    bool MountedReady, bool Dismounted, bool CanFly, bool InsideNoFly = false,
    Vector3? NoFlyLandingPoint = null, bool MovementReady = true, bool TargetValid = true,
    Vector3? TargetPosition = null, ulong Character = 0, uint Territory = 0,
    bool NoFlyExempt = false);

public readonly record struct TravelJourneyUpdate(TravelJourneyState State, NavigationTravelOutcome Outcome,
    TravelMovementMode Movement, string Reason);

/// <summary>Owns mode selection, mount/takeoff, landing, dismount, final walk and strict arrival.</summary>
public sealed class TravelJourney : IDisposable
{
    private readonly TimeProvider clock;
    private readonly INavigationBackend backend;
    private readonly ITravelMovementControl movement;
    private readonly NavigationTravelSession travel;
    private readonly LandingSession landing;
    private readonly CombatLandingSession alternativeLanding;
    private bool alternativeLandingStarted;
    private readonly NoFlyTravelPolicy noFly = new();
    private TravelRequest? request;
    private Vector3 target, approach, routeTarget;
    private TravelMovementMode selected;
    private long started, phaseStarted;
    private long? settled;
    private Vector3 settleAnchor;
    private bool routeStarted, ownsMount, preparingTakeoff, intermediateReached;
    private int mountRequests, replans, cancelRequested;
    private ulong character;
    private uint territory;
    private bool noFlyExempt;
    private Vector3? intermediateDestination;
    private string reason = "尚未开始";
    public TravelJourneyState State { get; private set; }
    public bool IsMountPreparationActive => this.State == TravelJourneyState.Preparing && this.ownsMount;
    public TravelMovementMode Movement => this.selected;
    public NavigationFailureStage LastFailureStage { get; private set; }
    public Vector3? ApproachPoint => this.request is null ? null : this.approach;
    public bool IntermediateWaypointPending => this.request?.IntermediateWaypoint is not null && !this.intermediateReached
        && this.State is TravelJourneyState.Preparing or TravelJourneyState.Travelling;
    public bool IntermediateWaypointReached => this.intermediateReached;

    public TravelJourney(INavigationBackend backend, IDescentControl descent, ITravelMovementControl movement, TimeProvider? clock = null, bool externalRecovery = false)
    {
        this.clock = clock ?? TimeProvider.System;
        this.backend = backend; this.movement = movement;
        this.travel = new(backend, descent, externalRecovery ? null : movement, this.clock);
        this.landing = new(backend, descent);
        this.alternativeLanding = new(backend, descent);
    }

    public void Start(TravelRequest request, TravelObservation observation)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TravelGeometry.IsFinite(request.Destination) || request.IntermediateWaypoint is { } waypoint && !TravelGeometry.IsFinite(waypoint)
            || request.LandingPoint is { } p && !TravelGeometry.IsFinite(p)
            || request.ApproachPoint is { } a && !TravelGeometry.IsFinite(a)
            || request.RequiredArrivalPoint is { } end && !TravelGeometry.IsFinite(end)
            || !float.IsFinite(request.RequiredArrivalPointRadius) || request.RequiredArrivalPointRadius <= 0
            || !float.IsFinite(request.Arrival.Radius) || request.Arrival.Radius <= 0
            || float.IsNaN(request.Arrival.HeightTolerance) || request.Arrival.HeightTolerance < 0
            || !float.IsFinite(request.LandingRadius) || request.LandingRadius <= 0
            || !float.IsFinite(request.FlightHeight) || request.FlightHeight < 0
            || !float.IsFinite(request.PathArrivalRadius) || request.PathArrivalRadius <= 0
            || !float.IsFinite(request.IntermediateWaypointRadius) || request.IntermediateWaypointRadius <= 0
            || request.OperationTimeout <= TimeSpan.Zero || request.PreparationTimeout <= TimeSpan.Zero
            || request.LandingTimeout <= TimeSpan.Zero || request.Arrival.SettleDuration < TimeSpan.Zero
            || request.MaximumMountRequests <= 0 || request.MaximumReplans < 0)
            throw new ArgumentOutOfRangeException(nameof(request));
        this.Cancel();
        this.request = request;
        this.character = observation.Character; this.territory = observation.Territory;
        this.noFlyExempt = observation.NoFlyExempt;
        this.started = this.clock.GetTimestamp();
        this.mountRequests = this.replans = 0;
        this.LastFailureStage = NavigationFailureStage.None;
        Volatile.Write(ref this.cancelRequested, 0);
        this.target = request.Destination;
        this.intermediateReached = false;
        this.noFly.Begin(observation.Now, observation.InsideNoFly && !this.noFlyExempt);
        this.Plan(observation);
    }

    private void Plan(TravelObservation frame)
    {
        this.travel.Cancel(); this.landing.Reset(); this.alternativeLanding.Cancel(); this.alternativeLandingStarted = false; this.movement.ResetTakeoff();
        this.routeStarted = false; this.settled = null; this.preparingTakeoff = false;
        this.approach = this.request!.ApproachPoint ?? (this.request.FindApproachPoint
            ? TravelGeometry.FindApproachPoint(frame.Position, this.target, this.backend.FindNearestReachablePoint, this.request.Arrival) ?? this.target
            : this.target);
        this.selected = this.request.IntermediateWaypoint is not null
            ? TravelMovementMode.Fly : this.request.Movement.Select(frame.Position, this.target, frame.CanFly);
        this.Change(TravelJourneyState.Preparing, "准备前往目标");
    }

    public TravelJourneyUpdate Tick(TravelObservation frame)
    {
        if (this.request is null) return this.Update(NavigationTravelOutcome.None);
        if (Volatile.Read(ref this.cancelRequested) != 0) { this.Cancel(); return this.Update(NavigationTravelOutcome.Cancelled); }
        if (this.State is TravelJourneyState.Cancelled or TravelJourneyState.Failed)
            return this.Update(this.State == TravelJourneyState.Failed ? NavigationTravelOutcome.Failed : NavigationTravelOutcome.Cancelled);
        if (!frame.TargetValid || !TravelGeometry.IsFinite(frame.Position)
            || frame.Character != this.character || frame.Territory != this.territory)
            return this.Fail("目标、角色或区域已失效");
        if (this.clock.GetElapsedTime(this.started) >= this.request.OperationTimeout) return this.Fail("本次前往总时间预算已耗尽");
        if (frame.TargetPosition is { } supplied && !TravelGeometry.IsFinite(supplied)) return this.Fail("目标坐标无效");
        if (frame.TargetPosition is { } fresh && Vector3.DistanceSquared(fresh, this.target) > 1)
        {
            if (!TravelGeometry.IsFinite(fresh) || ++this.replans > this.request.MaximumReplans) return this.Fail("目标持续移动，重新规划次数已耗尽");
            this.target = fresh; this.Plan(frame);
        }
        if (this.State == TravelJourneyState.Arrived && this.ContainsGoal(frame.Position)
            && (!this.request.Arrival.RequireDismounted || frame.Dismounted)
            && (!(this.request.Arrival.RequireGrounded || this.request.Arrival.RequireDismounted) || !frame.Flight.InFlight)
            && !frame.Flight.Jumping && !frame.Flight.MountTransition) return this.Update(NavigationTravelOutcome.Arrived);
        if (!this.backend.IsAvailable) return this.Fail("导航后端不可用");
        if (!frame.MovementReady) { this.backend.Stop(); this.routeStarted = false; this.settled = null; return this.Update(NavigationTravelOutcome.Progress, "等待移动条件"); }

        this.noFly.Update(frame.Now, frame.InsideNoFly && !this.noFlyExempt,
            this.State == TravelJourneyState.Landing || this.travel.State == NavigationTravelState.Landing);
        bool intermediate = this.IntermediateWaypointPending;
        if (intermediate)
        {
            if (!frame.CanFly || this.noFly.GroundOnly) return this.Fail("离区中转要求飞行，但当前飞行条件不可用");
            if (this.intermediateDestination is null)
            {
                Vector3 waypoint = this.request.IntermediateWaypoint!.Value;
                if (this.request.IntermediateWaypointIsMapCoordinate)
                {
                    if (!this.backend.TryResolveTravelGroundDestination(waypoint, mapWaypoint: true, out var floor, out _)
                        || !TravelGeometry.IsFinite(floor))
                    {
                        if (this.clock.GetElapsedTime(this.phaseStarted) >= this.request.PreparationTimeout)
                            return this.Fail("无法解析离区中转点的地面高度");
                        return this.Update(NavigationTravelOutcome.Progress, "等待解析离区中转点的地面高度");
                    }
                    // Keep the configured X/Z authoritative; floor queries may snap sideways.
                    waypoint.Y = Math.Max(frame.Position.Y, floor.Y + Math.Max(4, this.request.FlightHeight));
                }
                this.intermediateDestination = waypoint;
            }
        }
        bool inRange = this.ContainsGoal(frame.Position);
        bool needsLand = this.request.Arrival.RequireGrounded || this.request.Arrival.RequireDismounted;
        Vector3 landingPoint = this.request.LandingPoint ?? this.approach;
        if (!intermediate && this.State is (TravelJourneyState.Preparing or TravelJourneyState.Travelling))
        {
            if (frame.Flight.InFlight && needsLand
                && TravelGeometry.HorizontalDistance(frame.Position, landingPoint) <= this.request.LandingRadius)
            {
                this.travel.Cancel(); this.routeStarted = false; this.landing.Reset();
                this.Change(TravelJourneyState.Landing, "已进入落地距离，停止长途导航");
            }
            else if (inRange && (!frame.Flight.InFlight || !needsLand))
            {
                this.travel.Cancel(); this.routeStarted = false;
                this.Change(TravelJourneyState.Dismounting, "已进入目标范围");
            }
        }

        if (this.State == TravelJourneyState.Landing)
        {
            if (this.clock.GetElapsedTime(this.phaseStarted) >= this.request.LandingTimeout) return this.Fail("落地时间预算已耗尽");
            if (this.request.LandingStrategy == TravelLandingStrategy.AroundTarget)
            {
                if (!this.alternativeLandingStarted)
                { this.alternativeLanding.Start(frame.Now, frame.Position, this.target, this.request.LandingRecoveryRadius); this.alternativeLandingStarted = true; }
                var attempt = this.alternativeLanding.Tick(frame.Now, frame.Position, frame.Flight.InFlight,
                    frame.InsideNoFly && !this.noFlyExempt);
                if (attempt.State == CombatLandingState.Failed) return this.Fail(attempt.Reason);
                if (attempt.State != CombatLandingState.Landed)
                { this.Pump(frame.Flight.InFlight); return this.Update(NavigationTravelOutcome.Progress, attempt.Reason); }
            }
            if (this.landing.Update(frame.Now, frame.Position, frame.Flight,
                    this.request.LandingStrategy == TravelLandingStrategy.ExactPoint ? landingPoint : null) != LandingStatus.Landed)
            { this.Pump(frame.Flight.InFlight); return this.Update(NavigationTravelOutcome.Progress, "正在落地并等待稳定"); }
            this.landing.Reset();
            this.Change(TravelJourneyState.Dismounting, "落地已稳定");
        }
        if (this.State == TravelJourneyState.Dismounting)
        {
            if (this.clock.GetElapsedTime(this.phaseStarted) >= this.request.PreparationTimeout) return this.Fail("下坐骑时间预算已耗尽");
            if (this.request.Arrival.RequireDismounted && !frame.Dismounted)
            { this.movement.TryDismount(); return this.Update(NavigationTravelOutcome.Progress, "等待实际下坐骑"); }
            this.Change(TravelJourneyState.FinalApproach, "核验最终到达范围");
        }
        if (this.State is TravelJourneyState.FinalApproach or TravelJourneyState.Settling or TravelJourneyState.Arrived)
        {
            if (frame.Flight.InFlight && needsLand)
            { this.travel.Cancel(); this.routeStarted = false; this.landing.Reset(); this.Change(TravelJourneyState.Landing, "再次进入飞行，重新落地"); return this.Update(NavigationTravelOutcome.Progress); }
            if (this.request.Arrival.RequireDismounted && !frame.Dismounted)
            { this.travel.Cancel(); this.routeStarted = false; this.Change(TravelJourneyState.Dismounting, "再次骑乘，等待下坐骑"); return this.Update(NavigationTravelOutcome.Progress); }
            if (inRange && !frame.Flight.Jumping && !frame.Flight.MountTransition)
            {
                this.travel.Cancel(); this.routeStarted = false;
                if (this.settled is null || Vector3.DistanceSquared(frame.Position, this.settleAnchor) > .01f)
                { this.settled = this.clock.GetTimestamp(); this.settleAnchor = frame.Position; }
                this.State = TravelJourneyState.Settling;
                if (this.clock.GetElapsedTime(this.settled.Value) >= this.request.Arrival.SettleDuration)
                { this.State = TravelJourneyState.Arrived; return this.Update(NavigationTravelOutcome.Arrived, "范围、高度、落地、坐骑及停稳条件已确认"); }
                return this.Update(NavigationTravelOutcome.Progress, "等待位置停稳");
            }
            this.settled = null;
            this.State = TravelJourneyState.FinalApproach;
            this.selected = TravelMovementMode.Foot;
            return this.Navigate(frame, this.approach, false, true);
        }

        bool fly = this.routeStarted ? this.travel.Mode == NavigationTravelMode.Fly : this.selected == TravelMovementMode.Fly;
        if (this.noFly.GroundOnly || !frame.CanFly) fly = false;
        else if (this.selected == TravelMovementMode.Fly && this.routeStarted
            && this.travel.GroundReason is NavigationGroundReason.NoFlyOrigin or NavigationGroundReason.NoFlyEntry)
            fly = true;
        if (!fly && frame.Flight.InFlight)
        {
            // Let the shared no-fly/recovery policy select its safe local landing point.
            return this.Navigate(frame, landingPoint, false, false);
        }
        if (this.selected == TravelMovementMode.Foot && !frame.Dismounted)
        {
            if (this.clock.GetElapsedTime(this.phaseStarted) >= this.request.PreparationTimeout) return this.Fail("步行准备超时");
            this.movement.TryDismount(); return this.Update(NavigationTravelOutcome.Progress, "步行前等待下坐骑");
        }
        if (this.selected != TravelMovementMode.Foot && !frame.MountedReady && !frame.Flight.InFlight)
        {
            if (this.State == TravelJourneyState.Travelling)
            { this.travel.Cancel(); this.routeStarted = false; this.movement.ResetTakeoff(); this.preparingTakeoff = false; this.Change(TravelJourneyState.Preparing, "骑乘状态变化，重新准备"); }
            if (this.clock.GetElapsedTime(this.phaseStarted) >= this.request.PreparationTimeout) return this.Fail("召唤坐骑超时");
            if (this.mountRequests < this.request.MaximumMountRequests)
                this.movement.TryMount(() => { this.ownsMount = true; this.mountRequests++; });
            return this.Update(NavigationTravelOutcome.Progress, "等待实际骑乘");
        }
        this.ownsMount = false;
        if (fly && !frame.Flight.InFlight)
        {
            if (!this.preparingTakeoff)
            {
                // A ground leg may have used most of the operation budget before leaving
                // a no-fly zone. Stop that leg and give takeoff its own bounded phase.
                this.travel.Cancel(); this.routeStarted = false;
                this.movement.ResetTakeoff(); this.preparingTakeoff = true;
                this.Change(TravelJourneyState.Preparing, "停止原路线，准备起飞");
            }
            if (this.clock.GetElapsedTime(this.phaseStarted) >= this.request.PreparationTimeout)
                return this.Fail($"起飞超时：{this.movement.TakeoffStatus}");
            this.movement.TryTakeoff(frame.Now);
            return this.Update(NavigationTravelOutcome.Progress, $"等待实际起飞：{this.movement.TakeoffStatus}");
        }
        this.preparingTakeoff = false;
        if (this.State == TravelJourneyState.Preparing) this.Change(TravelJourneyState.Travelling, "正在前往目标");
        if (this.selected == TravelMovementMode.Foot && this.request.DirectGroundApproach && !frame.Flight.InFlight)
        { this.State = TravelJourneyState.FinalApproach; return this.Navigate(frame, this.approach, false, true); }
        Vector3 route = intermediate ? this.intermediateDestination!.Value
            : fly ? landingPoint + Vector3.UnitY * this.request.FlightHeight : this.approach;
        return this.Navigate(frame, route, fly, false, intermediate);
    }

    private TravelJourneyUpdate Navigate(TravelObservation frame, Vector3 destination, bool fly, bool final, bool intermediate = false)
    {
        if (!this.routeStarted || Vector3.DistanceSquared(this.routeTarget, destination) > .01f)
        {
            this.travel.Cancel();
            if (this.backend is INavigationPump pump) pump.DirectGround = final && this.request!.DirectGroundApproach;
            this.routeTarget = destination;
            this.travel.Start(frame.Now, frame.Position, destination, frame.InsideNoFly, new()
            {
                GroundKind = this.request!.GroundKind,
                InitialIntent = fly ? NavigationTravelIntent.Fly : NavigationTravelIntent.Ground,
                CompleteAtResolvedDestination = false, AllowFlightRecovery = false, StopOnStall = intermediate || this.request!.StopOnStall,
                NoFlyLandingPoint = frame.NoFlyLandingPoint, LandingTimeout = this.request!.LandingTimeout,
                NoFlyExempt = this.noFlyExempt,
                StallTimeout = this.request.StallTimeout, NoPathTimeout = this.request.NoPathTimeout,
                OperationTimeout = this.request.OperationTimeout, MinimumProgressDisplacement = final ? .25f : 1.5f,
            });
            this.routeStarted = true;
        }
        bool arrived = Vector3.Distance(frame.Position, destination) <= (intermediate
            ? this.request!.IntermediateWaypointRadius : this.request!.PathArrivalRadius)
            && (!intermediate || frame.Flight.InFlight);
        var result = this.travel.Tick(new(frame.Now, frame.Position, Vector3.Distance(frame.Position, destination),
            arrived, frame.InsideNoFly && !this.noFlyExempt, frame.Flight, frame.NoFlyLandingPoint));
        if (result.Outcome == NavigationTravelOutcome.Failed) { this.LastFailureStage = result.FailureStage; return this.Fail(result.Reason); }
        if (result.Outcome == NavigationTravelOutcome.Arrived)
        {
            this.routeStarted = false;
            if (intermediate)
            {
                this.intermediateReached = true;
                this.Change(TravelJourneyState.Travelling, "已到达离区中转点，继续前往原目标");
                this.Pump(true);
                return this.Update(NavigationTravelOutcome.Progress, this.reason);
            }
            if (final && !this.ContainsGoal(frame.Position)) return this.Fail("路线结束但未满足实际目标范围");
            this.Change(fly && (this.request.Arrival.RequireGrounded || this.request.Arrival.RequireDismounted)
                ? TravelJourneyState.Landing : TravelJourneyState.Dismounting, "路线结束，核验交接条件");
        }
        this.Pump(true);
        return this.Update(NavigationTravelOutcome.Progress, result.Reason);
    }

    private bool ContainsGoal(Vector3 position) => (this.request!.IntermediateWaypoint is null || this.intermediateReached)
        && (this.request.Arrival.Contains(position, this.target)
        || this.request.HorizontalArrivalOnGroundFallback
            && (this.selected != TravelMovementMode.Fly || this.routeStarted && this.travel.Mode == NavigationTravelMode.Ground)
            && TravelGeometry.HorizontalDistance(position, this.target) <= this.request.Arrival.Radius)
        && (this.request.RequiredArrivalPoint is not { } point
            || TravelGeometry.HorizontalDistance(position, point) <= this.request.RequiredArrivalPointRadius
                && MathF.Abs(position.Y - point.Y) <= this.request.Arrival.HeightTolerance);
    private void Pump(bool ready) { if (this.backend is INavigationPump pump) pump.Pump(ready); }
    private void Change(TravelJourneyState state, string message) { this.State = state; this.phaseStarted = this.clock.GetTimestamp(); this.reason = message; }
    private TravelJourneyUpdate Update(NavigationTravelOutcome outcome, string? message = null)
    { if (message is not null) this.reason = message; return new(this.State, outcome, this.selected, this.reason); }
    private TravelJourneyUpdate Fail(string message) { this.Cancel(); this.State = TravelJourneyState.Failed; return this.Update(NavigationTravelOutcome.Failed, message); }
    /// <summary>May be called from UI. All game actions and path cleanup run on the next Framework tick.</summary>
    public void RequestCancel() => Volatile.Write(ref this.cancelRequested, 1);
    public void Cancel()
    {
        try { this.travel.Cancel(); }
        finally { this.landing.Reset(); this.alternativeLanding.Cancel(); this.alternativeLandingStarted = false; this.movement.ResetTakeoff(); }
        if (this.ownsMount) this.movement.CancelMountCast();
        this.ownsMount = false; this.routeStarted = false; this.settled = null; this.preparingTakeoff = false; this.intermediateReached = false;
        this.intermediateDestination = null;
        this.State = TravelJourneyState.Cancelled;
        this.reason = "导航已取消";
    }
    public void Dispose() => this.Cancel();
}
