using System.Text.Json;
using Bunting.Domain;
using Bunting.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Bunting.Server.Data;

public sealed class BuntingDbContext(DbContextOptions<BuntingDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectEnvironment> Environments => Set<ProjectEnvironment>();
    public DbSet<Flag> Flags => Set<Flag>();
    public DbSet<FlagEnvironmentConfig> FlagConfigs => Set<FlagEnvironmentConfig>();
    public DbSet<Segment> Segments => Set<Segment>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(e =>
        {
            e.ToTable("projects");
            e.HasKey(x => x.Id);
            e.Property(x => x.Key).HasMaxLength(63);
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => x.Key).IsUnique();
        });

        modelBuilder.Entity<ProjectEnvironment>(e =>
        {
            e.ToTable("environments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Key).HasMaxLength(63);
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ProjectId, x.Key }).IsUnique();
        });

        modelBuilder.Entity<Flag>(e =>
        {
            e.ToTable("flags");
            e.HasKey(x => x.Id);
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Variants).HasJsonConversion();
            e.Property(x => x.Tags).HasJsonConversion();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ProjectId, x.Key }).IsUnique();
        });

        modelBuilder.Entity<FlagEnvironmentConfig>(e =>
        {
            e.ToTable("flag_environment_configs");
            e.HasKey(x => new { x.FlagId, x.EnvironmentId });
            e.Property(x => x.OffVariant).HasMaxLength(100);
            e.Property(x => x.Rules).HasJsonConversion();
            e.Property(x => x.Fallthrough).HasJsonConversion();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<Flag>().WithMany().HasForeignKey(x => x.FlagId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ProjectEnvironment>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.EnvironmentId);
        });

        modelBuilder.Entity<Segment>(e =>
        {
            e.ToTable("segments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.Included).HasJsonConversion();
            e.Property(x => x.Excluded).HasJsonConversion();
            e.Property(x => x.Conditions).HasJsonConversion();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<ProjectEnvironment>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.EnvironmentId, x.Key }).IsUnique();
        });

        modelBuilder.Entity<ApiKey>(e =>
        {
            e.ToTable("api_keys");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasOne<ProjectEnvironment>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.KeyHash).IsUnique();
        });

        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_entries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Actor).HasMaxLength(200);
            e.Property(x => x.EntityType).HasMaxLength(50);
            e.Property(x => x.EntityId).HasMaxLength(200);
            e.Property(x => x.Action).HasMaxLength(50);
            e.Property(x => x.BeforeJson).HasColumnType("jsonb");
            e.Property(x => x.AfterJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.ProjectId, x.Timestamp });
            e.HasIndex(x => new { x.EntityType, x.EntityId });
        });
    }
}

internal static class JsonColumn
{
    /// <summary>Stores a value as <c>jsonb</c>, comparing by serialised form for change detection.</summary>
    public static PropertyBuilder<T> HasJsonConversion<T>(this PropertyBuilder<T> property)
    {
        var converter = new ValueConverter<T, string>(
            v => BuntingJson.Serialize(v),
            s => BuntingJson.Deserialize<T>(s)!);
        var comparer = new ValueComparer<T>(
            (a, b) => BuntingJson.Serialize(a) == BuntingJson.Serialize(b),
            v => BuntingJson.Serialize(v).GetHashCode(StringComparison.Ordinal),
            v => BuntingJson.Deserialize<T>(BuntingJson.Serialize(v))!);
        property.HasColumnType("jsonb").HasConversion(converter, comparer);
        return property;
    }
}

/// <summary>Used by <c>dotnet ef</c>; no database connection is made at design time.</summary>
public sealed class DesignTimeFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<BuntingDbContext>
{
    public BuntingDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<BuntingDbContext>()
            .UseNpgsql("Host=localhost;Database=bunting_design")
            .Options);
}
