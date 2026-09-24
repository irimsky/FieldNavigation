using System.Numerics;

namespace FieldNavigation;

public enum NavigationTravelIntent
{
    Ground,
    Fly,
}

public enum NavigationTravelMode
{
    Ground,
    Fly,
}

public enum NavigationTravelState
{
    Idle,
    Navigating,
    Landing,
    JumpRecovery,
    Completed,
    Failed,
    Cancelled,
}

public enum NavigationGroundReason
{
    None,
    InitialGround,
    NoFlyOrigin,
    NoFlyEntry,
    FlightStall,
    JumpRecovery,
    FlightRecovery,
}

public enum NavigationFailureStage
{
    None,
    BackendRejected,
    GroundResolution,
    LandingTimeout,
    FlightStall,
    GroundStall,
    JumpRecovery,
    FlightRecovery,
}

public enum NavigationTravelOutcome
{
    None,
    Progress,
    Retrying,
    Arrived,
    Failed,
    Cancelled,
}

public sealed class NavigationTravelOptions
{
    public NavigationTravelIntent InitialIntent { get; init; }
    public GroundDestinationKind GroundKind { get; init; } = GroundDestinationKind.Raw;
    public bool HorizontalProgress { get; init; }
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan NoPathTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan LandingTimeout { get; init; } = TimeSpan.FromSeconds(18);
    public bool AllowFlightRecovery { get; init; } = true;
}

/// <summary>All host-owned observations needed to advance one travel session.</summary>
public readonly record struct NavigationTravelFrame(
    DateTime Now,
    Vector3 Position,
    float DistanceToDestination,
    bool Arrived,
    bool InsideNoFly,
    FlightState Flight);

/// <summary>One result from a navigation session tick. Hosts map terminal results to business states.</summary>
public readonly record struct NavigationTravelUpdate(
    NavigationTravelState State,
    NavigationTravelOutcome Outcome,
    NavigationTravelMode Mode,
    NavigationGroundReason GroundReason,
    NavigationFailureStage FailureStage,
    LandingStatus LandingStatus,
    NavigationRequestResult RequestResult,
    NavigationRejection Rejection,
    string Reason);

/// <summary>Optional host action used by the shared jump recovery stage.</summary>
public interface ITravelRecoveryControl
{
    bool TryJump();
}

/// <summary>
/// Reusable state machine for one world navigation operation. It owns navigation safety and
/// recovery; the consumer owns target selection, preemption rules, arrival thresholds, and what
/// to do after Failed.
/// </summary>
public sealed class NavigationTravelSession : IDisposable
{
    private enum TravelPurpose { Travel }

    private readonly NavigationSession<TravelPurpose> navigation;
    private readonly LandingSession landing;
    private readonly ITravelRecoveryControl? recovery;
    private readonly NoFlyTravelPolicy noFly = new();

    private NavigationTravelOptions options = new();
    private NavigationTravelState state;
    private NavigationTravelMode mode;
    private NavigationGroundReason groundReason;
    private NavigationFailureStage failureStage;
    private NavigationRejection failureRejection;
    private NavigationTravelIntent initialIntent;
    private Vector3 destination;
    private Vector3 requestOrigin;
    private DateTime landingStartedAt;
    private bool jumpAttempted;
    private bool flightRecoveryAttempted;
    private bool disposed;

    public NavigationTravelSession(
        INavigationBackend backend,
        IDescentControl descent,
        ITravelRecoveryControl? recovery = null)
    {
        this.navigation = new NavigationSession<TravelPurpose>(backend);
        this.landing = new LandingSession(backend, descent);
        this.recovery = recovery;
    }

    public NavigationTravelState State => this.state;
    public NavigationTravelMode Mode => this.mode;
    public NavigationTravelIntent InitialIntent => this.initialIntent;
    public NavigationGroundReason GroundReason => this.groundReason;
    public NavigationFailureStage FailureStage => this.failureStage;
    public NavigationRejection FailureRejection => this.failureRejection;
    public GroundDestinationKind GroundKind => this.navigation.Operation.GroundKind;
    public Vector3? ResolvedDestination => this.navigation.Operation.ResolvedDestination;
    public DateTime RequestStartedAt => this.navigation.Operation.CommandStartedAt;
    public LandingSession Landing => this.landing;
    public bool IsActive => this.state is NavigationTravelState.Navigating
        or NavigationTravelState.Landing
        or NavigationTravelState.JumpRecovery;
    public bool CanResumeFlight => this.initialIntent == NavigationTravelIntent.Fly
        && this.IsNoFlyGroundReason()
        && !this.noFly.GroundOnly
        && !this.flightRecoveryAttempted;

