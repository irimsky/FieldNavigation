using System.Diagnostics;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using System.Numerics;

namespace FieldNavigation;

public sealed partial class VnavmeshWaypointIpc
{
    private Action<string>? diagnosticWrite;
    private Func<string>? diagnosticContext;
    private ICallGateSubscriber<int>? queuedQueries;
    private ICallGateSubscriber<int>? waypointCount;
    private long diagnosticQueryId, diagnosticTick;
    private long? lastDiagnosticPoll, lastStopTrace, lastRequestWaitTrace;
    private string lastDiagnosticState = string.Empty;
    private readonly List<QueryTrace> diagnosticQueries = [];

    internal void ConfigureDiagnostics(IPluginLog log, Func<string> context)
    {
        this.diagnosticWrite = text => log.Debug("{NavigationDiagnostic}", text);
        this.diagnosticContext = context;
        // Optional gates must never affect backend availability or normal IPC requirements.
        this.queuedQueries = this.plugins.GetIpcSubscriber<int>("vnavmesh.Nav.PathfindNumQueued");
        this.waypointCount = this.plugins.GetIpcSubscriber<int>("vnavmesh.Path.NumWaypoints");
    }

    private static void WriteDiagnostic(Action<string> write, string text)
    {
        try { write(text); } catch { /* Diagnostics cannot change navigation/cancellation. */ }
    }

    private static string ReadDiagnostic<T>(ICallGateSubscriber<T>? gate)
    {
        try { return gate?.HasFunction == true ? $"{gate.InvokeFunc()}" : "unavailable"; }
        catch (Exception ex) { return "error:" + ex.GetType().Name; }
    }

    internal string ReadDiagnosticState() =>
        $"navCalculating={ReadDiagnostic(this.calculating)}; queued={ReadDiagnostic(this.queuedQueries)}; "
        + $"simpleCalculating={ReadDiagnostic(this.simpleCalculating)}; rawPathRunning={ReadDiagnostic(this.running)}; "
        + $"waypoints={ReadDiagnostic(this.waypointCount)}; movementAllowed={ReadDiagnostic(this.movementAllowed)}; "
        + $"meshReady={ReadDiagnostic(this.ready)}; ownedIpc={this.SupportsOwnedPaths}";

    // Called only by FieldNavigator.Tick on Framework, including while no operation is active.
    internal void PollDiagnostics(string localState)
    {
        this.diagnosticTick++;
        if (this.diagnosticWrite is not { } write) return;
        long now = Stopwatch.GetTimestamp();
        if (this.lastDiagnosticPoll is { } previous && Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromSeconds(2)) return;
        this.lastDiagnosticPoll = now;
        this.diagnosticQueries.RemoveAll(trace => trace.Completed);
        try
        {
            string context = this.diagnosticContext?.Invoke() ?? "operation=none";
            string state = $"{context}; {localState}; {this.ReadDiagnosticState()}";
            if (context == "operation=none" && state == this.lastDiagnosticState) return;
            this.lastDiagnosticState = state;
            WriteDiagnostic(write, $"[FN_BACKEND] tick={this.diagnosticTick}; {state}");
        }
        catch { }
    }

    internal void TraceStop(string localState, bool hasLocalWork)
    {
        if (!hasLocalWork || this.diagnosticWrite is not { } write) return;
        // A stuck global busy flag can cause Stop every frame. Log pending query/path
        // cancellation immediately; throttle repeated cancellation-wait-only calls.
        long now = Stopwatch.GetTimestamp();
        bool waitingOnly = localState.StartsWith("query=none; ownsPath=False;", StringComparison.Ordinal);
        if (waitingOnly && this.lastStopTrace is { } previous && Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromSeconds(2)) return;
        this.lastStopTrace = now;
        try
        {
            WriteDiagnostic(write, $"[FN_STOP] tick={this.diagnosticTick}; {this.diagnosticContext?.Invoke()}; "
                + $"{localState}; {this.ReadDiagnosticState()}; caller={DiagnosticCallChain()}");
        }
        catch { }
    }

    internal void TraceStopCompleted(string localState)
    {
        if (this.diagnosticWrite is not { } write) return;
        try { WriteDiagnostic(write, $"[FN_STOP] after; tick={this.diagnosticTick}; {this.diagnosticContext?.Invoke()}; {localState}; {this.ReadDiagnosticState()}"); }
        catch { }
    }

