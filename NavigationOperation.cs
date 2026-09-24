using System.Numerics;

namespace FieldNavigation;

public enum NavigationRequestResult
{
    NotDue,
    Accepted,
    Rejected,
}

/// <summary>
/// Per-operation state for the navigation coordinator. A new destination, mode, or purpose
/// starts a new generation; requests from an older generation must never be reused.
/// </summary>
public sealed class NavigationOperation<TPurpose> where TPurpose : struct, Enum
{
    public TPurpose Purpose { get; private set; }

    public Vector3 Destination { get; private set; }

    /// <summary>The actual IPC endpoint, separate from the business destination used by Begin.</summary>
    public Vector3? ResolvedDestination { get; private set; }

    public bool Fly { get; private set; }

    public int Generation { get; private set; }

    public bool RequestIssued { get; private set; }

    public DateTime NextRequestAt { get; private set; } = DateTime.MinValue;

    public DateTime CommandStartedAt { get; private set; } = DateTime.MinValue;

    public bool IsActive { get; private set; }

    public GroundDestinationKind GroundKind { get; private set; }

    public bool Begin(TPurpose purpose, Vector3 destination, bool fly, float threshold = 1f,
        GroundDestinationKind groundKind = GroundDestinationKind.Raw)
    {
        bool changed = !this.IsActive
            || !EqualityComparer<TPurpose>.Default.Equals(this.Purpose, purpose)
            || this.Fly != fly
            || this.GroundKind != groundKind
            || Vector3.DistanceSquared(this.Destination, destination) > threshold * threshold;
        if (!changed)
            return false;

        this.IsActive = true;
        this.Purpose = purpose;
        this.GroundKind = groundKind;
        this.Destination = destination;
        this.ResolvedDestination = null;
        this.Fly = fly;
        this.Generation++;
        this.RequestIssued = false;
        this.NextRequestAt = DateTime.MinValue;
        this.CommandStartedAt = DateTime.MinValue;
        return true;
    }

    public void MarkRequestIssued(DateTime now, bool fly, Vector3? resolvedDestination = null)
    {
        this.Fly = fly;
        this.ResolvedDestination = resolvedDestination ?? this.Destination;
        this.RequestIssued = true;
        this.NextRequestAt = now.AddMilliseconds(750);
        this.CommandStartedAt = now;
    }

    public void MarkRequestRejected(DateTime now)
    {
        this.ResolvedDestination = null;
        this.RequestIssued = false;
        this.NextRequestAt = now.AddMilliseconds(750);
        this.CommandStartedAt = DateTime.MinValue;
    }

    public void ResetRequestGate()
    {
        this.ResolvedDestination = null;
        this.RequestIssued = false;
        this.NextRequestAt = DateTime.MinValue;
        this.CommandStartedAt = DateTime.MinValue;
    }

    public void Reset()
    {
        if (!this.IsActive
            && !this.RequestIssued
            && this.NextRequestAt == DateTime.MinValue
            && this.CommandStartedAt == DateTime.MinValue)
            return;

        this.IsActive = false;
        this.Purpose = default;
        this.GroundKind = default;
        this.Destination = Vector3.Zero;
        this.Fly = false;
        this.Generation++;
        this.ResetRequestGate();
    }
}
