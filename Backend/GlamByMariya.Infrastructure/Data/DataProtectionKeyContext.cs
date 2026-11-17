using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace GlamByMariya.Infrastructure.Data;

/// <summary>
/// Database-backed ASP.NET Core Data Protection key ring. Its table is created
/// only by the additive Public migration and is not mapped by browser models.
/// </summary>
public sealed class DataProtectionKeyContext(DbContextOptions<DataProtectionKeyContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("public");
        modelBuilder.Entity<DataProtectionKey>(entity =>
        {
            entity.ToTable("data_protection_keys");
            entity.HasKey(key => key.Id);
            entity.Property(key => key.Id).ValueGeneratedOnAdd();
            entity.Property(key => key.FriendlyName).HasColumnType("text");
            entity.Property(key => key.Xml).HasColumnType("text");
        });
    }
}