    private long TraceQuerySubmit(Vector3 origin, Vector3 destination, bool fly)
    {
        long id = Interlocked.Increment(ref this.diagnosticQueryId);
        if (this.diagnosticWrite is not { } write) return id;
        try
        {
            WriteDiagnostic(write, $"[FN_QUERY] submit; query={id}; tick={this.diagnosticTick}; "
                + $"{this.diagnosticContext?.Invoke()}; origin={origin}; destination={destination}; fly={fly}; "
                + $"{this.ReadDiagnosticState()}; caller={DiagnosticCallChain()}");
        }
        catch { }
        return id;
    }

    internal void TraceRequestWait(string detail)
    {
        if (this.diagnosticWrite is not { } write) return;
        long now = Stopwatch.GetTimestamp();
        if (this.lastRequestWaitTrace is { } previous && Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromSeconds(2)) return;
        this.lastRequestWaitTrace = now;
        try { WriteDiagnostic(write, $"[FN_WAIT] tick={this.diagnosticTick}; {this.diagnosticContext?.Invoke()}; {detail}; {this.ReadDiagnosticState()}"); }
        catch { }
    }

    private void TraceQueryLifetime(long id, Task<List<Vector3>> task, CancellationToken token, string layer)
    {
        if (this.diagnosticWrite is not { } write) return;
        try
        {
            var trace = new QueryTrace(write, id, this.diagnosticTick,
                $"layer={layer}; {this.diagnosticContext?.Invoke() ?? "operation=none"}");
            this.diagnosticQueries.Add(trace);
            trace.Start(task, token);
        }
        catch { }
    }

    // Managed-only observers are explicitly detached at unload. A backend task that never
    // completes may retain a small inert observer, but never FN, IPC gates or host services.
    internal void DetachDiagnostics()
    {
        this.diagnosticWrite = null;
        this.diagnosticContext = null;
        foreach (var trace in this.diagnosticQueries) trace.Dispose();
        this.diagnosticQueries.Clear();
    }

    private sealed class QueryTrace(Action<string> write, long id, long tick, string context) : IDisposable
    {
        private Action<string>? writer = write;
        private CancellationTokenRegistration registration;
        private int completed;
        private readonly long started = Stopwatch.GetTimestamp();
        public bool Completed => Volatile.Read(ref this.completed) != 0;
        private void Write(string text)
        {
            if (Volatile.Read(ref this.writer) is { } output) WriteDiagnostic(output, text);
        }
        public void Start(Task<List<Vector3>> task, CancellationToken token)
        {
            this.Write($"[FN_QUERY] returned; query={id}; task={task.Id}; tick={tick}; {context}; taskStatus={task.Status}");
            // May run off Framework: no IPC, position reads, or FN/host references.
            this.registration = token.Register(() => this.Write(
                $"[FN_QUERY] cancel; query={id}; task={task.Id}; {context}; submittedTick={tick}; "
                + $"thread={Environment.CurrentManagedThreadId}; ageMs={Stopwatch.GetElapsedTime(this.started).TotalMilliseconds:0.###}; "
                + $"taskStatus={task.Status}; completed={task.IsCompleted}; caller={DiagnosticCallChain()}"));
            _ = task.ContinueWith((finished, state) =>
            {
                var trace = (QueryTrace)state!;
                try
                {
                    trace.registration.Dispose();
                    trace.Write($"[FN_QUERY] complete; query={id}; task={finished.Id}; {context}; "
                        + $"ageMs={Stopwatch.GetElapsedTime(trace.started).TotalMilliseconds:0.###}; taskStatus={finished.Status}; "
                        + $"cancelRequested={token.IsCancellationRequested}; "
                        + $"points={(finished.IsCompletedSuccessfully ? finished.Result.Count : -1)}; "
                        + $"error={finished.Exception?.GetBaseException().GetType().Name}");
                }
                catch { }
                finally { trace.Dispose(); Volatile.Write(ref trace.completed, 1); }
            }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        public void Dispose()
        {
            Interlocked.Exchange(ref this.writer, null);
            this.registration.Dispose();
        }
    }

    private static string DiagnosticCallChain()
    {
        try
        {
            return string.Join(" <- ", new StackTrace(true).GetFrames()
                .Where(frame => frame.GetMethod()?.DeclaringType?.Namespace is { } ns
                    && (ns.StartsWith("FieldNavigation", StringComparison.Ordinal) || ns == "AutoFatre"))
                .Where(frame => frame.GetMethod()?.DeclaringType?.FullName?.StartsWith(typeof(VnavmeshWaypointIpc).FullName!, StringComparison.Ordinal) != true)
                .Take(12).Select(frame => $"{frame.GetMethod()?.DeclaringType?.Name}.{frame.GetMethod()?.Name}"
                    + $"@{Path.GetFileName(frame.GetFileName())}:{frame.GetFileLineNumber()}"));
        }
        catch { return "unavailable"; }
    }
}
