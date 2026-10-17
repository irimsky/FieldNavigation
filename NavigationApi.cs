using System.Numerics;

namespace FieldNavigation;

public enum OperationStatus { Running, Cancelling, Succeeded, Failed, Cancelled }
public enum OperationReason { None, BackendUnavailable, NoPath, PathfindTimeout, MovementStalled, LandingFailed, GoalNotSatisfied, TransferNotConfirmed, BudgetExceeded, ContextChanged, MovementConflict, CancelledByHost, InvalidRequest }
public enum RecoveryMode { None, Ground, Airborne }
public enum TransferKind { Aetheryte, Aethernet }

public sealed record ArrivalOptions
{
    public float Radius { get; init; } = 5;
    public float HeightTolerance { get; init; } = 2;
    public bool Use3DDistance { get; init; } = true;
    public bool RequireGrounded { get; init; } = true;
    public bool RequireDismounted { get; init; } = true;
    public TimeSpan SettleDuration { get; init; } = TimeSpan.FromMilliseconds(300);
    internal TravelArrivalPolicy Policy => new() { Radius = Radius, HeightTolerance = HeightTolerance, Use3DDistance = Use3DDistance,
        RequireGrounded = RequireGrounded, RequireDismounted = RequireDismounted, SettleDuration = SettleDuration };
}
public sealed record MovementOptions
{
    public TravelMovementMode Mode { get; init; } = TravelMovementMode.Automatic;
    public bool AllowMount { get; init; } = true;
    /// <summary>Allows an active travel operation to continue moving during combat. It never permits starting a mount, takeoff or combat jump recovery.</summary>
    public bool AllowDuringCombat { get; init; }
    public bool AllowFlight { get; init; } = true;
    public float WalkDistance { get; init; } = 20;
    public float GroundMountDistance { get; init; } = 25;
}
public sealed record RecoveryOptions
{
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromMinutes(6);
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan PathfindTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaximumRecoveries { get; init; } = 3;
    public bool AllowJump { get; init; } = true;
    public bool AllowRelocation { get; init; } = true;
    public bool AllowGroundFallback { get; init; } = true;
}
public sealed record NavigateRequest
{
    public required Vector3 Target { get; init; }
    public ArrivalOptions Arrival { get; init; } = new();
    public MovementOptions Movement { get; init; } = new();
    public RecoveryOptions Recovery { get; init; } = new();
    public bool IgnoreNoFlyZones { get; init; }
    public float LandingRadius { get; init; } = 5;
    public float FlightHeight { get; init; } = 4;
    public GroundDestinationKind GroundKind { get; init; } = GroundDestinationKind.LiveObject;
    /// <summary>Pure coordinate hints, not native objects. Every point still satisfies Arrival.</summary>
    public IReadOnlyList<Vector3> CandidatePositions { get; init; } = [];
}
public sealed record TransferRequest
{
    public TransferKind Kind { get; init; }
    public required uint DestinationId { get; init; }
    public required uint TerritoryId { get; init; }
    public byte SubIndex { get; init; }
    public Vector3? ArrivalPosition { get; init; }
    public float ArrivalRadius { get; init; } = 15;
    public bool ArrivalUse3DDistance { get; init; }
    public bool ForceTransfer { get; init; }
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public int MaximumAttempts { get; init; } = 3;
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan SettleDuration { get; init; } = TimeSpan.FromSeconds(1);
}
public sealed record RelocateRequest
{
    public Vector3? AwayFrom { get; init; }
    public float Distance { get; init; } = 4;
    public float MinimumDisplacement { get; init; } = 2;
    public float MaximumDisplacement { get; init; } = 8;
    public float MaximumPathLength { get; init; } = 12;
    public bool IgnoreNoFlyZones { get; init; }
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan SettleDuration { get; init; } = TimeSpan.FromMilliseconds(300);
}
public sealed record OperationSnapshot(Guid OperationId, OperationStatus Status, string Phase, OperationReason Reason,
    string Message, int Attempts, int Recoveries, TimeSpan Elapsed, Vector3? ResolvedDestination, RecoveryMode RecoveryMode)
{
    public bool IsTerminal => Status is OperationStatus.Succeeded or OperationStatus.Failed or OperationStatus.Cancelled;
}

/// <summary>Managed operation handle. Only Cancel and Snapshot are intended for UI access.</summary>
public sealed class FnOperation
{
    private OperationSnapshot snapshot;
    private int cancelled;
    private Vector3? pendingTarget;
    internal FnOperation() => snapshot = new(Guid.NewGuid(), OperationStatus.Running, "Pending", OperationReason.None, "等待 Framework 推进", 0, 0, TimeSpan.Zero, null, RecoveryMode.None);
    public OperationSnapshot Snapshot => Volatile.Read(ref snapshot);
    public event Action<OperationSnapshot>? Completed;
    public void Cancel() => Interlocked.Exchange(ref cancelled, 1);
    public void UpdateTarget(Vector3 position)
    {
        if (!TravelGeometry.IsFinite(position)) throw new ArgumentOutOfRangeException(nameof(position));
        lock (this) pendingTarget = position;
    }
    internal bool CancellationRequested => Volatile.Read(ref cancelled) != 0;
    internal Vector3? TakeTarget() { lock (this) { var target = pendingTarget; pendingTarget = null; return target; } }
    internal void Publish(OperationSnapshot value) => Volatile.Write(ref snapshot, value);
    internal void Notify(Action<Exception> log)
    {
        if (Completed is not { } handlers) return;
        foreach (Action<OperationSnapshot> handler in handlers.GetInvocationList())
            try { handler(Snapshot); } catch (Exception ex) { log(ex); }
    }
}
