using System.Diagnostics;
using System.Reflection;

namespace GlamByMariya.CustomerAuthDatabaseDiagnostic;

internal static class SupervisionChecks
{
    private static readonly string Dll = Assembly.GetExecutingAssembly().Location;
    private static string Dotnet => Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "dotnet.exe");

    internal static async Task Run()
    {
        Program.Require(OperatingSystem.IsWindows(), "Windows supervision tests required.");
        var checks = 0;
        void Check(bool condition)
        {
            if (!condition) Console.WriteLine($"SUPERVISIONTEST|failed_check={checks + 1}");
            Program.Require(condition, "Offline supervision check failed.");
            checks++;
        }
        var directory = Path.Combine(Path.GetTempPath(), "opencode", "glam-supervision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var phase = "ExpiredLaunch";
        try
        {
            // Exhaustion is tested before any worker/configuration access, not clamped to 1 ms.
            var path = Path.Combine(directory, "expired");
            var refused = false;
            try { await DiagnosticSupervisor.Supervise(Dotnet, Arguments("success", path), directory, 0, 1000, TextWriter.Null); }
            catch (OperationCanceledException) { refused = true; }
            Check(refused && !File.Exists(path));

            using (var output = new StringWriter())
            {
                phase = "Success";
                path = Path.Combine(directory, "success");
                var exit = await DiagnosticSupervisor.Supervise(Dotnet, Arguments("success", path), directory, 5000, 7000, output);
                Check(exit == 0 && ReadPids(path).Length == 1);
                Check(output.ToString().Contains("stage=Complete") && !output.ToString().Contains("PRIVATE"));
                Check(AllStopped(path));
            }

            path = Path.Combine(directory, "cooperative");
            phase = "Cancellation";
            var clock = Stopwatch.StartNew();
            var result = await DiagnosticSupervisor.Supervise(Dotnet, Arguments("cooperative", path), directory, 1200, 4000, TextWriter.Null);
            Check(result == 125 && File.Exists(path + ".cancelled"));
            Check(clock.ElapsedMilliseconds < 3500 && AllStopped(path));

            path = Path.Combine(directory, "hang");
            phase = "HardStop";
            clock.Restart();
            result = await DiagnosticSupervisor.Supervise(Dotnet, Arguments("hang", path), directory, 1200, 2400, TextWriter.Null);
            Check(result == 124 && clock.ElapsedMilliseconds < 5000);
            Check(ReadPids(path).Length == 3 && AllStopped(path)); // Worker, child and grandchild.

            path = Path.Combine(directory, "orphan");
            phase = "OrphanCleanup";
            result = await DiagnosticSupervisor.Supervise(Dotnet, Arguments("orphan", path), directory, 5000, 7000, TextWriter.Null);
            Check(result == 0 && ReadPids(path).Length == 3 && AllStopped(path));

            path = Path.Combine(directory, "failure");
            phase = "FailureNoRetry";
            result = await DiagnosticSupervisor.Supervise(Dotnet, Arguments("failure", path), directory, 5000, 7000, TextWriter.Null);
            Check(result == 7 && ReadPids(path).Length == 1 && AllStopped(path)); // No retry.

            // Exercise the actual PowerShell budget functions without dot-sourcing the executable script.
            var script = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Dll)!, "..", "..", "..", "..",
                "Run-CustomerAuthDatabaseDiagnostic.ps1"));
            phase = "PowerShellBudgets";
            var command = """
                $ErrorActionPreference='Stop'
                $ast=[Management.Automation.Language.Parser]::ParseFile('__SCRIPT__',[ref]$null,[ref]$null)
                foreach($name in @('Get-RemainingBudget','Wait-DiagnosticChild')){
                    $fn=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
                    Invoke-Expression $fn.Extent.Text
                }
                $deadline=[long]::Parse($env:GLAM_V2_DIAGNOSTIC_DEADLINE)
                $cancelSignal=[Threading.EventWaitHandle]::OpenExisting($env:GLAM_V2_DIAGNOSTIC_CANCEL)
                try {
                    if((Get-RemainingBudget) -le 0){throw 'Positive deadline refused'}
                    $deadline=[Diagnostics.Stopwatch]::GetTimestamp()-1
                    $refused=$false
                    try {$null=Get-RemainingBudget} catch {$refused=$true}
                    if(!$refused){throw 'Exhausted launch accepted'}
                    $deadline=[long]::Parse($env:GLAM_V2_DIAGNOSTIC_DEADLINE)
                    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'powershell.exe'))
                    $start.UseShellExecute=$false
                    $start.CreateNoWindow=$true
                    $start.Arguments='-NoLogo -NoProfile -NonInteractive -Command "[Threading.Thread]::Sleep(60000)"'
                    $null=Get-RemainingBudget
                    $child=[Diagnostics.Process]::Start($start)
                    try {
                        if(Wait-DiagnosticChild 50){throw 'Stage ceiling ignored'}
                        $cancelSignal.Dispose()
                        $cancelSignal=[Threading.EventWaitHandle]::new($true,[Threading.EventResetMode]::ManualReset)
                        $refused=$false
                        try {$null=Wait-DiagnosticChild 5000} catch {$refused=$true}
                        if(!$refused){throw 'Cancellation ignored during child wait'}
                    } finally {
                        if(!$child.HasExited){$child.Kill();$null=$child.WaitForExit(1000)}
                        $child.Dispose()
                    }
                    exit 0
                } finally {$cancelSignal.Dispose()}
                """.Replace("__SCRIPT__", script.Replace("'", "''"));
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));
            result = await DiagnosticSupervisor.Supervise(DiagnosticSupervisor.PowerShell,
                "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + encoded, directory, 10000, 12000, TextWriter.Null);
            if (result != 0) Console.WriteLine($"SUPERVISIONTEST|powershell_fixture_exit={result}");
            Check(result == 0);

            path = Path.Combine(directory, "interrupted");
            phase = "SupervisorInterruption";
            using (var guard = Process.Start(new ProcessStartInfo(Dotnet)
            { Arguments = Arguments("guard", path), UseShellExecute = false, CreateNoWindow = true })!)
            {
                try
                {
                    clock.Restart();
                    while (ReadPids(path).Length < 3 && !guard.HasExited && clock.ElapsedMilliseconds < 5000) await Task.Delay(10);
                    Check(ReadPids(path).Length == 3);
                    guard.Kill(); // Simulate interruption of the independent supervisor itself.
                    Check(guard.WaitForExit(1000) && AllStopped(path));
                }
                finally { if (!guard.HasExited) { guard.Kill(); guard.WaitForExit(1000); } }
            }

            Console.WriteLine($"PASS: {checks} offline supervision checks: expired launch refusal, absolute soft deadline, independent hard stop, three-generation containment, orphan cleanup, failure/no retry and output redaction. No owner configuration or network accessed.");
        }
        catch
        {
            Console.WriteLine("SUPERVISIONTEST|phase=" + phase + "|status=Failed");
            throw;
        }
        finally
        {
            var deferred = false;
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                try { File.Delete(file); }
                catch (IOException) { deferred = true; }
            }
            if (!deferred)
            {
                try { Directory.Delete(directory); }
                catch (IOException) { deferred = true; }
            }
            // Process termination is asserted above. Windows can retain transient file/directory locks afterward.
            if (deferred) Console.WriteLine("SUPERVISIONTEST|cleanup=Deferred|contents=PidMarkersOnly|retry=False");
        }
    }

    // Harmless test workers: PID-only files, no sockets, owner configuration or provider code.
    internal static async Task<int> Fixture(string scenario, string path)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Windows test fixture required.");
        if (scenario == "guard")
            return await DiagnosticSupervisor.Supervise(Dotnet, Arguments("hang", path),
                Path.GetDirectoryName(path)!, 10000, 12000, TextWriter.Null);
        RunDeadline.FromSupervisor();
        File.AppendAllText(path, Environment.ProcessId + Environment.NewLine);
        if (scenario == "success")
        {
            Console.WriteLine("PRIVATE-synthetic-do-not-export");
            Console.WriteLine(DiagnosticTrace.Record(Phase.Complete, Outcome.Passed, 0, 0));
            return 0;
        }
        if (scenario == "failure") return 7;
        if (scenario == "cooperative")
        {
            // Simulate configuration loading before cancellation setup, without resetting the absolute deadline.
            await Task.Delay(350);
            using var signal = EventWaitHandle.OpenExisting(Environment.GetEnvironmentVariable(RunDeadline.CancelVariable)!);
            signal.WaitOne();
            File.WriteAllText(path + ".cancelled", "cancelled");
            return 0;
        }
        if (scenario is "hang" or "orphan" or "descendant")
        {
            using var child = Process.Start(new ProcessStartInfo(Dotnet)
            {
                Arguments = Arguments(scenario == "descendant" ? "leaf" : "descendant", path),
                UseShellExecute = false, CreateNoWindow = true
            })!;
            if (scenario == "orphan")
            {
                var timeout = Stopwatch.StartNew();
                while (ReadPids(path).Length < 3 && timeout.ElapsedMilliseconds < 3000) await Task.Delay(10);
                return 0;
            }
        }
        await Task.Delay(Timeout.Infinite);
        return 0;
    }

    private static string Arguments(string scenario, string path) =>
        "\"" + Dll + "\" --supervision-fixture " + scenario + " \"" + path + "\"";

    private static int[] ReadPids(string path)
    {
        if (!File.Exists(path)) return [];
        // Fixtures append while the test observes readiness. Do not deny their write handle or accept a partial PID.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n').SkipLast(1)
            .Where(line => int.TryParse(line, out _)).Select(int.Parse).ToArray();
    }

    private static bool AllStopped(string path) => ReadPids(path).All(pid =>
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited || process.WaitForExit(1000); }
        catch (ArgumentException) { return true; }
    });
}
