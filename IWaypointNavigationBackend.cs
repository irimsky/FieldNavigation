using System.Numerics;

namespace FieldNavigation;

/// <summary>
/// Optional backend for hosts that validate a calculated route before starting movement.
/// All IPC calls run on the framework thread; the returned task never moves the character.
/// </summary>
public interface IWaypointNavigationBackend : INavigationBackend
{
    bool IsLoaded { get; }
    bool IsReady { get; }
    bool IsBusy { get; }
    bool MovementAllowed { get; }
    Task<List<Vector3>> FindPath(Vector3 origin, Vector3 destination, bool fly, CancellationToken cancellation);
    void MovePath(List<Vector3> waypoints, bool fly);
    // Unlike best-effort Stop(), reports IPC failures to the host's cleanup policy.
    void StopPath();
}
