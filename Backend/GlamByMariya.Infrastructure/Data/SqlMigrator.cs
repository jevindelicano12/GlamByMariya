using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace GlamByMariya.Infrastructure.Data;

// Applies database/migrations/NNNN_name.sql in order, one transaction per file, and records
// each in public.schema_migrations. Files never contain BEGIN/COMMIT. A recorded file that
// changed or disappeared, a gap or a duplicate number stops the run before anything is applied.
public static partial class SqlMigrator
{
    private const long LockKey1 = 718430512, LockKey2 = 2;

    [GeneratedRegex(@"^(\d{4})_([a-z0-9_]+)\.sql$")]
    private static partial Regex FileName();

    // Same checksum for CRLF (Windows checkout) and LF (Linux image) copies of a file.
    public static string Checksum(string fileText) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fileText.Replace("\r\n", "\n")))).ToLowerInvariant();

    public static async Task<IReadOnlyList<string>> ApplyAsync(NpgsqlDataSource source, string directory, CancellationToken ct = default)
    {
        var files = Directory.GetFiles(directory, "*.sql")
            .Select(path => (Path: path, Name: Path.GetFileName(path), Match: FileName().Match(Path.GetFileName(path))))
            .ToList();
        var invalid = files.FirstOrDefault(f => !f.Match.Success);
        if (invalid.Name != null) throw new InvalidOperationException($"Migration file name must look like 0001_name.sql: {invalid.Name}");
        var ordered = files.Select(f => (f.Path, f.Name, Version: int.Parse(f.Match.Groups[1].Value)))
            .OrderBy(f => f.Version).ToList();
        for (var i = 0; i < ordered.Count; i++)
            if (ordered[i].Version != i + 1)
                throw new InvalidOperationException($"Migration numbers must run 0001, 0002, … with no gap or duplicate; found {ordered[i].Name}.");

        await using var connection = await source.OpenConnectionAsync(ct);
        await Execute(connection, null, $"SELECT pg_advisory_lock({LockKey1}, {LockKey2})", ct);
        try
        {
            await Execute(connection, null, """
                CREATE TABLE IF NOT EXISTS public.schema_migrations(
                  version int PRIMARY KEY, name text NOT NULL, checksum text NOT NULL,
                  applied_at timestamptz NOT NULL DEFAULT now())
                """, ct);
            var recorded = new Dictionary<int, (string Name, string Checksum)>();
            await using (var read = new NpgsqlCommand("SELECT version, name, checksum FROM public.schema_migrations", connection))
            await using (var reader = await read.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) recorded[reader.GetInt32(0)] = (reader.GetString(1), reader.GetString(2));

            var byVersion = ordered.ToDictionary(f => f.Version);
            foreach (var (version, row) in recorded.OrderBy(r => r.Key))
            {
                if (!byVersion.TryGetValue(version, out var file) || file.Name != row.Name)
                    throw new InvalidOperationException($"Applied migration {row.Name} is missing from {directory}.");
                if (Checksum(await File.ReadAllTextAsync(file.Path, ct)) != row.Checksum)
                    throw new InvalidOperationException($"Applied migration {row.Name} was changed after it ran. Add a new migration instead.");
            }

            var applied = new List<string>();
            foreach (var file in ordered.Where(f => !recorded.ContainsKey(f.Version)))
            {
                var text = await File.ReadAllTextAsync(file.Path, ct);
                await using var transaction = await connection.BeginTransactionAsync(ct);
                await Execute(connection, transaction, text, ct);
                await using (var record = new NpgsqlCommand("INSERT INTO public.schema_migrations(version, name, checksum) VALUES(@v, @n, @c)", connection, transaction))
                {
                    record.Parameters.AddWithValue("v", file.Version);
                    record.Parameters.AddWithValue("n", file.Name);
                    record.Parameters.AddWithValue("c", Checksum(text));
                    await record.ExecuteNonQueryAsync(ct);
                }
                await transaction.CommitAsync(ct);
                applied.Add(file.Name);
            }
            return applied;
        }
        finally
        {
            if (connection.State == System.Data.ConnectionState.Open)
                await Execute(connection, null, $"SELECT pg_advisory_unlock({LockKey1}, {LockKey2})", CancellationToken.None);
        }
    }

    private static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync(ct);
    }
}
