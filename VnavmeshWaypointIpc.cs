using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace FieldNavigation;

/// <summary>
/// Typed, cancellable path calculation and explicit waypoint execution. This preserves a
/// host's arrival, flight, target, and ownership rules without using SimpleMove's late execution.
/// </summary>
public sealed class VnavmeshWaypointIpc : IWaypointNavigationBackend
{
    private readonly IDalamudPluginInterface plugins;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> pointOnFloor;
    private readonly ICallGateSubscriber<bool> ready;
    private readonly ICallGateSubscriber<bool> running;
    private readonly ICallGateSubscriber<bool> calculating;
    private readonly ICallGateSubscriber<bool> simpleCalculating;
    private readonly ICallGateSubscriber<bool> movementAllowed;
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>> findPath;
    private readonly ICallGateSubscriber<List<Vector3>, bool, object> movePath;
    private readonly ICallGateSubscriber<object> stop;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> nearest;

    public VnavmeshWaypointIpc(IDalamudPluginInterface plugins)
    {
        this.plugins = plugins;
        this.pointOnFloor = plugins.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        this.ready = plugins.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        this.running = plugins.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        this.calculating = plugins.GetIpcSubscriber<bool>("vnavmesh.Nav.PathfindInProgress");
        this.simpleCalculating = plugins.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        this.movementAllowed = plugins.GetIpcSubscriber<bool>("vnavmesh.Path.GetMovementAllowed");
        this.findPath = plugins.GetIpcSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>>(
            "vnavmesh.Nav.PathfindCancelable");
        this.movePath = plugins.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");
        this.stop = plugins.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        this.nearest = plugins.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable");
    }

    public bool IsLoaded => this.plugins.InstalledPlugins.Any(p => p.InternalName == "vnavmesh" && p.IsLoaded)
        && this.ready.HasFunction && this.running.HasFunction && this.calculating.HasFunction
        && this.simpleCalculating.HasFunction && this.movementAllowed.HasFunction && this.findPath.HasFunction
        && this.movePath.HasAction && this.stop.HasAction && this.nearest.HasFunction && this.pointOnFloor.HasFunction;
    public bool IsAvailable => this.IsLoaded && this.IsReady;
    public bool IsReady => this.ready.InvokeFunc();
    public bool IsPathRunning => this.running.InvokeFunc();
    public bool IsMoveInProgress => this.calculating.InvokeFunc() || this.simpleCalculating.InvokeFunc();
    public bool IsMoveActive => this.IsPathRunning || this.IsMoveInProgress;
    public bool IsBusy => this.IsMoveActive;
    public bool MovementAllowed => this.movementAllowed.InvokeFunc();

    public Task<List<Vector3>> FindPath(Vector3 origin, Vector3 destination, bool fly, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!Finite(origin) || !Finite(destination)) throw new ArgumentException("Navigation coordinates must be finite.");
        return this.findPath.InvokeFunc(origin, destination, fly, cancellation);
    }

    public void MovePath(List<Vector3> waypoints, bool fly)
    {
        if (waypoints.Count == 0 || waypoints.Any(p => !Finite(p)))
            throw new ArgumentException("Navigation requires a nonempty finite waypoint path.", nameof(waypoints));
        if (!this.IsLoaded || !this.MovementAllowed || this.IsBusy)
            throw new InvalidOperationException("Navigation backend is unavailable, busy, or movement is disabled.");
        // Deliberately does not require a mesh: a host may supply a direct ground waypoint.
        this.movePath.InvokeAction(waypoints.ToList(), fly);
    }

    // Raw IPC cannot own a pending request; use WaypointTravelBackend.
    public bool MoveTo(Vector3 destination, bool fly) => false;
    public Vector3? FindNearestReachablePoint(Vector3 position, float halfExtentXZ = 20f, float halfExtentY = 100f) =>
        this.nearest.HasFunction ? this.nearest.InvokeFunc(position, halfExtentXZ, halfExtentY) : null;
    /// <summary>Resolve a map waypoint like moveflag, or keep a loaded NPC on its own floor.</summary>
    public bool TryResolveTravelGroundDestination(
        Vector3 requested,
        bool mapWaypoint,
        out Vector3 destination,
        out string resolution)
    {
        destination = default;
        resolution = string.Empty;
        if (mapWaypoint)
        {
            try
            {
                // MapUtil.FlagToPoint uses these exact probe coordinates and query defaults.
                // Use our waypoint's X/Z without reading or changing the player's map flag.
                Vector3 probe = new(requested.X, 1024f, requested.Z);
                if (this.pointOnFloor.InvokeFunc(probe, true, 5f) is { } floor && IsFinite(floor))
                {
                    destination = floor;
                    resolution = "PointOnFloor（moveflag）";
                    return true;
                }

                resolution = "PointOnFloor 未找到地面；";
            }
            catch (Exception ex)
            {
                resolution = $"PointOnFloor 调用失败：{ex.Message}；";
            }
        }

        try
        {
            // A live opener has a meaningful height; do not snap it onto a roof above the NPC.
            float halfExtentXZ = mapWaypoint ? 30f : 5f;
            float halfExtentY = mapWaypoint ? 100f : 5f;
            if (this.nearest.InvokeFunc(requested, halfExtentXZ, halfExtentY) is { } nearest
                && IsFinite(nearest))
            {
                destination = nearest;
                resolution += "NearestPointReachable";
                return true;
            }

            resolution += "NearestPointReachable 未找到地面";
        }
        catch (Exception ex)
        {
            resolution += $"NearestPointReachable 调用失败：{ex.Message}";
        }

        return false;
    }

    private static bool IsFinite(Vector3 position) =>
        float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);

    public void StopPath() { if (this.stop.HasAction) this.stop.InvokeAction(); }
    public void Stop() => this.StopPath();
    private static bool Finite(Vector3 position) => float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);
}
