using System.Numerics;

namespace FieldNavigation;

/// <summary>Framework-thread-owned floor selection. Mesh queries only produce candidates;
/// route queries and the movement watchdog must still reject unusable destinations.</summary>
public sealed class AreaLandingPlanner(INavigationBackend navigation,
    Func<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>> findPath)
{
    private const int MaxRejectedDestinations = 6;
    private readonly Queue<Vector3> probes = new();
    private readonly List<Vector3> rejected = new();
    private readonly List<Vector3> actorProbes = new();
    private Task<List<Vector3>>? path;
    private CancellationTokenSource? cancellation;
    private Vector3 pendingFloor;
    private bool pendingFlight;
    private DateTime queryStartedAt;
    private DateTime searchStartedAt;
    private ulong? operationId;
    public Vector3? Floor { get; private set; }
    public bool IsActive => this.operationId is not null;
    public bool Exhausted { get; private set; }
    public string Status { get; private set; } = string.Empty;

    public Vector3? Update(DateTime now, ulong operationId, Vector3 center, float radius, Vector3 origin, bool fly, IEnumerable<Vector3> targetPositions)
        => this.Update(now, operationId, center, radius, origin, _ => fly, targetPositions);

    /// <summary>Selects the query mode for each resolved floor, including route-specific no-fly exemptions.</summary>
    public Vector3? Update(DateTime now, ulong operationId, Vector3 center, float radius, Vector3 origin,
        Func<Vector3, bool> flyToFloor, IEnumerable<Vector3> targetPositions)
    {
        if (this.operationId != operationId)
        {
            this.Reset();
            this.operationId = operationId;
            this.searchStartedAt = now;
            this.probes.Enqueue(center);
            // Favor the center, then expand inside the event. A live enemy supplies a
            // meaningful floor if the event marker lies inside scenery or on another level.
            foreach (float ring in new[] { 6f, 12f, 20f })
            {
                float offset = Math.Min(ring, radius * 0.8f);
                if (!float.IsFinite(offset) || offset < 2f)
                    continue;
                float startAngle = MathF.Atan2(origin.Z - center.Z, origin.X - center.X);
                for (int i = 0; i < 8; i++)
                {
                    float angle = startAngle + i * MathF.PI / 4f;
                    this.probes.Enqueue(center + new Vector3(MathF.Cos(angle) * offset, 0, MathF.Sin(angle) * offset));
                }
            }
        }

        // Actors can stream in after the search starts. Re-query their world positions
        // without retaining any game object across Framework ticks.
        foreach (Vector3 target in targetPositions.Take(8))
        {
            if (this.actorProbes.Count >= 8 || !IsFinite(target)
                || this.actorProbes.Any(point => Vector3.DistanceSquared(point, target) < 16f))
                continue;
            this.actorProbes.Add(target);
            this.probes.Enqueue(target);
        }

        if (this.Floor is not null || this.Exhausted)
            return this.Floor;
        if (this.searchStartedAt == DateTime.MinValue)
            this.searchStartedAt = now;
        if (this.rejected.Count >= MaxRejectedDestinations || now - this.searchStartedAt >= TimeSpan.FromSeconds(60))
        {
            this.CancelQuery();
            this.Exhausted = true;
            this.Status = "目标范围内落点选择/实际导航恢复预算已耗尽";
            return null;
        }

        if (this.path is { } query)
        {
            bool fly = flyToFloor(this.pendingFloor);
            if (this.pendingFlight != fly)
            {
                // A mode change invalidates the query, not the candidate. Retry it under
                // the current movement policy without consuming another destination.
                this.CancelQuery(retry: true);
                this.Status = "移动方式变化，重新检查候选落点路径";
                return null;
            }
            if (!query.IsCompleted && now - this.queryStartedAt < TimeSpan.FromSeconds(5))
            {
                this.Status = "正在异步检查候选落点路径";
                return null;
            }
            bool usable = false;
            if (query.IsCompletedSuccessfully)
            {
                List<Vector3> points = query.GetAwaiter().GetResult();
                Vector3 endpoint = Approach(this.pendingFloor, fly);
                usable = points.Count >= 2 && points.All(IsFinite)
                    && Vector3.DistanceSquared(points[^1], endpoint) <= 9f;
            }
            Vector3 checkedFloor = this.pendingFloor;
            this.CancelQuery();
            if (usable)
            {
                this.Floor = checkedFloor;
                this.Status = "候选地面及预查路径已通过，仍需实际抵达";
                return checkedFloor;
            }
        }

        // At most one candidate per tick; never run a blocking route search in Update.
        if (!this.probes.TryDequeue(out Vector3 probe))
        {
            this.Exhausted = true;
            this.Status = "目标范围内未找到有落脚空间且通过路径预查的地面";
            return null;
        }
        Vector3? candidate = navigation.FindNearestReachablePoint(probe, 1f, 5f);
        if (candidate is not { } floor || !IsFinite(floor)
            || LandingRecoveryPolicy.HorizontalDistance(floor, probe) > 0.75f
            || LandingRecoveryPolicy.HorizontalDistance(floor, center) > radius * 0.9f
            || this.rejected.Any(point => LandingRecoveryPolicy.HorizontalDistance(point, floor) < 4f)
            || !this.HasFooting(floor))
        {
            this.Status = "排除边缘、错误楼层或已失败落点，继续检查候选";
            return null;
        }

        bool candidateFlight = flyToFloor(floor);
        this.pendingFloor = floor;
        this.pendingFlight = candidateFlight;
        if (Vector3.DistanceSquared(origin, Approach(floor, candidateFlight)) <= 9f)
        {
            this.Floor = floor;
            this.Status = "当前位置已在候选落点接近范围内";
            return floor;
        }
        this.queryStartedAt = now;
        this.cancellation = new CancellationTokenSource();
        try
        {
            this.path = findPath(origin, Approach(floor, candidateFlight), candidateFlight, this.cancellation.Token);
            // No continuation touches host/game state; observe faults even after Stop/unload.
            _ = this.path.ContinueWith(static failed => { _ = failed.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            this.Status = "正在异步检查候选落点路径";
        }
        catch (Exception ex)
        {
            this.CancelQuery();
            this.Exhausted = true;
            this.Status = $"落点路径查询不可用：{ex.Message}";
        }
        return null;
    }

    public static Vector3 Approach(Vector3 floor, bool fly) => floor + (fly ? new Vector3(0, 4f, 0) : Vector3.Zero);

    public void RejectDestination(DateTime now)
    {
        if (this.Floor is { } floor)
            this.rejected.Add(floor);
        this.Floor = null;
        this.CancelQuery();
        // Rejected candidates consume the same operation budget.
    }

    public void CancelQuery(bool retry = false)
    {
        // A canceled pending probe is retried after pause/dependency recovery, rather than
        // letting a late result issue movement for an obsolete activity.
        if (retry && this.path is not null)
        {
            this.probes.Enqueue(this.pendingFloor);
        }
        this.cancellation?.Cancel();
        this.cancellation?.Dispose();
        this.cancellation = null;
        this.path = null;
    }

    public void Reset()
    {
        this.CancelQuery();
        this.operationId = null;
        this.probes.Clear();
        this.rejected.Clear();
        this.actorProbes.Clear();
        this.Floor = null;
        this.Exhausted = false;
        this.Status = string.Empty;
    }

    private bool HasFooting(Vector3 floor)
    {
        foreach (Vector3 direction in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ })
        {
            Vector3 probe = floor + direction * 1.5f;
            Vector3? point = navigation.FindNearestReachablePoint(probe, 0.5f, 3f);
            if (point is not { } resolved || !IsFinite(resolved)
                || LandingRecoveryPolicy.HorizontalDistance(probe, resolved) > 0.5f
                || Math.Abs(resolved.Y - floor.Y) > 2f)
                return false;
        }
        return true;
    }

    private static bool IsFinite(Vector3 point) =>
        float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
}
