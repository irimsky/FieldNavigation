using System.Numerics;


namespace FieldNavigation;

public enum CombatLandingState { Idle, Descending, Repositioning, Landed, Failed }

public readonly record struct CombatLandingUpdate(CombatLandingState State, int PointIndex, string Reason);

/// <summary>Framework-only landing attempts around a frozen mob position; owns no game objects.</summary>
public sealed class CombatLandingSession
{
    private enum Purpose { Landing }
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(6);
    private const float MinimumProgress = 0.5f;
    private readonly NavigationSession<Purpose> navigation;
    private readonly IDescentControl descent;
    private Vector3[] points = [];
    private Vector3 progressPosition;
    private DateTime lastProgressAt;
    private DateTime repositionStartedAt;

    public CombatLandingSession(INavigationBackend backend, IDescentControl descent)
    {
        this.navigation = new(backend);
        this.descent = descent;
    }

    public CombatLandingState State { get; private set; }
    public int PointIndex { get; private set; }
    public Vector3? Destination => this.points.Length == 0 ? null : this.points[this.PointIndex];

    public void Start(DateTime now, Vector3 position, Vector3 center, float fallbackRadius)
    {
        this.Cancel();
        this.points = CreatePoints(position, center, fallbackRadius);
        this.State = CombatLandingState.Descending;
        this.ResetProgress(now, position);
    }

    public CombatLandingUpdate Tick(DateTime now, Vector3 position, bool inFlight, bool insideNoFly = false)
    {
        this.navigation.PumpCancellation();
        if (this.State is CombatLandingState.Idle or CombatLandingState.Landed or CombatLandingState.Failed)
            return this.Update("落地尝试已结束");
        // Landing wins over the watchdog, including when a reposition path touches ground.
        if (!inFlight)
        {
            this.navigation.Cancel();
            this.descent.StopDescending();
            this.State = CombatLandingState.Landed;
            return this.Update("已落地");
        }

        if (this.State == CombatLandingState.Repositioning)
        {
            this.descent.StopDescending();
            if (insideNoFly)
            {
                this.navigation.Cancel();
                this.State = CombatLandingState.Failed;
                return this.Update("换落点时进入禁飞区，停止飞行恢复");
            }
            Vector3 destination = this.points[this.PointIndex];
            if (Vector2.Distance(new(position.X, position.Z), new(destination.X, destination.Z)) <= 1f)
            {
                this.navigation.Cancel();
                this.State = CombatLandingState.Descending;
                this.ResetProgress(now, position);
            }
            else
            {
                if (Vector3.Distance(position, this.progressPosition) >= MinimumProgress)
                    this.ResetProgress(now, position);
                if (now - this.lastProgressAt >= StallTimeout)
                    return this.Advance(now, position, "换落点导航连续 6 秒没有有效位移");
                if (now - this.repositionStartedAt >= TimeSpan.FromSeconds(30))
                    return this.Advance(now, position, "换落点导航超过 30 秒仍未到达");
                NavigationRequestResult request = this.navigation.Request(now, Purpose.Landing,
                    destination, fly: true, position);
                return this.Update(request == NavigationRequestResult.Rejected
                    ? $"换落点导航请求失败，等待重试：{this.navigation.LastResolution}"
                    : "正在飞往下一个落点");
            }
        }

        // Only real downward progress renews the descent budget; jitter and upward
        // motion against an obstacle must not keep a blocked attempt alive forever.
        if (this.progressPosition.Y - position.Y >= MinimumProgress)
            this.ResetProgress(now, position);
        if (now - this.lastProgressAt >= StallTimeout)
            return this.Advance(now, position, "下降连续 6 秒没有有效进展");
        this.descent.BeginDescending();
        return this.Update("正在下降");
    }

    public void Cancel()
    {
        this.navigation.Cancel();
        this.descent.StopDescending();
        this.State = CombatLandingState.Idle;
        this.PointIndex = 0;
        this.points = [];
    }

    public void PumpCancellation() => this.navigation.PumpCancellation();

    public static Vector3[] CreatePoints(Vector3 initialPosition, Vector3 center, float fallbackRadius)
    {
        Vector2 offset = new(initialPosition.X - center.X, initialPosition.Z - center.Z);
        // Near the center the construction gives ineffective, nearly identical points.
        // Use the configured approach radius to keep recovery points distinct.
        if (offset.LengthSquared() < 1f)
            offset = new(Math.Max(2f, fallbackRadius), 0f);
        Vector2 perpendicular = new(-offset.Y, offset.X);
        Vector3 Point(Vector2 delta) => new(center.X + delta.X, initialPosition.Y, center.Z + delta.Y);
        return [initialPosition, Point(-offset), Point(perpendicular), Point(-perpendicular)];
    }

    private CombatLandingUpdate Advance(DateTime now, Vector3 position, string reason)
    {
        this.navigation.Cancel();
        this.descent.StopDescending();
        if (this.PointIndex == this.points.Length - 1)
        {
            this.State = CombatLandingState.Failed;
            return this.Update($"四个落点均失败：{reason}");
        }
        this.PointIndex++;
        // Keep the horizontal construction, but reposition at the blocked height
        // rather than climbing back to the altitude at which descent first began.
        Vector3 next = this.points[this.PointIndex];
        this.points[this.PointIndex] = new(next.X, position.Y, next.Z);
        this.State = CombatLandingState.Repositioning;
        this.repositionStartedAt = now;
        this.ResetProgress(now, position);
        return this.Update($"{reason}，切换落点 {this.PointIndex + 1}/4");
    }

    private void ResetProgress(DateTime now, Vector3 position)
    {
        this.lastProgressAt = now;
        this.progressPosition = position;
    }

    private CombatLandingUpdate Update(string reason) => new(this.State, this.PointIndex, reason);
}
