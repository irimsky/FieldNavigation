using System.Numerics;

namespace FieldNavigation;

/// <summary>Executes shared travel requests using cancellable, calculation-only waypoint IPC.</summary>
public sealed class WaypointTravelBackend(IWaypointNavigationBackend backend, Func<Vector3?> position) : INavigationBackend
{
    private CancellationTokenSource? cancellation;
    private Task<List<Vector3>>? query;
    private bool ownsPath;
    private bool flying;
    private bool directGround;
    public bool DirectGround { get => this.directGround; set => this.directGround = value; }
    public bool IsAvailable => backend.IsLoaded && (this.directGround || backend.IsReady);
    public bool IsMoveInProgress => this.query is not null;
    public bool IsPathRunning => this.ownsPath && backend.IsPathRunning;
    public bool IsMoveActive => this.IsMoveInProgress || this.IsPathRunning;

    public bool MoveTo(Vector3 destination, bool fly)
    {
        if (!this.IsAvailable || this.IsMoveActive || backend.IsBusy || !backend.MovementAllowed
            || position() is not { } origin)
            return false;
        this.flying = fly;
        if (this.directGround && !fly)
        {
            this.ownsPath = true;
            backend.MovePath([destination], false);
        }
        else
        {
            this.cancellation = new();
            this.query = backend.FindPath(origin, destination, fly, this.cancellation.Token);
        }
        return true;
    }

    /// <summary>Only the active host ticks results. Cancelled calculations can never start a path.</summary>
    public void Pump(bool movementReady)
    {
        if (this.query is not { IsCompleted: true } completed)
            return;
        if (!completed.IsCompletedSuccessfully)
        {
            this.ReleaseQuery();
            _ = completed.Exception;
            throw new InvalidOperationException("FieldNavigation 路径计算失败或已取消。", completed.Exception);
        }
        if (!movementReady)
            return;
        var path = completed.GetAwaiter().GetResult();
        this.ReleaseQuery();
        if (path.Count == 0 || path.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)))
            throw new InvalidOperationException("FieldNavigation 未找到有效路线。");
        if (backend.IsBusy || !backend.MovementAllowed)
            throw new InvalidOperationException("FieldNavigation 路径执行前移动被占用或禁止。");
        this.ownsPath = true;
        backend.MovePath(path, this.flying);
    }

    public void Stop()
    {
        this.ReleaseQuery();
        if (!this.ownsPath)
            return;
        backend.StopPath();
        this.ownsPath = false;
    }

    public void CancelCalculation() => this.ReleaseQuery();

    private void ReleaseQuery()
    {
        if (this.query is { } pending)
            _ = pending.ContinueWith(static t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        this.query = null;
        this.cancellation?.Cancel();
        this.cancellation?.Dispose();
        this.cancellation = null;
    }

    public Vector3? FindNearestReachablePoint(Vector3 point, float halfExtentXZ = 20f, float halfExtentY = 100f) =>
        backend.FindNearestReachablePoint(point, halfExtentXZ, halfExtentY);
    public bool TryResolveTravelGroundDestination(Vector3 requested, bool mapWaypoint, out Vector3 destination, out string resolution) =>
        backend.TryResolveTravelGroundDestination(requested, mapWaypoint, out destination, out resolution);
}
