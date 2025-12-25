using Hart.MCP.Core.Entities;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Hart.MCP.Core.Data;

/// <summary>
/// EF Core DbContext for HART-MCP database
/// Single-table design: everything stored in 'atom' table
/// </summary>
public class HartDbContext : DbContext
{
    public HartDbContext(DbContextOptions<HartDbContext> options) : base(options)
    {
    }

    public DbSet<Atom> Atoms { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Atom>(entity =>
        {
            entity.ToTable("atom");

            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id)
                .HasColumnName("id")
                .ValueGeneratedOnAdd();

            entity.Property(e => e.HilbertHigh)
                .HasColumnName("hilbert_high")
                .IsRequired();

            entity.Property(e => e.HilbertLow)
                .HasColumnName("hilbert_low")
                .IsRequired();

            entity.Property(e => e.Geom)
                .HasColumnName("geom")
                .HasColumnType("geometry(GeometryZM, 0)")
                .IsRequired();

            entity.Property(e => e.IsConstant)
                .HasColumnName("is_constant")
                .IsRequired();

            entity.Property(e => e.SeedValue)
                .HasColumnName("seed_value");

            entity.Property(e => e.SeedType)
                .HasColumnName("seed_type");

            entity.Property(e => e.Refs)
                .HasColumnName("refs");

            entity.Property(e => e.Multiplicities)
                .HasColumnName("multiplicities");

            entity.Property(e => e.ContentHash)
                .HasColumnName("content_hash")
                .HasMaxLength(32)
                .IsRequired();

            entity.Property(e => e.AtomType)
                .HasColumnName("atom_type")
                .HasMaxLength(64);

            entity.Property(e => e.Metadata)
                .HasColumnName("metadata")
                .HasColumnType("jsonb");

            entity.Property(e => e.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("NOW()")
                .ValueGeneratedOnAdd();

            // Indexes for performance
            entity.HasIndex(e => e.Geom)
                .HasMethod("GIST");

            entity.HasIndex(e => new { e.HilbertHigh, e.HilbertLow });

            entity.HasIndex(e => e.ContentHash)
                .IsUnique();

            entity.HasIndex(e => e.IsConstant);

            entity.HasIndex(e => e.AtomType);

            entity.HasIndex(e => e.SeedValue);

            entity.HasIndex(e => e.CreatedAt);

            // GIN index for JSONB metadata queries
            entity.HasIndex(e => e.Metadata)
                .HasMethod("GIN");

            // GIN index for refs array containment queries
            entity.HasIndex(e => e.Refs)
                .HasMethod("GIN");
        });
    }

    /// <summary>
    /// Seed database with full Unicode (1.1M codepoints) using native bulk ingestion.
    /// Call this after EnsureCreated() or after migrations.
    /// </summary>
    public async Task SeedUnicodeAsync(string connectionString, bool fullUnicode = true)
    {
        // Check if already seeded
        var hasUnicode = await Atoms.AnyAsync(a => a.AtomType == "unicode");
        if (hasUnicode)
            return;

        var service = new Hart.MCP.Core.Services.Ingestion.NativeBulkIngestionService(connectionString);
        try
        {
            await service.SeedUnicodeAsync(fullUnicode);
        }
        finally
        {
            service.Dispose();
        }
    }
}
