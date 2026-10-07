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
    private readonly VnavmeshIpc travel;
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
        this.travel = new(plugins);
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
        && this.movePath.HasAction && this.stop.HasAction;
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

    public bool MoveTo(Vector3 destination, bool fly) => this.IsLoaded && this.MovementAllowed && !this.IsBusy
        && Finite(destination) && this.travel.MoveTo(destination, fly);
    public Vector3? FindNearestReachablePoint(Vector3 position, float halfExtentXZ = 20f, float halfExtentY = 100f) =>
        this.nearest.HasFunction ? this.nearest.InvokeFunc(position, halfExtentXZ, halfExtentY) : null;
    public bool TryResolveTravelGroundDestination(Vector3 requested, bool mapWaypoint, out Vector3 destination, out string resolution) =>
        this.travel.TryResolveTravelGroundDestination(requested, mapWaypoint, out destination, out resolution);
    public void StopPath() { if (this.stop.HasAction) this.stop.InvokeAction(); }
    public void Stop() => this.travel.Stop();
    private static bool Finite(Vector3 position) => float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);
}
