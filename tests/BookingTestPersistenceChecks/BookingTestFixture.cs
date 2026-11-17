using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

// Offline model and configuration review only. This is deliberately NOT an
// IPersistentStudioStore, is not registered by the MVC application, and has no
// database provider, data source, connection factory, query or write method.
internal static class BookingTestFixture
{
    internal const string Schema = "booking_test";
    private static readonly string[] RequiredTables = ["fixture_runs", "artists", "conflict_reservations"];

    internal static void ValidateTarget(bool explicitlyEnabled, string connectionString, string schema)
    {
        if (!explicitlyEnabled || schema != Schema)
            throw new InvalidOperationException("The isolated booking fixture is disabled or has the wrong schema.");
        NpgsqlConnectionStringBuilder target;
        try { target = new NpgsqlConnectionStringBuilder(connectionString); }
        catch (ArgumentException) { throw new InvalidOperationException("Invalid isolated booking fixture target."); }
        if (target.Host != "127.0.0.1" || target.Port != 5432 ||
            target.Database != "glam_auth_mvc_workflow_test" || target.Username != "booking_test_runtime")
            throw new InvalidOperationException("Unexpected isolated booking fixture target.");
    }

    internal static void ValidateTableNames(IEnumerable<string> names)
    {
        var found = names.ToHashSet(StringComparer.Ordinal);
        if (RequiredTables.Any(table => !found.Contains(table)))
            throw new InvalidOperationException("Required isolated booking fixture tables are missing.");
    }

    // Registers metadata only: even opt-in cannot open or create a DB connection.
    internal static void AddOfflineMetadata(IServiceCollection services, bool explicitlyEnabled, string connectionString, string schema)
    {
        ValidateTarget(explicitlyEnabled, connectionString, schema);
        services.AddDbContext<ConflictFixtureContext>();
    }
}

internal sealed class ConflictFixtureContext(DbContextOptions<ConflictFixtureContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<FixtureRun>(e =>
        {
            e.ToTable("fixture_runs", BookingTestFixture.Schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });
        model.Entity<FixtureArtist>(e =>
        {
            e.ToTable("artists", BookingTestFixture.Schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.RunId).HasColumnName("run_id");
            e.HasOne<FixtureRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ConflictReservation>(e =>
        {
            e.ToTable("conflict_reservations", BookingTestFixture.Schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.RunId).HasColumnName("run_id");
            e.Property(x => x.CustomerIdentity).HasColumnName("customer_identity");
            e.Property(x => x.ArtistId).HasColumnName("artist_id");
            e.Property(x => x.StartAt).HasColumnName("start_at");
            e.Property(x => x.EndAt).HasColumnName("end_at");
            e.Property(x => x.ReservedUntil).HasColumnName("reserved_until");
            e.Property(x => x.Status).HasColumnName("status");
            e.HasOne<FixtureRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<FixtureArtist>().WithMany().HasForeignKey(x => x.ArtistId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

internal sealed class FixtureRun { public Guid Id { get; set; } public DateTimeOffset CreatedAt { get; set; } }
internal sealed class FixtureArtist { public Guid Id { get; set; } public Guid RunId { get; set; } }
internal sealed class ConflictReservation
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid CustomerIdentity { get; set; }
    public Guid ArtistId { get; set; }
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset EndAt { get; set; }
    public DateTimeOffset ReservedUntil { get; set; }
    public string Status { get; set; } = "";
}
