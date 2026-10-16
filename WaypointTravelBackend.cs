using System.Numerics;
using System.Diagnostics;

namespace FieldNavigation;

/// <summary>Framework-owned calculation-only requests followed by explicit path execution.</summary>
public sealed class WaypointTravelBackend(IWaypointNavigationBackend backend, Func<Vector3?> position, TimeProvider? timeProvider = null) : INavigationBackend, INavigationPump, INavigationFailureInfo
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private CancellationTokenSource? cancellation;
    private Task<List<Vector3>>? query;
    private long queryStarted;
    private Vector3 queryOrigin, queryDestination;
    private bool ownsPath, flying;
    public bool DirectGround { get; set; }
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public float MaximumPathLength { get; set; } = float.PositiveInfinity;
    public float MaximumStartDrift { get; set; } = 3;
    public string LastFailure { get; private set; } = "";
    public bool IsAvailable => backend.IsLoaded && (this.DirectGround || backend.IsReady);
    public bool IsMoveInProgress => this.query is not null;
    public bool IsPathRunning => this.ownsPath && (backend is IOwnedWaypointNavigationBackend { SupportsOwnedPaths: true } owned
        ? owned.IsOwnedPathRunning : backend.IsPathRunning);
    public bool IsMoveActive => this.IsMoveInProgress || this.IsPathRunning;

    public bool MoveTo(Vector3 destination, bool fly)
    {
        if (!TravelGeometry.IsFinite(destination)) { this.LastFailure = "目标坐标无效"; return false; }
        if (!this.IsAvailable) { this.LastFailure = "vnavmesh 后端未就绪"; return false; }
        if (this.IsMoveActive) { this.LastFailure = "已有 FN 路径或算路正在进行"; return false; }
        if (backend.IsBusy) { this.LastFailure = "vnavmesh 后端正在被其他路径占用"; return false; }
        if (!backend.MovementAllowed) { this.LastFailure = "vnavmesh 当前禁止移动"; return false; }
        if (position() is not { } origin || !TravelGeometry.IsFinite(origin)) { this.LastFailure = "角色位置无效"; return false; }
        this.ownsPath = false;
        this.flying = fly; this.queryOrigin = origin; this.queryDestination = destination; this.LastFailure = "";
        if (this.DirectGround && !fly)
        {
            if (Vector3.Distance(origin, destination) > this.MaximumPathLength) { this.LastFailure = "直达路径超过长度限制"; return false; }
            this.ownsPath = true;
            backend.MovePath([destination], false);
        }
        else
        {
            this.cancellation = new(); this.queryStarted = this.clock.GetTimestamp();
            try { this.query = backend.FindPath(origin, destination, fly, this.cancellation.Token); }
            catch { this.ReleaseQuery(); throw; }
        }
        return true;
    }

    /// <summary>Call after identity/zone/stop checks; no task continuation starts movement.</summary>
    public void Pump(bool movementReady)
    {
        if (this.ownsPath && !this.IsPathRunning)
        {
            this.ownsPath = false;
            if (backend.IsPathRunning) throw new InvalidOperationException("导航移动已由其他调用者接管。");
        }
        if (this.query is not { } pending) return;
        if (this.clock.GetElapsedTime(this.queryStarted) >= this.QueryTimeout)
        { this.LastFailure = "路径计算超时"; this.ReleaseQuery(); return; }
        if (!pending.IsCompleted) return;
        if (!pending.IsCompletedSuccessfully)
        { this.LastFailure = "路径计算失败或已取消"; this.ReleaseQuery(); return; }
        if (!movementReady) return;
        List<Vector3> path = pending.GetAwaiter().GetResult();
        this.ReleaseQuery();
        if (position() is not { } current || !TravelGeometry.IsFinite(current)
            || Vector3.Distance(current, this.queryOrigin) > this.MaximumStartDrift)
        { this.LastFailure = "算路期间起点发生偏移"; return; }
        float length = path.Count == 0 ? float.PositiveInfinity : Vector3.Distance(current, path[0]);
        for (int i = 1; i < path.Count; i++) length += Vector3.Distance(path[i - 1], path[i]);
        if (path.Count == 0 || Vector3.Distance(path[^1], this.queryDestination) > 3 || path.Any(p => !TravelGeometry.IsFinite(p)) || !float.IsFinite(length) || length > this.MaximumPathLength)
        { this.LastFailure = "路线为空、无效或绕行距离超过限制"; return; }
        if (!this.IsAvailable || backend.IsBusy || !backend.MovementAllowed)
        { this.LastFailure = "执行路线前后端被占用或不可用"; return; }
        this.ownsPath = true;
        backend.MovePath(path, this.flying);
    }

    public void Stop()
    {
        this.ReleaseQuery();
        if (!this.ownsPath) return;
        // Retain ownership when Stop throws so Framework cleanup can retry.
        if (backend.IsPathRunning) backend.StopPath();
        this.ownsPath = false;
    }
    public void CancelCalculation() => this.ReleaseQuery();
    private void ReleaseQuery()
    {
        if (this.query is { } pending)
            _ = pending.ContinueWith(static t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        this.query = null;
        var source = this.cancellation; this.cancellation = null;
        if (source is not null) { try { source.Cancel(); } finally { source.Dispose(); } }
    }
    public Vector3? FindNearestReachablePoint(Vector3 point, float halfExtentXZ = 20f, float halfExtentY = 100f) =>
        backend.FindNearestReachablePoint(point, halfExtentXZ, halfExtentY);
    public bool TryResolveTravelGroundDestination(Vector3 requested, bool mapWaypoint, out Vector3 destination, out string resolution) =>
        backend.TryResolveTravelGroundDestination(requested, mapWaypoint, out destination, out resolution);
}
