namespace FieldNavigation;

public static class TravelSafetyRules
{
    public static bool CanCheckAtStartup(bool inFlight, bool jumping, bool mountTransition) =>
        !inFlight && !jumping && !mountTransition;
    public static bool CanStartGroundTravel(bool inFlight, bool landingPending) => !inFlight && !landingPending;
}

/// <summary>Debounces map boundaries without permitting a ground route during descent.</summary>
public sealed class NoFlyTravelPolicy
{
    private static readonly TimeSpan BoundaryDebounce = TimeSpan.FromSeconds(1);
    private bool initialized;
    private DateTime? enteredAt;
    private DateTime? leftAt;
    public bool GroundOnly { get; private set; }

    /// <summary>Starts a navigation session and remembers whether its origin is in a no-fly zone.</summary>
    public void Begin(DateTime now, bool inside)
    {
        this.Reset();
        this.initialized = true;
        this.GroundOnly = inside;
        this.enteredAt = inside ? now : null;
    }

    public void Update(DateTime now, bool inside, bool landing)
    {
        if (!this.initialized)
        {
            this.initialized = true;
            this.GroundOnly = inside;
        }
        if (inside)
        {
            this.leftAt = null;
            this.enteredAt ??= now;
            // Keep the current flight request alive while the character is only
            // touching the boundary. Once the position has remained inside for a
            // full second, lock the session to ground mode and let the travel
            // state machine start the landing transition.
            if (!this.GroundOnly
                && now - this.enteredAt.Value >= BoundaryDebounce)
                this.GroundOnly = true;
        }
        else
        {
            this.enteredAt = null;
            if (this.GroundOnly)
            {
                this.leftAt ??= now;
                // Landing owns movement until it reports a stable ground frame. The
                // exit timer may elapse during descent, but it is consumed only after
                // landing has finished. This also applies when the session started in
                // a no-fly zone: the initial intent may resume after the one-second
                // exit debounce.
                if (!landing && now - this.leftAt.Value >= BoundaryDebounce)
                    this.GroundOnly = false;
            }
            else if (!this.GroundOnly)
            {
                this.leftAt = null;
            }
        }
    }

    public void Reset()
    {
        this.initialized = false;
        this.enteredAt = this.leftAt = null;
        this.GroundOnly = false;
    }
}

/// <summary>A teleport checkpoint requires a real loading cycle, including same-territory teleports.</summary>
public sealed class TeleportArrivalGate
{
    public bool Pending { get; private set; }
    private bool requested;
    private bool observedLoading;

    public void Requested() { this.Pending = this.requested = true; this.observedLoading = false; }
    public void CancelRequest() { this.Pending = this.requested = this.observedLoading = false; }
    public void Observe(bool loading)
    {
        if (this.requested && loading)
            this.observedLoading = true;
    }
    public bool Complete(bool atDestination, bool loading, bool busy)
    {
        if (this.Pending && this.requested && this.observedLoading && atDestination && !loading && !busy)
        {
            this.Pending = false;
            return true;
        }
        return false;
    }
}
