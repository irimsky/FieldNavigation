using System.Numerics;

namespace FieldNavigation;

public static class LandingRecoveryPolicy
{
    public static IEnumerable<Vector3> GetCandidates(Vector3 position, Vector3 center, float radius, int attempt)
    {
        float boundary = radius * 0.9f;
        if (!float.IsFinite(boundary) || boundary <= 0)
            yield break;

        float offset = Math.Min(10f, radius * 0.4f);
        for (int i = 0; i < 8; i++)
        {
            float angle = (i + attempt) * MathF.PI / 4f;
            Vector3 candidate = position + new Vector3(MathF.Cos(angle) * offset, 0, MathF.Sin(angle) * offset);
            Vector2 relative = new(candidate.X - center.X, candidate.Z - center.Z);
            if (relative.Length() > boundary)
                relative = Vector2.Normalize(relative) * boundary;
            yield return new(center.X + relative.X, position.Y, center.Z + relative.Y);
        }
    }

    public static bool IsUsable(Vector3 point, Vector3 origin, Vector3 center, float radius)
    {
        float distance = HorizontalDistance(point, origin);
        return float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z)
            && HorizontalDistance(point, center) <= radius * 0.95f
            && distance >= Math.Min(6f, radius * 0.2f) && distance <= 16f;
    }

    public static float HorizontalDistance(Vector3 left, Vector3 right) =>
        Vector2.Distance(new(left.X, left.Z), new(right.X, right.Z));
}
