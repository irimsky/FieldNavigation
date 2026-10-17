using System.Numerics;

namespace FieldNavigation;

/// <summary>One incremental, cancellable candidate search shared by all target kinds.</summary>
internal sealed class DestinationSearch(VnavmeshIpc navigation, NoFlyZoneCatalog zones, NavigateRequest request,
    Vector3 target, Vector3 origin, uint territory, IReadOnlyList<Vector3> rejected, TimeProvider clock) : IDisposable
{
    private readonly Queue<Vector3> probes = CreateProbes(request, target, origin);
    private CancellationTokenSource? cancellation;
    private Task<List<Vector3>>? query;
    private long queryStarted;
    private Vector3 pending;
    public Vector3? Result { get; private set; }
    public bool Exhausted { get; private set; }
    public string Message { get; private set; } = "选择目标范围内可达点";
    private static Queue<Vector3> CreateProbes(NavigateRequest request, Vector3 target, Vector3 origin)
    {
        var queue = new Queue<Vector3>(request.CandidatePositions.Where(TravelGeometry.IsFinite).Take(8));
        queue.Enqueue(target);
        float bearing = MathF.Atan2(origin.Z - target.Z, origin.X - target.X);
        foreach (float fraction in new[] { .8f, .5f, .2f })
            for (int i = 0; i < 8; i++)
            {
                float angle = bearing + i * MathF.PI / 4;
                queue.Enqueue(target + new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)) * (request.Arrival.Radius * fraction));
            }
        return queue;
    }
    public void Tick(Vector3 position, bool canFly, bool inFlight)
    {
        if (Result is not null || Exhausted) return;
        if (query is { } task)
        {
            if (!task.IsCompleted && clock.GetElapsedTime(queryStarted) < request.Recovery.PathfindTimeout) return;
            bool valid = task.IsCompletedSuccessfully && task.Result.Count > 0
                && task.Result.All(TravelGeometry.IsFinite) && Vector3.Distance(task.Result[^1], pending) <= 3;
            CancelQuery();
            if (valid) { Result = pending; Message = "候选点通过路径检查"; return; }
        }
        // A locally cancelled query may still be draining in vnavmesh. Do not
        // pile candidate queries behind it, or overwrite another caller's path.
        if (navigation.Raw.IsMoveActive)
        { Message = "等待导航后端完成已有查询或路径"; return; }
        if (!probes.TryDequeue(out var probe)) { Exhausted = true; Message = "原目标范围内没有可达候选点"; return; }
        Vector3 floor;
        bool airborneGoal = !request.Arrival.RequireGrounded && !request.Arrival.RequireDismounted;
        if (airborneGoal) floor = probe;
        else if (request.GroundKind == GroundDestinationKind.MapWaypoint)
        {
            if (!navigation.TryResolveTravelGroundDestination(probe, true, out floor, out _)) return;
        }
        else
        {
            var nearest = navigation.FindNearestReachablePoint(probe, .8f, Math.Min(5, request.Arrival.HeightTolerance));
            if (nearest is not { } resolved) return;
            floor = resolved;
        }
        if (!TravelGeometry.IsFinite(floor) || !request.Arrival.Policy.Contains(floor, target)
            || rejected.Any(point => Vector3.Distance(point, floor) < Math.Min(2, request.Arrival.Radius * .3f))) return;
        bool fly = request.Movement.AllowFlight && request.Movement.AllowMount && canFly
            && request.Movement.Mode is not TravelMovementMode.Foot and not TravelMovementMode.GroundMount;
        bool exempt = request.IgnoreNoFlyZones || zones.IsRouteExempt(territory, origin, floor);
        if (!exempt && (zones.Find(territory, position) is not null || zones.Find(territory, floor) is not null)) fly = false;
        if (Vector3.Distance(position, floor) < .5f) { Result = floor; return; }
        // Query the actual landing floor, not a point on an unrelated upper storey.
        pending = floor;
        cancellation = new(); queryStarted = clock.GetTimestamp();
        Vector3 queryOrigin = position;
        if (!fly && inFlight)
        {
            if (!navigation.TryResolveTravelGroundDestination(position, true, out queryOrigin, out _)) { CancelQuery(); return; }
        }
        try { query = navigation.FindPath(queryOrigin, pending, fly, cancellation.Token); }
        catch { CancelQuery(); }
    }
    private void CancelQuery()
    {
        var task = query; query = null;
        if (task is not null) _ = task.ContinueWith(static t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var source = cancellation; cancellation = null;
        if (source is not null) { try { source.Cancel(); } finally { source.Dispose(); } }
    }
    public void Dispose() => CancelQuery();
}
