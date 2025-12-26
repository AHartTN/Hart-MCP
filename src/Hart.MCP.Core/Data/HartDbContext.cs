using Hart.MCP.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Hart.MCP.Core.Data;

/// <summary>
/// EF Core DbContext for HART-MCP database.
/// Three tables: constant (leaf nodes), composition (internal nodes), relation (edges).
/// </summary>
public class HartDbContext : DbContext
{
    // Value converter for ulong <-> long (PostgreSQL bigint is signed)
    private static readonly ValueConverter<ulong, long> UlongToLongConverter =
        new(v => unchecked((long)v), v => unchecked((ulong)v));

    public HartDbContext(DbContextOptions<HartDbContext> options) : base(options)
    {
    }

    public DbSet<Constant> Constants { get; set; } = null!;
    public DbSet<Composition> Compositions { get; set; } = null!;
    public DbSet<Relation> Relations { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasPostgresExtension("postgis");

        // --- Constant Configuration ---
        modelBuilder.Entity<Constant>(entity =>
        {
            entity.ToTable("constant");

            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id)
                .HasColumnName("id")
                .ValueGeneratedOnAdd();

            entity.Property(e => e.SeedValue)
                .HasColumnName("seed_value")
                .IsRequired();

            entity.Property(e => e.SeedType)
                .HasColumnName("seed_type")
                .IsRequired();

            entity.Property(e => e.ContentHash)
                .HasColumnName("content_hash")
                .HasMaxLength(32)
                .IsRequired();

            entity.Property(e => e.HilbertHigh)
                .HasColumnName("hilbert_high")
                .HasConversion(UlongToLongConverter)
                .IsRequired();

            entity.Property(e => e.HilbertLow)
                .HasColumnName("hilbert_low")
                .HasConversion(UlongToLongConverter)
                .IsRequired();

            entity.Property(e => e.Geom)
                .HasColumnName("geom")
                .HasColumnType("geometry(PointZM, 0)");

            // Indexes
            entity.HasIndex(e => e.ContentHash)
                .IsUnique();

            entity.HasIndex(e => new { e.SeedType, e.SeedValue });

            entity.HasIndex(e => new { e.HilbertHigh, e.HilbertLow });

            entity.HasIndex(e => e.Geom)
                .HasMethod("GIST");
        });

        // --- Composition Configuration ---
        modelBuilder.Entity<Composition>(entity =>
        {
            entity.ToTable("composition");

            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id)
                .HasColumnName("id")
                .ValueGeneratedOnAdd();

            entity.Property(e => e.ContentHash)
                .HasColumnName("content_hash")
                .HasMaxLength(32)
                .IsRequired();

            entity.Property(e => e.HilbertHigh)
                .HasColumnName("hilbert_high")
                .HasConversion(UlongToLongConverter)
                .IsRequired();

            entity.Property(e => e.HilbertLow)
                .HasColumnName("hilbert_low")
                .HasConversion(UlongToLongConverter)
                .IsRequired();

            entity.Property(e => e.Geom)
                .HasColumnName("geom")
                .HasColumnType("geometry(GeometryZM, 0)");

            entity.Property(e => e.TypeId)
                .HasColumnName("type_id");

            // Self-reference for type
            entity.HasOne(e => e.Type)
                .WithMany()
                .HasForeignKey(e => e.TypeId)
                .OnDelete(DeleteBehavior.SetNull);

            // Indexes
            entity.HasIndex(e => e.ContentHash)
                .IsUnique();

            entity.HasIndex(e => new { e.HilbertHigh, e.HilbertLow });

            entity.HasIndex(e => e.Geom)
                .HasMethod("GIST");

            entity.HasIndex(e => e.TypeId);
        });

        // --- Relation Configuration ---
        modelBuilder.Entity<Relation>(entity =>
        {
            entity.ToTable("relation");

            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id)
                .HasColumnName("id")
                .ValueGeneratedOnAdd();

            entity.Property(e => e.CompositionId)
                .HasColumnName("composition_id")
                .IsRequired();

            entity.Property(e => e.ChildConstantId)
                .HasColumnName("child_constant_id");

            entity.Property(e => e.ChildCompositionId)
                .HasColumnName("child_composition_id");

            entity.Property(e => e.Position)
                .HasColumnName("position")
                .IsRequired();

            entity.Property(e => e.Multiplicity)
                .HasColumnName("multiplicity")
                .IsRequired()
                .HasDefaultValue(1);

            // FK: Parent composition
            entity.HasOne(r => r.Composition)
                .WithMany(c => c.OutgoingRelations)
                .HasForeignKey(r => r.CompositionId)
                .OnDelete(DeleteBehavior.Cascade);

            // FK: Child constant
            entity.HasOne(r => r.ChildConstant)
                .WithMany(c => c.IncomingRelations)
                .HasForeignKey(r => r.ChildConstantId)
                .OnDelete(DeleteBehavior.Cascade);

            // FK: Child composition
            entity.HasOne(r => r.ChildComposition)
                .WithMany(c => c.IncomingRelations)
                .HasForeignKey(r => r.ChildCompositionId)
                .OnDelete(DeleteBehavior.Cascade);

            // Indexes
            entity.HasIndex(e => e.CompositionId);

            entity.HasIndex(e => e.ChildConstantId);

            entity.HasIndex(e => e.ChildCompositionId);

            // Unique: one position per parent
            entity.HasIndex(e => new { e.CompositionId, e.Position })
                .IsUnique();

            // Check constraint: exactly one child type
            // Note: EF Core doesn't support check constraints directly, add in migration
        });
    }
}