    public NavigationTravelUpdate Start(
        DateTime now,
        Vector3 origin,
        Vector3 destination,
        bool insideNoFly,
        NavigationTravelOptions options)
    {
        this.ThrowIfDisposed();
        this.Cancel();
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.initialIntent = options.InitialIntent;
        this.destination = destination;
        this.requestOrigin = origin;
        this.failureStage = NavigationFailureStage.None;
        this.failureRejection = NavigationRejection.None;
        this.jumpAttempted = false;
        this.flightRecoveryAttempted = false;
        this.landingStartedAt = DateTime.MinValue;
        this.noFly.Begin(now, insideNoFly);
        this.mode = insideNoFly ? NavigationTravelMode.Ground : ToMode(options.InitialIntent);
        this.groundReason = insideNoFly
            ? NavigationGroundReason.NoFlyOrigin
            : options.InitialIntent == NavigationTravelIntent.Ground
                ? NavigationGroundReason.InitialGround
                : NavigationGroundReason.None;
        this.state = NavigationTravelState.Navigating;
        return this.Update(
            NavigationTravelOutcome.Progress,
            NavigationRequestResult.NotDue,
            NavigationRejection.None,
            LandingStatus.Descending,
            insideNoFly ? "导航起点位于禁飞区，锁定地面模式" : "导航会话已开始");
    }

    public NavigationTravelUpdate Tick(NavigationTravelFrame frame)
    {
        this.ThrowIfDisposed();
        if (this.state == NavigationTravelState.Idle)
            return this.Update(NavigationTravelOutcome.None, NavigationRequestResult.NotDue,
                NavigationRejection.None, LandingStatus.Descending, "导航会话尚未开始");
        if (this.state == NavigationTravelState.Completed)
            return this.Update(NavigationTravelOutcome.Arrived, NavigationRequestResult.NotDue,
                NavigationRejection.None, LandingStatus.Landed, "导航已完成");
        if (this.state == NavigationTravelState.Failed)
            return this.Update(NavigationTravelOutcome.Failed, NavigationRequestResult.NotDue,
                this.failureRejection, LandingStatus.Descending, "导航已失败");
        if (this.state == NavigationTravelState.Cancelled)
            return this.Update(NavigationTravelOutcome.Cancelled, NavigationRequestResult.NotDue,
                NavigationRejection.None, LandingStatus.Descending, "导航已取消");

        this.noFly.Update(frame.Now, frame.InsideNoFly, this.state == NavigationTravelState.Landing);
        if (this.state == NavigationTravelState.Landing)
            return this.TickLanding(frame);
        if (this.state == NavigationTravelState.JumpRecovery)
            return this.TickJumpRecovery(frame);

        if (this.mode == NavigationTravelMode.Fly && this.noFly.GroundOnly)
        {
            this.groundReason = this.groundReason == NavigationGroundReason.None
                ? NavigationGroundReason.NoFlyEntry
                : this.groundReason;
            this.mode = NavigationTravelMode.Ground;
            this.requestOrigin = frame.Position;
            this.navigation.Cancel();
            if (frame.Flight.InFlight)
                return this.BeginLanding(frame, "进入禁飞区，停止飞行并开始落地");
        }
        else if (this.mode == NavigationTravelMode.Ground
                 && this.initialIntent == NavigationTravelIntent.Fly
                 && this.IsNoFlyGroundReason()
                 && !this.noFly.GroundOnly
                 && !frame.Flight.InFlight)
        {
            this.mode = NavigationTravelMode.Fly;
            this.requestOrigin = frame.Position;
            this.navigation.Cancel();
        }

        if (this.mode == NavigationTravelMode.Ground && frame.Flight.InFlight)
            return this.BeginLanding(frame, "地面导航要求先完成落地");

        if (frame.Arrived)
        {
            this.navigation.Cancel();
            this.landing.Reset();
            this.state = NavigationTravelState.Completed;
            return this.Update(NavigationTravelOutcome.Arrived, NavigationRequestResult.NotDue,
                NavigationRejection.None, LandingStatus.Landed, "已到达导航目标");
        }

        if (this.navigation.Operation.RequestIssued)
        {
            NavigationProgressResult progress = this.navigation.CheckProgress(
                frame.Now,
                frame.Position,
                this.destination,
                frame.DistanceToDestination,
                this.options.HorizontalProgress,
                this.options.StallTimeout,
                this.options.NoPathTimeout);
            if (progress.Status == NavigationProgressStatus.Stalled)
                return this.HandleStall(frame, progress.Reason);
            return this.Update(
                NavigationTravelOutcome.Progress,
                NavigationRequestResult.NotDue,
                NavigationRejection.None,
                LandingStatus.Descending,
                progress.Reason.Length == 0 ? "导航进行中" : progress.Reason);
        }

        NavigationRequestResult request = this.navigation.Request(
            frame.Now,
            TravelPurpose.Travel,
            this.destination,
            this.mode == NavigationTravelMode.Fly,
            this.requestOrigin,
            this.options.GroundKind,
            groundAllowed: !frame.Flight.InFlight,
            this.options.HorizontalProgress);
        return request switch
        {
            NavigationRequestResult.Accepted => this.Update(
                NavigationTravelOutcome.Progress,
                request,
                NavigationRejection.None,
                LandingStatus.Descending,
                $"已发起{(this.mode == NavigationTravelMode.Fly ? "飞行" : "地面")}导航"),
            NavigationRequestResult.Rejected => this.Update(
                NavigationTravelOutcome.Retrying,
                request,
                this.navigation.LastRejection,
                LandingStatus.Descending,
                $"导航请求失败，等待重试：{this.navigation.LastResolution}"),
            _ => this.Update(
                NavigationTravelOutcome.Progress,
                request,
                NavigationRejection.None,
                LandingStatus.Descending,
                "等待导航请求窗口"),
        };
    }

