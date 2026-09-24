using System.Numerics;

namespace FieldNavigation;

public enum LandingStatus { WaitingForPath, Approaching, Descending, Settling, Landed }

/// <summary>Releases old navigation, optionally approaches a nearby floor, descends, then settles.</summary>
public sealed class LandingSession(INavigationBackend backend, IDescentControl descent)
{
    private bool approachIssued;
    private bool pointAttempted;
    private DateTime? settledSince;
    private Vector3? lastGroundedPosition;
    private const float GroundedMovementThresholdSquared = 0.25f;
    public Vector3? Destination { get; private set; }
    public DateTime? SettledSince => this.settledSince;
    public TimeSpan SettleDuration { get; init; } = TimeSpan.FromMilliseconds(750);

    public LandingStatus Update(DateTime now, Vector3? position, FlightState state)
    {
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
            this.pointAttempted = true;
            Vector3? point = backend.FindNearestReachablePoint(origin, 30f, 120f);
            if (point is { } reachable && Vector3.DistanceSquared(origin, reachable) > 9f)
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
        descent.BeginDescending();
        return LandingStatus.Descending;
    }

    public void Reset()
    {
        descent.StopDescending();
        this.approachIssued = this.pointAttempted = false;
        this.Destination = null;
        this.settledSince = null;
        this.lastGroundedPosition = null;
    }
}
