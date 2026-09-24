using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace FieldNavigation;

/// <summary>
/// Thin, typed wrapper over the IPC names exported by the checked-in vnavmesh source.
/// </summary>
public sealed class VnavmeshIpc : INavigationBackend
{
    private readonly ICallGateSubscriber<bool> isReady;
    private readonly ICallGateSubscriber<Vector3, bool, bool> moveTo;
    private readonly ICallGateSubscriber<bool> moveInProgress;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> nearestReachablePoint;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> pointOnFloor;
    private readonly ICallGateSubscriber<object> stopPath;

    public VnavmeshIpc(IDalamudPluginInterface pluginInterface)
    {
        this.isReady = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        this.moveTo = pluginInterface.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        this.moveInProgress = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        this.pathIsRunning = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        this.nearestReachablePoint = pluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable");
        this.pointOnFloor = pluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        this.stopPath = pluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
    }

    public bool IsAvailable
    {
        get
        {
            try
            {
                return this.isReady.InvokeFunc();
            }
            catch
            {
                return false;
            }
        }
    }

    public bool MoveTo(Vector3 destination, bool fly)
    {
        try
        {
            return this.moveTo.InvokeFunc(destination, fly);
        }
        catch
        {
            return false;
        }
    }

    public bool IsMoveInProgress
    {
        get
        {
            try
            {
                return this.moveInProgress.InvokeFunc();
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Gets whether vnavmesh is currently executing a computed waypoint path.
    /// SimpleMove.PathfindInProgress only covers the path calculation task;
    /// Path.IsRunning covers the subsequent movement execution phase.
    /// </summary>
    public bool IsPathRunning
    {
        get
        {
            try
            {
                return this.pathIsRunning.InvokeFunc();
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>True while either path calculation or waypoint execution is active.</summary>
    public bool IsMoveActive => this.IsMoveInProgress || this.IsPathRunning;

    public Vector3? FindNearestReachablePoint(Vector3 position, float halfExtentXZ = 20f, float halfExtentY = 100f)
    {
        try
        {
            Vector3? result = this.nearestReachablePoint.InvokeFunc(position, halfExtentXZ, halfExtentY);
            return result is { } value && IsFinite(value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

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
            if (this.nearestReachablePoint.InvokeFunc(requested, halfExtentXZ, halfExtentY) is { } nearest
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

    public void Stop()
    {
        try
        {
            // vnavmesh's public IPC can stop the executor but not its SimpleMove calculation.
            // The navigation coordinator therefore keeps stopping stale results and does not
            // submit the replacement operation until both calculation and execution are idle.
            this.stopPath.InvokeAction();
        }
        catch
        {
            // vnavmesh may be unloaded while the host is unloading.
        }
    }
}