    public void Cancel()
    {
        if (this.disposed)
            return;
        this.navigation.Cancel();
        this.landing.Reset();
        this.noFly.Reset();
        this.state = NavigationTravelState.Cancelled;
        this.mode = NavigationTravelMode.Ground;
        this.groundReason = NavigationGroundReason.None;
        this.failureStage = NavigationFailureStage.None;
        this.failureRejection = NavigationRejection.None;
    }

    public void Dispose()
    {
        if (this.disposed)
            return;
        this.Cancel();
        this.disposed = true;
    }

    private NavigationTravelUpdate TickLanding(NavigationTravelFrame frame)
    {
        if (frame.Now - this.landingStartedAt > this.options.LandingTimeout)
            return this.BeginJump(frame, "落地超时，进入跳跃恢复");

        LandingStatus status = this.landing.Update(frame.Now, frame.Position, frame.Flight);
        if (status == LandingStatus.Landed)
        {
            // Landing owns the boundary debounce while descent is active. Re-evaluate once
            // with landing=false after stability is confirmed so an already completed one-second
            // exit debounce can immediately restore the original flight intent on this tick.
            this.noFly.Update(frame.Now, frame.InsideNoFly, landing: false);
            this.state = NavigationTravelState.Navigating;
            bool resumeFlight = this.initialIntent == NavigationTravelIntent.Fly
                && this.IsNoFlyGroundReason()
                && !this.noFly.GroundOnly
                && !frame.Flight.InFlight;
            this.mode = resumeFlight ? NavigationTravelMode.Fly : NavigationTravelMode.Ground;
            this.requestOrigin = frame.Position;
            this.navigation.Cancel();
            return this.Update(NavigationTravelOutcome.Progress, NavigationRequestResult.NotDue,
                NavigationRejection.None,
                status,
                resumeFlight ? "落地稳定且已离开禁飞区，恢复原始飞行导航" : "落地稳定，继续地面导航");
        }

        return this.Update(NavigationTravelOutcome.Progress, NavigationRequestResult.NotDue,
            NavigationRejection.None, status, status switch
            {
                LandingStatus.WaitingForPath => "等待旧导航路径结束",
                LandingStatus.Approaching => "前往可达落地点",
                LandingStatus.Descending => "正在下降",
                _ => "等待落地稳定",
            });
    }

    private NavigationTravelUpdate TickJumpRecovery(NavigationTravelFrame frame)
    {
        if (frame.Flight.InFlight)
            return this.BeginLanding(frame, "跳跃恢复期间意外进入飞行，强制落地");
        if (frame.Flight.Jumping || frame.Now - this.landingStartedAt < TimeSpan.FromMilliseconds(500))
            return this.Update(NavigationTravelOutcome.Progress, NavigationRequestResult.NotDue,
                NavigationRejection.None, LandingStatus.Settling, "等待跳跃恢复结束");

        this.state = NavigationTravelState.Navigating;
        this.mode = this.mode == NavigationTravelMode.Fly
            ? NavigationTravelMode.Fly
            : NavigationTravelMode.Ground;
        this.requestOrigin = frame.Position;
        this.navigation.Cancel();
        return this.Update(NavigationTravelOutcome.Progress, NavigationRequestResult.NotDue,
            NavigationRejection.None, LandingStatus.Landed, "跳跃恢复结束，重新发起导航");
    }

