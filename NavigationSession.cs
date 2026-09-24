using System.Numerics;

namespace FieldNavigation;

public enum NavigationRejection { None, Unavailable, InvalidDestination, GroundResolution, BackendRejected }

/// <summary>
/// Owns one request generation and its watchdog. Hosts own target selection and arrival policy.
/// PumpCancellation must run on framework ticks even while a host is paused.
/// </summary>
public sealed class NavigationSession<TPurpose>(INavigationBackend backend) where TPurpose : struct, Enum
{
    private bool cancellationPending;
    public NavigationOperation<TPurpose> Operation { get; } = new();
    public NavigationProgressMonitor Progress { get; } = new();
    public string LastResolution { get; private set; } = string.Empty;
    public NavigationRejection LastRejection { get; private set; }

    public NavigationRequestResult Request(DateTime now, TPurpose purpose, Vector3 destination, bool fly,
        Vector3 origin, GroundDestinationKind groundKind = GroundDestinationKind.Raw,
        bool groundAllowed = true, bool horizontalProgress = false)
    {
        if (!fly && !groundAllowed)
            return NavigationRequestResult.NotDue;

        bool hadOperation = this.Operation.IsActive;
        bool changed = this.Operation.Begin(purpose, destination, fly, groundKind: groundKind);
        if (changed)
        {
            this.Progress.Reset();
            this.LastRejection = NavigationRejection.None;
            this.LastResolution = string.Empty;
            if (hadOperation || backend.IsMoveActive)
                backend.Stop();
        }

        this.PumpCancellation();
        // Path.Stop cannot cancel SimpleMove's calculation. Wait for its late result and stop it.
        if (!this.Operation.RequestIssued && backend.IsMoveActive)
        {
            backend.Stop();
            return NavigationRequestResult.NotDue;
        }
        if (this.Operation.RequestIssued || now < this.Operation.NextRequestAt)
            return NavigationRequestResult.NotDue;

        if (!IsFinite(destination))
            return this.Reject(now, NavigationRejection.InvalidDestination, "目标坐标无效");
        if (!backend.IsAvailable)
            return this.Reject(now, NavigationRejection.Unavailable, "vnavmesh 尚未就绪");

        Vector3 resolved = destination;
        string resolution = "原始坐标";
        if (!fly && groundKind != GroundDestinationKind.Raw
            && !backend.TryResolveTravelGroundDestination(destination, groundKind == GroundDestinationKind.MapWaypoint,
                out resolved, out resolution))
            return this.Reject(now, NavigationRejection.GroundResolution, resolution);
        if (!IsFinite(resolved))
            return this.Reject(now, NavigationRejection.GroundResolution, "地面解析返回无效坐标");
        if (!backend.MoveTo(resolved, fly))
            return this.Reject(now, NavigationRejection.BackendRejected, "vnavmesh 拒绝导航请求");

        this.LastResolution = resolution;
        this.LastRejection = NavigationRejection.None;
        this.Operation.MarkRequestIssued(now, fly, resolved);
        this.Progress.Start(now, origin, NavigationProgressMonitor.Distance(origin, destination, horizontalProgress));
        return NavigationRequestResult.Accepted;
    }

    public NavigationProgressResult CheckProgress(DateTime now, Vector3 position, Vector3 destination,
        float distance, bool horizontal, TimeSpan stuckTimeout, TimeSpan? noPathTimeout = null) =>
        this.Progress.Update(now, position, destination, distance, horizontal, this.Operation.RequestIssued,
            this.Operation.CommandStartedAt, backend.IsMoveInProgress, backend.IsPathRunning, stuckTimeout,
            noPathTimeout);

    public void Cancel()
    {
        this.cancellationPending |= backend.IsMoveInProgress;
        backend.Stop();
        this.Operation.Reset();
        this.Progress.Reset();
        this.LastRejection = NavigationRejection.None;
        this.LastResolution = string.Empty;
    }

    public void PumpCancellation()
    {
        if (!this.cancellationPending)
            return;
        backend.Stop();
        if (!backend.IsMoveActive)
            this.cancellationPending = false;
    }

    private NavigationRequestResult Reject(DateTime now, NavigationRejection rejection, string reason)
    {
        this.Operation.MarkRequestRejected(now);
        this.LastRejection = rejection;
        this.LastResolution = reason;
        return NavigationRequestResult.Rejected;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
