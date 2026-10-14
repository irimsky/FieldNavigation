using System.Numerics;

namespace FieldNavigation;

/// <summary>Finite alternative-point budget for one actor. Host time is monotonic milliseconds.</summary>
public sealed class ApproachRetrySession(long startedAt, int maximumRetries = 2, long timeoutMilliseconds = 90000)
{
    private readonly List<Vector3> tried = [];
    public long Deadline { get; } = startedAt + timeoutMilliseconds;
    public int Retries { get; private set; }
    public Vector3? TryNext(long now, Vector3 origin, Vector3 target, float radius, float heightTolerance,
        Func<Vector3, float, float, Vector3?> nearest)
    {
        if (now >= this.Deadline || this.Retries >= maximumRetries) return null;
        if (this.tried.Count == 0) this.tried.Add(target);
        Vector2 direction = new(origin.X - target.X, origin.Z - target.Z);
        direction = direction.LengthSquared() > .01f ? Vector2.Normalize(direction) : Vector2.UnitX;
        Vector3? best = null;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < 8; i++)
        {
            float angle = i * MathF.PI / 4;
            var offset = new Vector2(direction.X * MathF.Cos(angle) - direction.Y * MathF.Sin(angle),
                direction.X * MathF.Sin(angle) + direction.Y * MathF.Cos(angle)) * Math.Max(.5f, radius - .5f);
            var probe = target + new Vector3(offset.X, 0, offset.Y);
            if (nearest(probe, .75f, heightTolerance) is not { } point || !TravelGeometry.IsFinite(point)
                || TravelGeometry.HorizontalDistance(point, target) > radius - .1f || MathF.Abs(point.Y - target.Y) > heightTolerance
                || this.tried.Any(previous => Vector3.DistanceSquared(previous, point) < .25f)) continue;
            float distance = Vector3.DistanceSquared(origin, point);
            if (distance < bestDistance) { best = point; bestDistance = distance; }
        }
        if (best is { } result) { this.tried.Add(result); this.Retries++; }
        return best;
    }
}
