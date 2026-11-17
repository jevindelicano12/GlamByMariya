using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GlamByMariya.CustomerAuthDatabaseDiagnostic;

internal static class DiagnosticSupervisor
{
    internal static Dictionary<string, string> EnvironmentForChild()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
                     "TEMP", "TMP", "DOTNET_ROOT", "PROCESSOR_ARCHITECTURE", "ProgramFiles", "ProgramFiles(x86)", "PROGRAMDATA", "SystemDrive" })
            if (Environment.GetEnvironmentVariable(name) is { } value) environment[name] = value;
        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        return environment;
    }

    internal static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
        @"WindowsPowerShell\v1.0\powershell.exe");

    internal static async Task<int> Run(string script, string mode)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(script) || script.Contains('"') ||
            mode is not ("consolidated" or "disable-gss" or "original"))
            throw new InvalidOperationException("Invalid diagnostic supervisor invocation.");
        var flags = mode == "consolidated" ? " -Consolidated" : mode == "disable-gss" ? " -DisableGss" : "";
        return await Supervise(PowerShell,
            "-NoLogo -NoProfile -NonInteractive -File \"" + Path.GetFullPath(script) + "\" -OwnerApproved -SupervisedWorker" + flags,
            Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(script)))!, 75000, 90000, Console.Out);
    }

    internal static async Task<int> Supervise(string executable, string arguments, string directory,
        int cancelMilliseconds, int killMilliseconds, TextWriter output)
    {
        var started = Stopwatch.GetTimestamp();
        var deadline = started + (long)(cancelMilliseconds * (double)Stopwatch.Frequency / 1000);
        var hardDeadline = started + (long)(killMilliseconds * (double)Stopwatch.Frequency / 1000);
        var name = @"Local\GlamV2Diagnostic-" + Guid.NewGuid().ToString("N");
        using var signal = new EventWaitHandle(false, EventResetMode.ManualReset, name, out var created);
        Program.Require(created, "Cancellation event already exists.");
        using var job = new WindowsDiagnosticJob();
        var environment = EnvironmentForChild();
        environment[RunDeadline.Variable] = deadline.ToString(CultureInfo.InvariantCulture);
        environment[RunDeadline.CancelVariable] = name;
        var hardStopped = 0;
        await using var cancelTimer = new Timer(_ => signal.Set(), null, RunDeadline.Remaining(deadline), Timeout.Infinite);
        await using var hardTimer = new Timer(_ =>
        {
            Interlocked.Exchange(ref hardStopped, 1);
            job.Stop();
        }, null, RunDeadline.Remaining(hardDeadline), Timeout.Infinite);
        RunDeadline.Remaining(deadline); // Refuse launch even if setup consumed the whole budget.
        job.Start(executable, arguments, directory, environment);
        var read = ReadSafeOutput(job.Output, output);
        while (!job.Exited) await Task.Delay(10);
        var exit = job.ExitCode;
        job.Stop(); // Clean up descendants even if their parent exited successfully.
        while (job.ActiveProcesses != 0) await Task.Delay(10);
        var (records, failed) = await read;
        if (exit != 0 && !failed)
            await output.WriteLineAsync(DiagnosticTrace.Record(
                records == 0 ? Phase.WorkerInitialization : Phase.OutputCollection, Outcome.Failed,
                Stopwatch.GetElapsedTime(started).Ticks / TimeSpan.TicksPerMillisecond, 0,
                new InvalidOperationException("Worker exited without a structured result.")));
        if (Volatile.Read(ref hardStopped) != 0) return 124;
        if (signal.WaitOne(0) || Stopwatch.GetTimestamp() >= deadline) return 125;
        return exit;
    }

    private static async Task<(int Records, bool Failed)> ReadSafeOutput(StreamReader reader, TextWriter output)
    {
        var records = 0;
        var failed = false;
        while (await reader.ReadLineAsync() is { } line)
            if (Regex.IsMatch(line, @"^DB(DIAG|CONFIG|NATIVE)\|[A-Za-z0-9_=|.-]+$") ||
                Regex.IsMatch(line, @"^Diagnostic child stopped; exit code=[0-9]+; hard deadline reached=(True|False)\. No automatic retry\.$"))
            {
                // Worker emits only its existing strict field whitelist. Do not forward arbitrary PowerShell errors.
                if (line.StartsWith("DB", StringComparison.Ordinal) && !SafeRecord(line)) continue;
                await output.WriteLineAsync(line);
                if (line.StartsWith("DB", StringComparison.Ordinal))
                {
                    records++;
                    failed |= line.Contains("|status=Failed|", StringComparison.Ordinal) ||
                        line.Contains("|status=Refused|", StringComparison.Ordinal);
                }
            }
        return (records, failed);
    }

    private static bool SafeRecord(string line)
    {
        if (line.StartsWith("DBNATIVE|", StringComparison.Ordinal))
        {
            if (line == "DBNATIVE|status=Failed|category=executable-start-failure|exit_code=None") return true;
            var match = Regex.Match(line, @"^DBNATIVE\|status=(Passed|Failed)\|category=(None|process-exit-failure|output-validation-failure|timeout|output-collection-failure|canceled)\|exit_code=(0|-?[1-9][0-9]{0,9})$");
            if (!match.Success || !int.TryParse(match.Groups[3].Value, out var code)) return false;
            var category = match.Groups[2].Value;
            if (match.Groups[1].Value == "Passed") return category == "None" && code == 0;
            return (category == "process-exit-failure" && code != 0) ||
                (category is "output-validation-failure" or "output-collection-failure" && code == 0) ||
                category is "timeout" or "canceled";
        }
        var stages = string.Join('|', Enum.GetNames<Phase>());
        return Regex.IsMatch(line, @"^DBDIAG\|stage=(" + stages +
            @")\|status=(Started|Passed|Failed|Unfinished|Observed|Refused)\|elapsed_ms=[0-9]+\|duration_ms=[0-9]+\|category=(None|client-baseline-failure|startup-failure|database-authentication|database-access-denied|database-schema-or-query|database-capacity|database-query|database-or-https-tls|network|timeout|canceled|cryptography|local-access-denied)\|type=(None|Exception|PostgresException|NpgsqlException|AuthenticationException|SocketException|TimeoutException|CryptographicException|UnauthorizedAccessException|DirectoryNotFoundException|FileNotFoundException|IOException|InvalidOperationException|ArgumentException|OperationCanceledException)\|cause=(None|Exception|PostgresException|NpgsqlException|AuthenticationException|SocketException|TimeoutException|CryptographicException|UnauthorizedAccessException|DirectoryNotFoundException|FileNotFoundException|IOException|InvalidOperationException|ArgumentException|OperationCanceledException)\|code=(None|Other|28P01|28000|42501|42P01|42703|42883|53300|3D000|57P03|XX000|HostNotFound|NoData|NetworkUnreachable|ConnectionRefused|TimedOut)$") ||
            Regex.IsMatch(line, @"^DBCONFIG\|project_pins=Passed\|tls=VerifyFull\|timeout_s=15\|command_timeout_s=30\|pool_min=0\|pool_max=2\|gss=(Prefer|Disable|Require)\|ssl_negotiation=(Postgres|Direct)\|root=(SystemTrust|ExplicitFile)$");
    }
}
