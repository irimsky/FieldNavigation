using System.Numerics;

namespace FieldNavigation;

/// <summary>Calls are made on the host's framework thread. No game object is retained.</summary>
public interface INavigationBackend
{
    bool IsAvailable { get; }
    bool IsMoveInProgress { get; }
    bool IsPathRunning { get; }
    bool IsMoveActive { get; }
    bool MoveTo(Vector3 destination, bool fly);
    void Stop();
    Vector3? FindNearestReachablePoint(Vector3 position, float halfExtentXZ = 20f, float halfExtentY = 100f);
    bool TryResolveTravelGroundDestination(Vector3 requested, bool mapWaypoint, out Vector3 destination, out string resolution);
}

public enum GroundDestinationKind
{
    /// <summary>The host has already supplied a usable world position.</summary>
    Raw,
    /// <summary>Height is unknown; query the floor from above, like a map flag.</summary>
    MapWaypoint,
    /// <summary>Height belongs to a live actor; resolve locally without snapping to a roof.</summary>
    LiveObject,
}

public interface IDescentControl
{
    void BeginDescending();
    void StopDescending();
}

public readonly record struct FlightState(bool InFlight, bool Jumping, bool MountTransition);
