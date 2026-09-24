namespace FieldNavigation;

/// <summary>Condition flags authorize riding; native state prevents duplicate requests during transitions.</summary>
public readonly record struct MountState(bool ConditionMounted, bool NativeMounted, bool Transition)
{
    public bool CanNavigateMounted => this.ConditionMounted && !this.Transition;
    public bool CanRequestMount => !this.ConditionMounted && !this.NativeMounted && !this.Transition;
    public bool IsDismounted => !this.ConditionMounted && !this.NativeMounted && !this.Transition;
}

public sealed class MountRequestGate
{
    public DateTime NextRequestAt { get; private set; }
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public bool TryRequest(DateTime now, MountState state, bool allowed, Func<bool> request)
    {
        if (!allowed || !state.CanRequestMount || now < this.NextRequestAt)
            return false;
        this.NextRequestAt = now + this.RetryDelay;
        return request();
    }
}
