using System.Diagnostics;
using System.Diagnostics.Tracing;
using GlamByMariya.Web.Security;
using Microsoft.Extensions.Logging;

namespace GlamByMariya.CustomerAuthDatabaseDiagnostic;

internal enum Phase
{
    Configuration, PsqlBaseline, ProtocolConnect, SslRequest, ProtocolTls,
    ColdPoolAcquire, PoolDispatch, PhysicalOpen, Dns, Tcp, Tls,
    PostTlsLoginOrBootstrap, DriverInternalCommand, WarmPoolAcquire,
    ReadOnlyBegin, RestrictedIdentity, ReadinessQuery, Rollback, Complete,
    Launcher, WorkerInitialization, OutputCollection
}
internal enum Outcome { Started, Passed, Failed, Unfinished, Observed, Refused }

internal sealed class DiagnosticTrace(TextWriter output)
{
    private readonly object gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Dictionary<(Phase Phase, Guid Activity), long> starts = new();
    private Phase current = Phase.Configuration;
    internal int PhysicalOpens { get; private set; }
    internal int SuccessfulTlsHandshakes { get; private set; }
    internal bool IsBorrowing { get { lock (gate) return current is Phase.ColdPoolAcquire or Phase.WarmPoolAcquire; } }

    internal void Begin(Phase phase, bool main = true, Guid activity = default)
    {
        lock (gate)
        {
            if (main) current = phase;
            starts[(phase, activity)] = clock.ElapsedMilliseconds;
            if (phase == Phase.PhysicalOpen) PhysicalOpens++;
            Mark(phase, Outcome.Started, activity);
        }
    }

    internal bool End(Phase phase, Outcome outcome, Guid activity = default)
    {
        lock (gate)
        {
            if (!starts.ContainsKey((phase, activity))) return false; // Missing event is not PASS.
            if (phase == Phase.Tls && outcome == Outcome.Passed) SuccessfulTlsHandshakes++;
            Mark(phase, outcome, activity);
            starts.Remove((phase, activity));
            return true;
        }
    }

    internal void Mark(Phase phase, Outcome outcome, Guid activity = default)
    {
        lock (gate) output.WriteLine(Record(phase, outcome, clock.ElapsedMilliseconds,
            starts.TryGetValue((phase, activity), out var start) ? clock.ElapsedMilliseconds - start : 0));
    }

    internal static string Record(Phase phase, Outcome outcome, long elapsed, long duration, Exception? error = null)
    {
        var detail = error == null ? "category=None|type=None|cause=None|code=None" :
            CustomerAuthStartupDiagnostics.Describe(CustomerAuthStartupStage.RestrictedDatabaseReadiness, error)
                .Split("|category=", 2)[1];
        if (error != null) detail = "category=" + detail;
        return $"DBDIAG|stage={phase}|status={outcome}|elapsed_ms={elapsed}|duration_ms={duration}|{detail}";
    }

    internal void Fail(Exception error)
    {
        lock (gate)
        {
            output.WriteLine(Record(current, Outcome.Failed, clock.ElapsedMilliseconds,
                starts.TryGetValue((current, Guid.Empty), out var start) ? clock.ElapsedMilliseconds - start : 0, error));
            starts.Remove((current, Guid.Empty));
            foreach (var key in starts.Keys.Where(p => p.Phase != current).ToArray()) End(key.Phase, Outcome.Unfinished, key.Activity);
        }
    }
}

internal sealed class NetworkObserver : EventListener
{
    private DiagnosticTrace? trace;
    internal NetworkObserver(DiagnosticTrace trace)
    {
        this.trace = trace;
        foreach (var source in EventSource.GetSources()) Enable(source);
    }
    protected override void OnEventSourceCreated(EventSource source) => Enable(source);
    private void Enable(EventSource source)
    {
        if (source.Name is "System.Net.NameResolution" or "System.Net.Sockets" or "System.Net.Security")
            EnableEvents(source, EventLevel.Informational, EventKeywords.All);
    }
    protected override void OnEventWritten(EventWrittenEventArgs args)
    {
        // Event payloads contain private hosts/certificates: never inspect/format them.
        Observe(trace, args.EventSource.Name, args.EventName, args.ActivityId);
    }
    internal static void Observe(DiagnosticTrace? trace, string source, string? name, Guid activity = default)
    {
        if (trace == null) return;
        var phase = source switch
        {
            "System.Net.NameResolution" => Phase.Dns,
            "System.Net.Sockets" => Phase.Tcp,
            "System.Net.Security" => Phase.Tls,
            _ => (Phase?)null
        };
        if (phase == null) return;
        switch (name)
        {
            case "ResolutionStart" or "ConnectStart" or "HandshakeStart":
                trace.Begin(phase.Value, main: false, activity); break;
            case "ResolutionFailed" or "ConnectFailed" or "HandshakeFailed":
                trace.End(phase.Value, Outcome.Failed, activity); break;
            case "ResolutionStop" or "ConnectStop" or "HandshakeStop":
                var observed = trace.End(phase.Value, Outcome.Passed, activity);
                if (phase == Phase.Tls && observed && trace.IsBorrowing) trace.Begin(Phase.PostTlsLoginOrBootstrap, main: false);
                break;
        }
    }
}

internal sealed class DriverObserver(DiagnosticTrace trace) : ILoggerFactory
{
    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
    public void Dispose() { }
    public ILogger CreateLogger(string categoryName) => new DriverLogger(trace, categoryName);
    internal static void Observe(DiagnosticTrace trace, string category, int id)
    {
        // Npgsql 10.0.0 event IDs are checked offline against its installed metadata.
        if (category == "Npgsql.Connection" && id == 1110)
        {
            trace.End(Phase.PoolDispatch, Outcome.Passed);
            trace.Begin(Phase.PhysicalOpen, main: false);
        }
        if (category == "Npgsql.Connection" && id == 1111)
        {
            trace.End(Phase.PhysicalOpen, Outcome.Passed);
        }
        if (category == "Npgsql.Command" && id == 2003) trace.Mark(Phase.DriverInternalCommand, Outcome.Observed);
    }
    private sealed class DriverLogger(DiagnosticTrace trace, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
            => Observe(trace, category, id.Id); // Ignore state, exceptions and formatter.
    }
}
