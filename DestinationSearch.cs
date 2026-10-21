using System.Numerics;

namespace FieldNavigation;

internal readonly record struct DestinationSelection(Vector3 Point, bool ForceGround);

/// <summary>One incremental, cancellable candidate search shared by all target kinds.</summary>
internal sealed class DestinationSearch(VnavmeshIpc navigation, NoFlyZoneCatalog zones, NavigateRequest request,
    Vector3 target, Vector3 origin, uint territory, IReadOnlyList<Vector3> rejected, TimeProvider clock) : IDisposable
{
    private readonly Queue<Vector3> probes = CreateProbes(request, target, origin);
    private CancellationTokenSource? cancellation;
    private Task<List<Vector3>>? query;
    private long queryStarted;
    private Vector3 pending;
    private bool pendingForceGround;
    private (Vector3 Point, bool ForceGround)? groundFallback;
    public DestinationSelection? Result { get; private set; }
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
            if (valid) { Result = new(pending, pendingForceGround); Message = "候选点通过路径检查"; return; }
        }
        // A locally cancelled query may still be draining in vnavmesh. Do not
        // pile candidate queries behind it, or overwrite another caller's path.
        if (navigation.Raw.IsMoveActive)
        { Message = "等待导航后端完成已有查询或路径"; return; }
        while (true)
        {
            if (groundFallback is { } fallback)
            {
                groundFallback = null;
                Message = "空中候选不可达，正在检查对应地面点";
                if (IsCandidateAcceptable(fallback.Point)
                    && StartQuery(position, fallback.Point, fly: false, forceGround: fallback.ForceGround, inFlight)) return;
                continue;
            }
            if (!probes.TryDequeue(out var probe))
            {
                Exhausted = true; Message = "原目标范围内没有可达候选点"; return;
            }
            if (TryStartProbe(probe, position, canFly, inFlight)) return;
        }
    }
    private bool TryStartProbe(Vector3 probe, Vector3 position, bool canFly, bool inFlight)
    {
        bool fly = request.Movement.AllowFlight && request.Movement.AllowMount && canFly
            && request.Movement.Mode is not TravelMovementMode.Foot and not TravelMovementMode.GroundMount;
        bool airborneGoal = !request.Arrival.RequireGrounded && !request.Arrival.RequireDismounted;
        Vector3 floor;
        groundFallback = null;
        if (airborneGoal)
        {
            // 禁飞区或当前无法飞行时，空中目标必须降级到它下方的地面点：地面寻路
            // 打向半空中的点会直接返回空路径，候选搜索就会把所有刷新点判成不可达。
            bool hasGround = navigation.TryResolveTravelGroundDestination(probe, true, out Vector3 groundFloor, out _);
            if (IsBlocked(position, hasGround ? groundFloor : probe, fly ? probe + Vector3.UnitY * request.FlightHeight : null)) fly = false;
            bool probeOk = IsCandidateAcceptable(probe);
            bool groundOk = hasGround && IsCandidateAcceptable(groundFloor);
            if (fly && probeOk)
            {
                floor = probe;
                if (groundOk && Vector3.Distance(groundFloor, probe) > .01f)
                    groundFallback = (groundFloor, true);
            }
            else if (groundOk)
            {
                floor = groundFloor;
                Message = !probeOk
                    ? "空中候选不可达，已改用对应地面点"
                    : "禁飞区或无法飞行：空中目标已降级为地面点";
                return StartQuery(position, floor, fly: false, forceGround: !probeOk, inFlight);
            }
            else
            {
                Message = "禁飞区或无法飞行，且目标下方没有可解析的地面点";
                return false;
            }
        }
        else if (request.GroundKind == GroundDestinationKind.MapWaypoint)
        {
            if (!navigation.TryResolveTravelGroundDestination(probe, true, out floor, out _)
                || !IsCandidateAcceptable(floor)) return false;
            if (IsBlocked(position, floor, fly ? floor + Vector3.UnitY * request.FlightHeight : null)) fly = false;
        }
        else
        {
            var nearest = navigation.FindNearestReachablePoint(probe, .8f, Math.Min(5, request.Arrival.HeightTolerance));
            if (nearest is not { } resolved || !IsCandidateAcceptable(resolved)) return false;
            floor = resolved;
            if (IsBlocked(position, floor, fly ? floor + Vector3.UnitY * request.FlightHeight : null)) fly = false;
        }
        return StartQuery(position, floor, fly, forceGround: false, inFlight);
    }
    private bool IsBlocked(Vector3 position, Vector3 point, Vector3? flightDestination) => !request.IgnoreNoFlyZones
        && !zones.IsRouteExempt(territory, origin, flightDestination ?? point)
        && (zones.Find(territory, position, NoFlyZone.BoundaryMargin) is not null
            || zones.Find(territory, point, NoFlyZone.BoundaryMargin) is not null
            || flightDestination is { } flight
                && zones.FindRouteBlocking(territory, position, flight, NoFlyZone.BoundaryMargin) is not null);
    private bool IsCandidateAcceptable(Vector3 floor) => TravelGeometry.IsFinite(floor)
        && request.Arrival.Policy.Contains(floor, target)
        && !rejected.Any(point => Vector3.Distance(point, floor) < Math.Min(2, request.Arrival.Radius * .3f));
    private bool StartQuery(Vector3 position, Vector3 destination, bool fly, bool forceGround, bool inFlight)
    {
        // Query the actual landing floor, not a point on an unrelated upper storey.
        pending = destination;
        pendingForceGround = forceGround;
        cancellation = new(); queryStarted = clock.GetTimestamp();
        Vector3 queryOrigin = position;
        if (!fly && inFlight)
        {
            if (!navigation.TryResolveTravelGroundDestination(position, true, out queryOrigin, out _))
            { CancelQuery(); return false; }
        }
        try { query = navigation.FindPath(queryOrigin, destination, fly, cancellation.Token); return true; }
        catch { CancelQuery(); return false; }
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
