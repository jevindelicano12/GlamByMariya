namespace GlamByMariya.Infrastructure.Integrations;

// Supabase keys never contain whitespace. A key pasted into the host's settings can pick up
// spaces or new lines (at the ends or where it wrapped); HTTP headers refuse new lines, so drop them all.
public static class StorageKey
{
    public static string Clean(string? key) =>
        string.Concat((key ?? "").Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)));
}
