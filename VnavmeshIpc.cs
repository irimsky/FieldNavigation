using System.Numerics;
using Dalamud.Plugin;

namespace FieldNavigation;

/// <summary>Compatibility facade over shared cancellable path execution. Pump on Framework after stop checks.</summary>
public sealed class VnavmeshIpc : INavigationBackend, INavigationPump, INavigationFailureInfo
{
    public VnavmeshWaypointIpc Raw { get; }
    public WaypointTravelBackend Paths { get; }
    public VnavmeshIpc(IDalamudPluginInterface plugin, Func<Vector3?> position)
    { this.Raw = new(plugin); this.Paths = new(this.Raw, position); }
    public string LastFailure => this.Paths.LastFailure;
    public bool IsAvailable { get { try { return this.Paths.IsAvailable; } catch { return false; } } }
    public bool IsMoveInProgress => this.Paths.IsMoveInProgress;
    public bool IsPathRunning => this.Paths.IsPathRunning;
    public bool IsMoveActive => this.Paths.IsMoveActive;
    public bool DirectGround { get => this.Paths.DirectGround; set => this.Paths.DirectGround = value; }
    public bool MoveTo(Vector3 destination, bool fly) => this.Paths.MoveTo(destination, fly);
    public void Stop() => this.Paths.Stop();
    public void Pump(bool movementReady) => this.Paths.Pump(movementReady);
    public Vector3? FindNearestReachablePoint(Vector3 position, float halfExtentXZ = 20, float halfExtentY = 100)
    { try { return this.Paths.FindNearestReachablePoint(position, halfExtentXZ, halfExtentY); } catch { return null; } }
    public Task<List<Vector3>> FindPath(Vector3 origin, Vector3 destination, bool fly, CancellationToken cancellation) =>
        this.Raw.FindPath(origin, destination, fly, cancellation);
    public bool TryResolveTravelGroundDestination(Vector3 requested, bool mapWaypoint, out Vector3 destination, out string resolution) =>
        this.Paths.TryResolveTravelGroundDestination(requested, mapWaypoint, out destination, out resolution);
}
