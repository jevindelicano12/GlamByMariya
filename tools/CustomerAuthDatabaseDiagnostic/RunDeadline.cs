using System.Diagnostics;
using System.Globalization;

namespace GlamByMariya.CustomerAuthDatabaseDiagnostic;

internal static class RunDeadline
{
    internal const string Variable = "GLAM_V2_DIAGNOSTIC_DEADLINE";
    internal const string CancelVariable = "GLAM_V2_DIAGNOSTIC_CANCEL";

    internal static long After(int milliseconds) =>
        checked(Stopwatch.GetTimestamp() + (long)(milliseconds * (double)Stopwatch.Frequency / 1000));

    internal static int Remaining(long deadline)
    {
        var remaining = (deadline - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency;
        if (remaining < 1) throw new OperationCanceledException("Diagnostic budget exhausted.");
        return (int)Math.Min(int.MaxValue, remaining);
    }

    internal static long FromSupervisor()
    {
        if (!long.TryParse(Environment.GetEnvironmentVariable(Variable), NumberStyles.None,
                CultureInfo.InvariantCulture, out var deadline) || Remaining(deadline) > 75000 ||
            !WindowsDiagnosticJob.CurrentProcessIsContained())
            throw new InvalidOperationException("Independent diagnostic supervision required.");
        return deadline;
    }
}
