using System.Numerics;

namespace FieldNavigation;

public enum TravelMovementMode { Automatic, Foot, GroundMount, Fly }

/// <summary>Distances are world units (yalms); ground path type does not imply mounted movement.</summary>
public sealed record TravelModePolicy
{
    public TravelMovementMode Preferred { get; init; } = TravelMovementMode.Automatic;
    public float WalkDistance { get; init; } = 20;
    public float GroundMountDistance { get; init; } = 25;
    public float WalkHeightTolerance { get; init; } = 3;

    public TravelMovementMode Select(Vector3 origin, Vector3 target, bool canFly)
    {
        if (this.Preferred != TravelMovementMode.Automatic)
            return this.Preferred == TravelMovementMode.Fly && !canFly ? TravelMovementMode.GroundMount : this.Preferred;
        float distance = TravelGeometry.HorizontalDistance(origin, target);
        if (MathF.Abs(origin.Y - target.Y) <= this.WalkHeightTolerance)
        {
            if (distance <= this.WalkDistance) return TravelMovementMode.Foot;
            if (distance <= this.GroundMountDistance) return TravelMovementMode.GroundMount;
        }
        return canFly ? TravelMovementMode.Fly : TravelMovementMode.GroundMount;
    }
}

public sealed record TravelArrivalPolicy
{
    public float Radius { get; init; } = 5;
    public float HeightTolerance { get; init; } = 2;
    public bool Use3DDistance { get; init; } = true;
    public bool RequireGrounded { get; init; } = true;
    public bool RequireDismounted { get; init; } = true;
    public TimeSpan SettleDuration { get; init; } = TimeSpan.FromMilliseconds(300);

    public bool Contains(Vector3 position, Vector3 target) => TravelGeometry.IsFinite(position)
        && TravelGeometry.IsFinite(target) && MathF.Abs(position.Y - target.Y) <= this.HeightTolerance
        && (this.Use3DDistance ? Vector3.Distance(position, target) : TravelGeometry.HorizontalDistance(position, target)) <= this.Radius;
}

public static class TravelGeometry
{
    public static bool IsFinite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    public static float HorizontalDistance(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));

    /// <summary>Local actor-height probes; never snap a live target to a roof above it.</summary>
    public static Vector3? FindApproachPoint(Vector3 origin, Vector3 target,
        Func<Vector3, float, float, Vector3?> nearest, TravelArrivalPolicy arrival, float margin = .5f)
    {
        if (!IsFinite(origin) || !IsFinite(target)) return null;
        float bearing = MathF.Atan2(origin.Z - target.Z, origin.X - target.X);
        Vector3? best = null;
        float bestDistance = float.MaxValue;
        foreach (float factor in new[] { .8f, .5f, .25f })
        for (int i = 0; i < 16; i++)
        {
            float angle = bearing + i * MathF.Tau / 16;
            var probe = target + new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)) * (arrival.Radius * factor);
            if (nearest(probe, 1.25f, arrival.HeightTolerance) is not { } point || !IsFinite(point)
                || HorizontalDistance(point, target) < 1 || !arrival.Contains(point, target)
                || (arrival.Use3DDistance ? Vector3.Distance(point, target) : HorizontalDistance(point, target)) > arrival.Radius - margin)
                continue;
            float distance = Vector3.DistanceSquared(origin, point);
            if (distance < bestDistance) { bestDistance = distance; best = point; }
        }
        return best;
    }
}

/// <summary>Framework-only actions; the session observes actual state before advancing.</summary>
public interface ITravelMovementControl : ITravelRecoveryControl
{
    bool TryMount(Action onRequest);
    bool TryDismount();
    bool TryTakeoff(DateTime now);
    string TakeoffStatus => "等待飞行状态确认";
    void ResetTakeoff();
    void CancelMountCast();
}

public sealed class TravelMovementControl(MountAdapter mount, TakeoffAdapter takeoff) : ITravelMovementControl
{
    public bool TryMount(Action onRequest) => mount.TryMount(onRequest);
    public bool TryDismount() => mount.TryDismount();
    public bool TryTakeoff(DateTime now) => takeoff.TryTakeoff(now, true);
    public string TakeoffStatus => takeoff.Status;
    public void ResetTakeoff() => takeoff.Reset();
    public void CancelMountCast() => mount.CancelMountCast();
    public bool TryJump() => mount.TryJump();
}

/// <summary>Optional shared path pump. Only call Pump after lifecycle/cancellation checks on Framework.</summary>
public interface INavigationPump
{
    bool DirectGround { get; set; }
    void Pump(bool movementReady);
}
