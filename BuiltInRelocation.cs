using System.Numerics;

namespace FieldNavigation;

/// <summary>Ground side/back points and airborne back/up/down points; never snaps flight onto mesh.</summary>
internal sealed class BuiltInRelocation(VnavmeshIpc navigation, NoFlyZoneCatalog zones, RelocateRequest request,
    TravelObservation start, Vector3 backwards, TimeProvider clock) : IDisposable
{
    private readonly Queue<Vector3> candidates = Generate(start, backwards, request.Distance);
    private readonly long started = clock.GetTimestamp();
    private Vector3? destination;
    private Vector3 settleAnchor;
    private long attemptStarted;
    private long? stable;
    private int attempts;
    public bool Completed { get; private set; }
    public bool Failed { get; private set; }
    public string Message { get; private set; } = "选择短距离脱困点";
    public Vector3? Destination => destination;
    public RecoveryMode Mode => start.Flight.InFlight ? RecoveryMode.Airborne : RecoveryMode.Ground;
    private static Queue<Vector3> Generate(TravelObservation start, Vector3 backwards, float distance)
    {
        backwards.Y = 0;
        backwards = backwards.LengthSquared() < .01f ? Vector3.UnitZ : Vector3.Normalize(backwards);
        Vector3 side = new(-backwards.Z, 0, backwards.X);
        var directions = start.Flight.InFlight
            ? new[] { backwards + Vector3.UnitY * .7f, backwards - Vector3.UnitY * .7f,
                backwards + side * .6f + Vector3.UnitY * .5f, backwards - side * .6f - Vector3.UnitY * .5f }
            : new[] { backwards + side, backwards - side, backwards + side * .4f, backwards - side * .4f };
        return new(directions.Select(d => start.Position + Vector3.Normalize(d) * distance));
    }
    public void Tick(TravelObservation frame)
    {
        if (Completed || Failed) return;
        if (clock.GetElapsedTime(started) >= request.TotalTimeout) { Fail("短距离脱困超时"); return; }
        if (frame.Flight.InFlight != start.Flight.InFlight) { Fail("脱困期间空中/地面状态变化"); return; }
        if (!frame.MovementReady || frame.Flight.Jumping || frame.Flight.MountTransition)
        { navigation.Stop(); destination = null; stable = null; Message = "等待脱困动作条件"; return; }
        if (Vector3.Distance(frame.Position, start.Position) > request.MaximumDisplacement) { Fail("脱困超出短距离限制"); return; }
        if (destination is { } point)
        {
            if (Vector3.Distance(frame.Position, point) <= .75f)
            {
                navigation.Stop();
                if (Vector3.Distance(frame.Position, start.Position) < request.MinimumDisplacement) { Next("实际位移不足"); return; }
                if (stable is null || Vector3.Distance(frame.Position, settleAnchor) > .1f)
                { stable = clock.GetTimestamp(); settleAnchor = frame.Position; }
                if (clock.GetElapsedTime(stable.Value) >= request.SettleDuration) { Completed = true; Message = "实际短移并停稳"; }
                return;
            }
            navigation.Pump(true);
            if (!navigation.IsMoveActive || clock.GetElapsedTime(attemptStarted) >= TimeSpan.FromSeconds(5))
                Next("短移候选未抵达或超时");
            return;
        }
        if (attempts >= 4 || !candidates.TryDequeue(out var probe)) { Fail("附近没有合法可达的脱困点"); return; }
        attempts++;
        Vector3 candidate = probe;
        if (!start.Flight.InFlight)
        {
            if (navigation.FindNearestReachablePoint(probe, 1, 2) is not { } floor) return;
            candidate = floor;
        }
        float displacement = Vector3.Distance(start.Position, candidate);
        if (!TravelGeometry.IsFinite(candidate) || displacement < request.MinimumDisplacement || displacement > request.MaximumDisplacement) return;
        if (start.Flight.InFlight && !request.IgnoreNoFlyZones && !zones.IsRouteExempt(frame.Territory, start.Position, candidate)
            && (zones.Find(frame.Territory, start.Position) is not null || zones.Find(frame.Territory, candidate) is not null)) return;
        navigation.Paths.DirectGround = false;
        navigation.Paths.MaximumPathLength = request.MaximumPathLength;
        if (!navigation.MoveTo(candidate, start.Flight.InFlight)) return;
        destination = candidate; attemptStarted = clock.GetTimestamp();
        Message = start.Flight.InFlight ? "向后上/后下方短移" : "向左右后方短移";
    }
    private void Next(string message) { navigation.Stop(); destination = null; stable = null; Message = message; }
    private void Fail(string message) { navigation.Stop(); Failed = true; Message = message; }
    public void Dispose() { navigation.Stop(); navigation.Paths.MaximumPathLength = float.PositiveInfinity; }
}