    private bool IsNoFlyGroundReason() => this.groundReason is
        NavigationGroundReason.NoFlyOrigin or NavigationGroundReason.NoFlyEntry;

    private NavigationTravelUpdate BeginLanding(NavigationTravelFrame frame, string reason)
    {
        this.state = NavigationTravelState.Landing;
        this.landingStartedAt = frame.Now;
        this.navigation.Cancel();
        this.landing.Reset();
        return this.TickLanding(frame) with { Reason = reason };
    }

    private NavigationTravelUpdate HandleStall(NavigationTravelFrame frame, string reason)
    {
        this.navigation.Cancel();
        if (this.mode == NavigationTravelMode.Fly)
        {
            if (this.flightRecoveryAttempted)
                return this.Fail(NavigationFailureStage.FlightRecovery, $"飞行恢复后仍无有效位移：{reason}");

            this.failureStage = NavigationFailureStage.FlightStall;
            this.groundReason = NavigationGroundReason.FlightStall;
            this.mode = NavigationTravelMode.Ground;
            this.requestOrigin = frame.Position;
            return frame.Flight.InFlight
                ? this.BeginLanding(frame, $"飞行导航 5 秒无有效位移：{reason}")
                : this.Update(NavigationTravelOutcome.Retrying, NavigationRequestResult.NotDue,
                    NavigationRejection.None, LandingStatus.Landed, $"飞行无位移，切换地面导航：{reason}");
        }

        this.failureStage = NavigationFailureStage.GroundStall;
        if (!this.jumpAttempted)
            return this.BeginJump(frame, $"地面导航 5 秒无有效位移：{reason}");

        if (this.initialIntent == NavigationTravelIntent.Ground && this.options.AllowFlightRecovery
            && !this.flightRecoveryAttempted)
        {
            this.flightRecoveryAttempted = true;
            this.failureStage = NavigationFailureStage.FlightRecovery;
            this.groundReason = NavigationGroundReason.FlightRecovery;
            this.mode = NavigationTravelMode.Fly;
            this.requestOrigin = frame.Position;
            return this.Update(NavigationTravelOutcome.Retrying, NavigationRequestResult.NotDue,
                NavigationRejection.None, LandingStatus.Landed, "跳跃恢复后仍无位移，尝试一次飞行导航");
        }

        return this.Fail(NavigationFailureStage.GroundStall, $"地面导航恢复失败：{reason}");
    }

    private NavigationTravelUpdate BeginJump(NavigationTravelFrame frame, string reason)
    {
        if (this.jumpAttempted)
            return this.Fail(NavigationFailureStage.JumpRecovery, reason);

        this.jumpAttempted = true;
        this.failureStage = NavigationFailureStage.JumpRecovery;
        this.state = NavigationTravelState.JumpRecovery;
        this.landingStartedAt = frame.Now;
        this.landing.Reset();
        this.navigation.Cancel();
        if (this.recovery is null || !this.recovery.TryJump())
            return this.Fail(NavigationFailureStage.JumpRecovery, "跳跃恢复请求失败");
        return this.Update(NavigationTravelOutcome.Retrying, NavigationRequestResult.NotDue,
            NavigationRejection.None, LandingStatus.Settling, reason);
    }

    private NavigationTravelUpdate Fail(NavigationFailureStage stage, string reason)
    {
        this.failureStage = stage;
        this.failureRejection = this.navigation.LastRejection;
        this.state = NavigationTravelState.Failed;
        this.navigation.Cancel();
        this.landing.Reset();
        return this.Update(NavigationTravelOutcome.Failed, NavigationRequestResult.Rejected,
            this.failureRejection, LandingStatus.Descending, reason);
    }

    private NavigationTravelUpdate Update(
        NavigationTravelOutcome outcome,
        NavigationRequestResult request,
        NavigationRejection rejection,
        LandingStatus landingStatus,
        string reason) =>
        new(this.state, outcome, this.mode, this.groundReason, this.failureStage,
            landingStatus, request, rejection, reason);

    private static NavigationTravelMode ToMode(NavigationTravelIntent intent) => intent switch
    {
        NavigationTravelIntent.Fly => NavigationTravelMode.Fly,
        _ => NavigationTravelMode.Ground,
    };

    private void ThrowIfDisposed()
    {
        if (this.disposed)
            throw new ObjectDisposedException(nameof(NavigationTravelSession));
    }
}
