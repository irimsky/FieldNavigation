using System.Numerics;

namespace FieldNavigation;

public static class RelocationPointPlanner
{
    public static Vector3? Find(Vector3 origin, Vector3? awayFrom, float rotation, bool flying,
        Func<Vector3, float, float, Vector3?> nearestReachable)
    {
        var direction = awayFrom is { } target ? origin - target : new Vector3(MathF.Sin(rotation), 0, MathF.Cos(rotation));
        float bearing = direction.X * direction.X + direction.Z * direction.Z > 0.01f
            ? MathF.Atan2(direction.Z, direction.X) : rotation + MathF.PI / 2;
        foreach (float radius in new[] { 4f, 3f })
        {
            // Prefer moving away from the old destination/target, then try either side.
            foreach (int offset in new[] { 0, 1, -1, 2, -2, 3, -3, 4, -4, 5, -5, 6, -6, 7, -7, 8 })
            {
                float angle = bearing + offset * MathF.Tau / 16;
                var sample = origin + new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius);
                var resolved = flying ? sample : nearestReachable(sample, 1, 1.5f);
                if (resolved is not { } point || !float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)) continue;
                float horizontal = Vector2.Distance(new(origin.X, origin.Z), new(point.X, point.Z));
                if (horizontal is >= 2.5f and <= 5 && MathF.Abs(point.Y - origin.Y) <= 1.5f) return point;
            }
        }
        return null;
    }
}
