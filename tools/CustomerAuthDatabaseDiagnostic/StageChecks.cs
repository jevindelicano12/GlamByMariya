using System.Security.Authentication;
using Npgsql;

namespace GlamByMariya.CustomerAuthDatabaseDiagnostic;

internal static class StageChecks
{
    internal static async Task Run()
    {
        var checks = 0;
        void Check(bool value)
        {
            Program.Require(value, "Offline stage independence check failed.");
            checks++;
        }
        foreach (var protocolPass in new[] { true, false })
        foreach (var managedPass in new[] { true, false })
        {
            using var output = new StringWriter();
            var trace = new DiagnosticTrace(output);
            var order = new List<string>();
            async Task Protocol()
            {
                order.Add("protocol");
                trace.Begin(Phase.SslRequest);
                using var wire = new ReplyStream(protocolPass ? (byte)'S' : (byte)'N');
                try { await ProtocolProbe.Exchange(wire, default); }
                catch (AuthenticationException error) { throw new ProtocolFailureException(error); }
                trace.End(Phase.SslRequest, Outcome.Passed);
                trace.Begin(Phase.ProtocolTls);
                trace.End(Phase.ProtocolTls, Outcome.Passed); // Synthetic outcome, not a live handshake.
            }
            Task Managed()
            {
                order.Add("managed");
                trace.Begin(Phase.ColdPoolAcquire);
                if (!managedPass) throw new NpgsqlException("PRIVATE", new TimeoutException("PRIVATE"));
                trace.End(Phase.ColdPoolAcquire, Outcome.Passed); // Synthetic opening; no provider/socket.
                return Task.CompletedTask;
            }
            var exit = 1;
            try { exit = await Program.RunStages(true, Protocol, Managed, () => { }, trace); }
            catch (NpgsqlException error) { trace.Fail(error); }
            Check(order.SequenceEqual(new[] { "protocol", "managed" }));
            Check(exit == (protocolPass && managedPass ? 0 : 1));
            Check(!output.ToString().Contains("PRIVATE"));
            Check(!output.ToString().Contains("PostTlsLoginOrBootstrap"));
            Check(output.ToString().Contains("stage=Complete|status=Passed") == (protocolPass && managedPass));
            if (!protocolPass) Check(output.ToString().Contains("stage=SslRequest|status=Failed"));
        }
        // Safety failures before either stage are not recoverable diagnostic outcomes.
        foreach (var failAt in new[] { 1, 2 })
        {
            using var output = new StringWriter();
            var trace = new DiagnosticTrace(output);
            var safetyCalls = 0; var protocolCalls = 0; var managedCalls = 0; var refused = false;
            try
            {
                await Program.RunStages(true, () => { protocolCalls++; return Task.CompletedTask; },
                    () => { managedCalls++; return Task.CompletedTask; },
                    () => { if (++safetyCalls == failAt) throw new InvalidOperationException("PRIVATE-safety"); }, trace);
            }
            catch (InvalidOperationException) { refused = true; }
            Check(refused && managedCalls == 0 && protocolCalls == failAt - 1);
            Check(!output.ToString().Contains("stage=Complete|status=Passed"));
        }
        foreach (var safetyError in new Exception[] { new IOException("PRIVATE-trust-file"), new InvalidOperationException("PRIVATE-TLS-mode") })
        {
            using var output = new StringWriter();
            var called = false; var refused = false;
            try
            {
                await Program.RunStages(true, () => throw safetyError,
                    () => { called = true; return Task.CompletedTask; }, () => { }, new DiagnosticTrace(output));
            }
            catch (Exception error) when (ReferenceEquals(error, safetyError)) { refused = true; }
            Check(refused && !called); // Unwrapped safety errors stop; only classified probe I/O may continue.
        }
        foreach (var wholeRunCanceled in new[] { false, true })
        {
            using var output = new StringWriter();
            using var canceled = new CancellationTokenSource();
            var called = false; var refused = false; var exit = -1;
            try
            {
                exit = await Program.RunStages(true, () => {
                    if (wholeRunCanceled) canceled.Cancel();
                    throw new ProtocolFailureException(new OperationCanceledException("PRIVATE"));
                }, () => { called = true; return Task.CompletedTask; },
                () => canceled.Token.ThrowIfCancellationRequested(), new DiagnosticTrace(output));
            }
            catch (OperationCanceledException) { refused = true; }
            Check(wholeRunCanceled ? refused && !called : !refused && called && exit == 1);
            Check(!output.ToString().Contains("stage=Complete|status=Passed"));
        }
        Console.WriteLine($"PASS: {checks} offline stage independence checks; synthetic managed opening, in-memory SSLRequest, safety refusal and cancellation; no owner configuration or network accessed.");
    }

    private sealed class ReplyStream(byte reply) : MemoryStream(new[] { reply }, writable: false)
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
