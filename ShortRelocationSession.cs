using System.Numerics;

namespace FieldNavigation;

public interface ILimitedNavigationBackend : INavigationBackend
{
    bool MoveTo(Vector3 destination, bool fly, float maximumLength);
}

public sealed record ShortRelocationOptions
{
    public float MaximumPathLength { get; init; } = 12;
    public float MinimumDisplacement { get; init; } = 2;
    public float MaximumDisplacement { get; init; } = 8;
    public float ArrivalRadius { get; init; } = .75f;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan SettleDuration { get; init; } = TimeSpan.FromMilliseconds(300);
}

/// <summary>Short, bounded relocation. The host decides whether and when its business should restart.</summary>
public sealed class ShortRelocationSession(ILimitedNavigationBackend backend, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private ShortRelocationOptions options = new();
    private Vector3 origin, settleAnchor;
    private bool issued, flying;
    private long started, nextRequest;
    private long? settled;
    public Vector3? Destination { get; private set; }
    public bool IsActive => this.Destination is not null;
    public string Reason { get; private set; } = "";

    public void Start(Vector3 position, Vector3? awayFrom, float rotation, bool fly, ShortRelocationOptions? options = null)
    {
        this.Cancel();
        this.options = options ?? new();
        this.origin = position; this.flying = fly;
        this.started = this.clock.GetTimestamp(); this.nextRequest = this.started;
        this.Destination = RelocationPointPlanner.Find(position, awayFrom, rotation, fly, backend.FindNearestReachablePoint)
            ?? throw new InvalidOperationException("附近没有可达的短距离脱困点");
    }

    public NavigationTravelOutcome Tick(Vector3 position)
    {
        if (this.Destination is not { } target) return NavigationTravelOutcome.Cancelled;
        if (!TravelGeometry.IsFinite(position) || this.clock.GetElapsedTime(this.started) >= this.options.Timeout)
            return this.Fail("短距离脱困超时或位置无效");
        if (Vector3.Distance(position, this.origin) > this.options.MaximumDisplacement) return this.Fail("脱困移动超出短距离限制");
        if (Vector3.Distance(position, target) <= this.options.ArrivalRadius)
        {
            if (Vector3.Distance(position, this.origin) < this.options.MinimumDisplacement) return this.Fail("脱困没有产生足够实际位移");
            backend.Stop();
            if (this.settled is null || Vector3.DistanceSquared(position, this.settleAnchor) > .01f)
            { this.settled = this.clock.GetTimestamp(); this.settleAnchor = position; }
            if (this.clock.GetElapsedTime(this.settled.Value) < this.options.SettleDuration) return NavigationTravelOutcome.Progress;
            this.Destination = null; this.Reason = "脱困已实际抵达并停稳";
            return NavigationTravelOutcome.Arrived;
        }
        if (this.settled is not null || this.issued && !backend.IsMoveActive) return this.Fail("脱困路线结束但未抵达目标");
        if (!this.issued && this.clock.GetTimestamp() >= this.nextRequest)
        {
            this.issued = backend.MoveTo(target, this.flying, this.options.MaximumPathLength);
            this.nextRequest = this.clock.GetTimestamp() + (long)(.75 * this.clock.TimestampFrequency);
        }
        if (backend is INavigationPump pump) pump.Pump(true);
        return NavigationTravelOutcome.Progress;
    }
    private NavigationTravelOutcome Fail(string reason) { this.Cancel(); this.Reason = reason; return NavigationTravelOutcome.Failed; }
    public void Cancel()
    {
        if (this.Destination is not null) backend.Stop();
        this.Destination = null; this.issued = false; this.settled = null;
    }
}
