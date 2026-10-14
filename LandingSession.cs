using System.Numerics;

namespace FieldNavigation;

public enum LandingStatus { WaitingForPath, Approaching, Descending, Settling, Landed }

/// <summary>Releases old navigation, optionally approaches a nearby floor, descends, then settles.</summary>
public sealed class LandingSession(INavigationBackend backend, IDescentControl descent)
{
    private const float InteriorProbeRadius = 1f;
    private const float InteriorProbeMaxSnap = 0.75f;
    private bool approachIssued;
    private bool pointAttempted;
    private DateTime? settledSince;
    private Vector3? lastGroundedPosition;
    private Vector3? preferredDestination;
    private const float GroundedMovementThresholdSquared = 0.25f;
    public Vector3? Destination { get; private set; }
    public DateTime? SettledSince => this.settledSince;
    public TimeSpan SettleDuration { get; init; } = TimeSpan.FromMilliseconds(750);
    /// <summary>
    /// Preferred horizontal clearance from a mesh edge when the landing point was found
    /// from an airborne position. The original vnavmesh query only guarantees reachability,
    /// so a point on the border can otherwise be returned.
    /// </summary>
    public float InteriorOffset { get; set; } = 3f;

    public LandingStatus Update(DateTime now, Vector3? position, FlightState state, Vector3? landingPoint = null)
    {
        // Exact world points already identify the intended layer.
        if (!this.pointAttempted && landingPoint is { } exact && position is { } start && state.InFlight)
        {
            if (!IsFinite(exact)) return LandingStatus.WaitingForPath;
            if (backend.IsMoveActive) { backend.Stop(); return LandingStatus.WaitingForPath; }
            if (Vector3.DistanceSquared(start, exact) <= 9f) this.pointAttempted = true;
            else if (backend.MoveTo(exact, true))
            {
                this.pointAttempted = this.approachIssued = true;
                this.Destination = exact;
                return LandingStatus.Approaching;
            }
            else return LandingStatus.WaitingForPath;
        }
        if (!state.InFlight)
        {
            descent.StopDescending();
            if (backend.IsMoveActive)
            {
                backend.Stop();
                this.settledSince = null;
                return LandingStatus.WaitingForPath;
            }
            // Jumping and mount-transition conditions can remain asserted briefly after
            // the flight condition clears. Use actual position movement to interrupt a
            // settle window; a stale condition alone must not keep landing pending forever.
            bool movedWhileGrounded = position is { } current
                && this.lastGroundedPosition is { } previous
                && Vector3.DistanceSquared(current, previous) > GroundedMovementThresholdSquared;
            if (position is { } groundedPosition)
                this.lastGroundedPosition = groundedPosition;
            if (movedWhileGrounded || (position is null && state.Jumping))
                this.settledSince = null;
            else
                this.settledSince ??= now;
            return this.settledSince is { } since && now - since >= this.SettleDuration
                ? LandingStatus.Landed : LandingStatus.Settling;
        }

        this.settledSince = null;
        this.lastGroundedPosition = null;
        if (!this.approachIssued && backend.IsMoveActive)
        {
            backend.Stop();
            descent.StopDescending();
            return LandingStatus.WaitingForPath;
        }
        if (!this.pointAttempted && position is { } origin)
        {
            if (this.preferredDestination is { } configured)
            {
                if (!IsFinite(configured))
                    return LandingStatus.WaitingForPath;
                if (!backend.TryResolveTravelGroundDestination(
                        configured, mapWaypoint: true, out Vector3 resolved, out _)
                    || !IsFinite(resolved))
                    return LandingStatus.WaitingForPath;
                if (Vector3.DistanceSquared(origin, resolved) <= 9f)
                {
                    this.pointAttempted = true;
                }
                else
                {
                    descent.StopDescending();
                    if (backend.MoveTo(resolved, fly: true))
                    {
                        this.pointAttempted = true;
                        this.approachIssued = true;
                        this.Destination = resolved;
                        return LandingStatus.Approaching;
                    }

                    // Keep the configured point authoritative. A rejected request is retried
                    // until the travel session's landing timeout, rather than descending elsewhere.
                    return LandingStatus.WaitingForPath;
                }
            }
            else
            {
                this.pointAttempted = true;
                Vector3? point = backend.FindNearestReachablePoint(origin, 30f, 120f);
                if (point is { } reachable)
                {
                    // NearestPointReachable may legitimately return the edge of a walkable
                    // island. From the air, the vector from that point to the player is a
                    // useful estimate of the outward direction. Try a small inward move and
                    // keep the original result if the map is too narrow or the probe fails.
                    reachable = this.TryMoveTowardInterior(origin, reachable) ?? reachable;
                    if (Vector3.DistanceSquared(origin, reachable) > 9f)
                    {
                        descent.StopDescending();
                        if (backend.MoveTo(reachable, fly: true))
                        {
                            this.approachIssued = true;
                            this.Destination = reachable;
                            return LandingStatus.Approaching;
                        }
                    }
                }
            }
        }
        if (this.approachIssued && backend.IsMoveActive)
        {
            if (position is { } current && this.Destination is { } target
                && Vector3.DistanceSquared(current, target) <= 9f)
                backend.Stop();
            else
                return LandingStatus.Approaching;
            if (backend.IsMoveActive)
                return LandingStatus.WaitingForPath;
        }
        if (this.approachIssued && this.preferredDestination is not null
            && (position is not { } currentPosition || this.Destination is not { } configuredTarget
                || Vector3.DistanceSquared(currentPosition, configuredTarget) > 9f))
        {
            // A configured point is authoritative. If vnavmesh ended elsewhere, retry the
            // approach instead of silently descending at an unintended location.
            this.approachIssued = false;
            this.pointAttempted = false;
            this.Destination = null;
            return LandingStatus.WaitingForPath;
        }
        descent.BeginDescending();
        return LandingStatus.Descending;
    }

    public void Reset()
    {
        descent.StopDescending();
        this.approachIssued = this.pointAttempted = false;
        this.Destination = null;
        this.preferredDestination = null;
        this.settledSince = null;
        this.lastGroundedPosition = null;
    }

    /// <summary>Uses a host-configured planar point and resolves its floor instead of searching around the player.</summary>
    public void SetPreferredDestination(Vector3? destination) => this.preferredDestination = destination;

    /// <summary>The host has already reached a validated floor point; descend without another floor search.</summary>
    public void BeginVerticalDescent()
    {
        this.Reset();
        this.pointAttempted = true;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private Vector3? TryMoveTowardInterior(Vector3 origin, Vector3 reachable)
    {
        float offset = this.InteriorOffset;
        if (!float.IsFinite(offset) || offset <= 0f)
            return null;

        Vector2 outward = new(origin.X - reachable.X, origin.Z - reachable.Z);
        if (outward.LengthSquared() <= 1f)
            return null;

        Vector2 inward = -Vector2.Normalize(outward);
        Vector3 probe = new(
            reachable.X + inward.X * offset,
            reachable.Y,
            reachable.Z + inward.Y * offset);
        Vector3? candidate = backend.FindNearestReachablePoint(probe, InteriorProbeRadius, 15f);
        if (candidate is not { } resolved || !IsFinite(resolved))
            return null;

        Vector2 probeXZ = new(probe.X, probe.Z);
        Vector2 resolvedXZ = new(resolved.X, resolved.Z);
        return Vector2.Distance(probeXZ, resolvedXZ) <= InteriorProbeMaxSnap
            && Vector2.Distance(new(reachable.X, reachable.Z), resolvedXZ) >= offset * 0.5f
            ? resolved : null;
    }
}
