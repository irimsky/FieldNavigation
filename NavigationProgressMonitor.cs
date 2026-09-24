using System.Numerics;

namespace FieldNavigation;

public enum NavigationProgressStatus { Idle, Pathfinding, Moving, Stalled }

public readonly record struct NavigationProgressResult(NavigationProgressStatus Status, string Reason = "");

/// <summary>Separates asynchronous calculation time from movement time; detours count as progress.</summary>
public sealed class NavigationProgressMonitor
{
    private DateTime lastSampleAt;
    private Vector3 pathfindStartPosition;
    private bool pathWasCalculating;
    public Vector3? LastProgressPosition { get; private set; }
    public DateTime LastProgressAt { get; private set; }
    public DateTime PathfindStartedAt { get; private set; }
    public float BestDistance { get; private set; } = float.MaxValue;
    public TimeSpan PathfindTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(2);
    public float MinimumDisplacement { get; init; } = 1.5f;

    public void Start(DateTime now, Vector3 position, float distance)
    {
        this.Reset();
        this.LastProgressPosition = position;
        this.LastProgressAt = this.lastSampleAt = now;
        this.BestDistance = distance;
    }

    public NavigationProgressResult Update(DateTime now, Vector3 position, Vector3 destination,
        float distance, bool horizontal, bool requestIssued, DateTime requestedAt,
        bool calculating, bool running, TimeSpan stuckTimeout, TimeSpan? noPathTimeout = null)
    {
        if (this.LastProgressPosition is null)
            this.Start(now, position, distance);

        if (calculating)
        {
            if (!this.pathWasCalculating)
            {
                this.PathfindStartedAt = now;
                this.pathfindStartPosition = position;
            }
            this.pathWasCalculating = true;
            this.LastProgressAt = now;
            this.LastProgressPosition = position;
            float displacement = Distance(this.pathfindStartPosition, position, horizontal);
            if (now - this.PathfindStartedAt >= this.PathfindTimeout && displacement < this.MinimumDisplacement)
                return new(NavigationProgressStatus.Stalled,
                    $"vnavmesh 连续算路 {this.PathfindTimeout.TotalSeconds:0} 秒且角色位移仅 {displacement:0.0}");
            return new(NavigationProgressStatus.Pathfinding);
        }

        if (this.pathWasCalculating)
        {
            // The first execution frame gets a fresh movement budget, even after a long calculation.
            this.LastProgressAt = this.lastSampleAt = now;
            this.LastProgressPosition = position;
            this.pathWasCalculating = false;
        }
        this.PathfindStartedAt = DateTime.MinValue;

        if (!requestIssued)
            return new(NavigationProgressStatus.Idle);
        if (!running)
        {
            TimeSpan idlePathTimeout = noPathTimeout ?? this.PathfindTimeout;
            if (now - requestedAt < idlePathTimeout)
                return new(NavigationProgressStatus.Pathfinding);
            return new(NavigationProgressStatus.Stalled,
                $"导航请求已接受但连续 {idlePathTimeout.TotalSeconds:0.#} 秒没有算路任务或运行路径");
        }
        if (now - this.lastSampleAt < this.SampleInterval)
            return new(NavigationProgressStatus.Moving);

        this.lastSampleAt = now;
        Vector3 previous = this.LastProgressPosition!.Value;
        float moved = Distance(previous, position, horizontal);
        float improvement = this.BestDistance - distance;
        Vector3 toTarget = destination - previous;
        Vector3 movement = position - previous;
        if (horizontal)
        {
            toTarget.Y = 0;
            movement.Y = 0;
        }
        float forward = toTarget.Length() > 0.1f ? Vector3.Dot(movement, Vector3.Normalize(toTarget)) : 0;
        if (moved >= this.MinimumDisplacement)
        {
            this.BestDistance = Math.Min(this.BestDistance, distance);
            this.LastProgressAt = now;
            this.LastProgressPosition = position;
        }
        else if (now - this.LastProgressAt >= stuckTimeout)
        {
            return new(NavigationProgressStatus.Stalled,
                $"导航连续 {stuckTimeout.TotalSeconds:0.#} 秒没有有效位移（最近采样位移 {moved:0.0}，朝目标前进 {forward:0.0}，距离改善 {improvement:0.0}，PathfindInProgress={calculating}，PathRunning={running}）");
        }
        return new(NavigationProgressStatus.Moving);
    }

    public void Reset()
    {
        this.LastProgressPosition = null;
        this.LastProgressAt = this.lastSampleAt = this.PathfindStartedAt = DateTime.MinValue;
        this.pathWasCalculating = false;
        this.BestDistance = float.MaxValue;
    }

    public static float Distance(Vector3 from, Vector3 to, bool horizontal) => horizontal
        ? Vector2.Distance(new(from.X, from.Z), new(to.X, to.Z))
        : Vector3.Distance(from, to);
}
