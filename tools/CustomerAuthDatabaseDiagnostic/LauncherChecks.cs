using System.Diagnostics;
using System.Reflection;

namespace GlamByMariya.CustomerAuthDatabaseDiagnostic;

// Copies only the launch/filter block into fixtures; the real network worker is never included.
internal static class LauncherChecks
{
    internal static async Task Run()
    {
        var dll = Assembly.GetExecutingAssembly().Location;
        var tools = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(dll)!, "..", "..", "..", ".."));
        var script = File.ReadAllText(Path.Combine(tools, "Run-CustomerAuthDatabaseDiagnostic.ps1"));
        var prefix = script[..script.IndexOf("function New-PsqlBaselineInfo", StringComparison.Ordinal)];
        var start = script.IndexOf("if (!$SupervisedWorker) {", StringComparison.Ordinal);
        var outer = script[start..script.IndexOf("$child = $null", start, StringComparison.Ordinal)];
        var catchStart = script.LastIndexOf("} catch {", StringComparison.Ordinal) + "} catch {".Length;
        var workerCatch = script[catchStart..script.IndexOf("} finally {", catchStart, StringComparison.Ordinal)];
        var directory = Path.Combine(tools, "offline-launcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var count = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Offline launcher check failed: " + label);
            count++;
        }
        Check(outer.Contains("Get-Command dotnet.exe -CommandType Application", StringComparison.Ordinal),
            "explicit executable lookup");
        Check(script.Split("Get-Command dotnet.exe -CommandType Application", StringSplitOptions.None).Length == 3,
            "both parent and managed-child executable lookups");
        Check(script.Contains("Wait-DiagnosticChild 45000", StringComparison.Ordinal) &&
            script.Contains("Wait-DiagnosticChild 75000", StringComparison.Ordinal) &&
            script.Contains("90000-$launcherClock.ElapsedMilliseconds", StringComparison.Ordinal),
            "production limits unchanged");
        var nativeRecords = new[]
        {
            "DBNATIVE|status=Passed|category=None|exit_code=0",
            "DBNATIVE|status=Failed|category=executable-start-failure|exit_code=None",
            "DBNATIVE|status=Failed|category=process-exit-failure|exit_code=7",
            "DBNATIVE|status=Failed|category=process-exit-failure|exit_code=-2147483648",
            "DBNATIVE|status=Failed|category=process-exit-failure|exit_code=2147483647",
            "DBNATIVE|status=Failed|category=output-validation-failure|exit_code=0",
            "DBNATIVE|status=Failed|category=output-collection-failure|exit_code=0",
            "DBNATIVE|status=Failed|category=timeout|exit_code=1",
            "DBNATIVE|status=Failed|category=canceled|exit_code=1"
        };
        var nativeBody = "Write-Output " + string.Join(',', nativeRecords.Select(record => "'" + record + "'")) +
            ",'DBNATIVE|status=Failed|category=process-exit-failure|exit_code=0'," +
            "'DBNATIVE|status=Passed|category=process-exit-failure|exit_code=7'," +
            "'DBNATIVE|status=Failed|category=timeout|exit_code=2147483648'," +
            "'DBDIAG|stage=PsqlBaseline|status=Failed|elapsed_ms=1|duration_ms=1|category=client-baseline-failure|type=None|cause=None|code=None'; exit 1";
        foreach (var test in new[]
        {
            (Name: "success", Body: "Write-Output 'PRIVATE-synthetic'; Write-Output 'DBDIAG|stage=Complete|status=Passed|elapsed_ms=0|duration_ms=0|category=None|type=None|cause=None|code=None'; exit 0", Exit: 0, Stage: "Complete", Status: "Passed"),
            (Name: "initialization", Body: "$clock=[Diagnostics.Stopwatch]::StartNew(); $workerStage='WorkerInitialization'; try { throw 'PRIVATE-synthetic' } catch {" + workerCatch + "}", Exit: 1, Stage: "WorkerInitialization", Status: "Failed"),
            (Name: "silent-failure", Body: "exit 7", Exit: 7, Stage: "WorkerInitialization", Status: "Failed"),
            (Name: "progress-then-failure", Body: "Write-Output 'DBDIAG|stage=Configuration|status=Started|elapsed_ms=0|duration_ms=0|category=None|type=None|cause=None|code=None'; exit 7", Exit: 7, Stage: "OutputCollection", Status: "Failed"),
            (Name: "stderr-only", Body: "[Console]::Error.WriteLine('PRIVATE-synthetic'); exit 9", Exit: 9, Stage: "WorkerInitialization", Status: "Failed"),
            (Name: "malformed-record", Body: "Write-Output 'DBDIAG|password=PRIVATE-synthetic'; exit 8", Exit: 8, Stage: "WorkerInitialization", Status: "Failed"),
            (Name: "silent-success", Body: "exit 0", Exit: 1, Stage: "OutputCollection", Status: "Failed"),
            (Name: "missing-runtime", Body: "exit 0", Exit: 1, Stage: "Launcher", Status: "Failed"),
            (Name: "missing-binary", Body: "exit 0", Exit: 2, Stage: "Launcher", Status: "Refused"),
            (Name: "native-failure", Body: "Write-Output 'DBNATIVE|status=Failed|category=process-exit-failure|exit_code=7','DBDIAG|stage=PsqlBaseline|status=Failed|elapsed_ms=1|duration_ms=1|category=client-baseline-failure|type=None|cause=None|code=None'; exit 1", Exit: 1, Stage: "PsqlBaseline", Status: "Failed"),
            (Name: "native-success", Body: "Write-Output 'DBNATIVE|status=Passed|category=None|exit_code=0','DBDIAG|stage=Complete|status=Passed|elapsed_ms=1|duration_ms=1|category=None|type=None|cause=None|code=None'; exit 0", Exit: 0, Stage: "Complete", Status: "Passed"),
            (Name: "native-negative", Body: "Write-Output 'DBNATIVE|status=Failed|category=process-exit-failure|exit_code=-2147483648','DBNATIVE|status=Failed|category=PRIVATE|exit_code=7','DBNATIVE|status=Passed|category=None|exit_code=0|password=PRIVATE','DBDIAG|stage=PsqlBaseline|status=Failed|elapsed_ms=1|duration_ms=1|category=client-baseline-failure|type=None|cause=None|code=None'; exit 1", Exit: 1, Stage: "PsqlBaseline", Status: "Failed"),
            (Name: "native-classifications", Body: nativeBody, Exit: 1, Stage: "PsqlBaseline", Status: "Failed")
        })
        {
            try
            {
                var marker = Path.Combine(directory, test.Name + ".pid");
                var fixture = Path.Combine(directory, test.Name + ".ps1");
                var caseOuter = outer.Replace(
                    "Join-Path $PSScriptRoot 'CustomerAuthDatabaseDiagnostic\\bin\\Debug\\net10.0\\CustomerAuthDatabaseDiagnostic.dll'",
                    "'" + dll.Replace("'", "''") + "'", StringComparison.Ordinal);
                if (test.Name == "missing-binary")
                    caseOuter = caseOuter.Replace("'" + dll.Replace("'", "''") + "'",
                        "'" + Path.Combine(directory, "absent.dll").Replace("'", "''") + "'", StringComparison.Ordinal);
                var worker = "if ($SupervisedWorker) { [IO.File]::AppendAllText('" +
                    marker.Replace("'", "''") + "',([string]$PID+[Environment]::NewLine)); " + test.Body + " }\n";
                File.WriteAllText(fixture, prefix + worker + caseOuter);
                var info = new ProcessStartInfo(DiagnosticSupervisor.PowerShell)
                {
                    Arguments = "-NoLogo -NoProfile -NonInteractive -File \"" + fixture + "\" -OwnerApproved -Consolidated",
                    WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                info.Environment.Clear();
                foreach (var pair in DiagnosticSupervisor.EnvironmentForChild()) info.Environment[pair.Key] = pair.Value;
                // Force extension-independent lookup, even when the invoking shell has PATHEXT.
                info.Environment.Remove("PATHEXT");
                if (test.Name == "missing-runtime") info.Environment["PATH"] = Path.GetDirectoryName(DiagnosticSupervisor.PowerShell)!;
                using var process = Process.Start(info)!;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                try { await process.WaitForExitAsync(deadline.Token); }
                catch { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(1000); } throw; }
                var output = await stdout;
                var errors = await stderr;
                Check(process.ExitCode == test.Exit, "exit code");
                Check(output.Contains($"DBDIAG|stage={test.Stage}|status={test.Status}|", StringComparison.Ordinal),
                    "structured outcome survives all output filters");
                Check(!output.Contains("PRIVATE", StringComparison.Ordinal) &&
                    !errors.Contains("PRIVATE", StringComparison.Ordinal), "synthetic private text never escapes");
                Check(output.Split('\n').Where(line => line.StartsWith("DBDIAG|", StringComparison.Ordinal))
                    .All(line => line.TrimEnd('\r').Split('|').Length == 9), "fixed record shape");
                if (test.Name.StartsWith("native-", StringComparison.Ordinal))
                    Check(output.Split('\n').Count(line => line.StartsWith("DBNATIVE|", StringComparison.Ordinal)) ==
                        (test.Name == "native-classifications" ? nativeRecords.Length : 1),
                        "native numeric exit survives supervisor and launcher; private/invalid records rejected");
                if (test.Name == "native-classifications")
                    foreach (var record in nativeRecords) Check(output.Contains(record + "\r\n", StringComparison.Ordinal),
                        "every fixed native classification survives actual process-to-supervisor-to-launcher filtering");
                var pids = File.Exists(marker) ? File.ReadAllLines(marker) : [];
                Check(pids.Length == (test.Name is "missing-runtime" or "missing-binary" ? 0 : 1), "worker launch count/no retry");
                foreach (var pid in pids)
                {
                    try { using var workerProcess = Process.GetProcessById(int.Parse(pid)); Check(workerProcess.HasExited || workerProcess.WaitForExit(1000), "worker stopped"); }
                    catch (ArgumentException) { Check(true, "worker stopped"); }
                }
                Console.WriteLine("LAUNCHERTEST|case=" + test.Name + "|status=Passed");
            }
            catch
            {
                Console.WriteLine("LAUNCHERTEST|case=" + test.Name + "|status=Failed");
                throw;
            }
        }
        // Keep the uniquely named, synthetic-only fixture files for review; no existing files are cleaned.
        Console.WriteLine($"PASS: {count} offline launcher checks; no owner configuration, database or network accessed.");
    }
}
