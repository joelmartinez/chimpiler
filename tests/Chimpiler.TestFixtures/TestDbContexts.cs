using Microsoft.EntityFrameworkCore;
using Chimpiler.EfMigrate;

namespace Chimpiler.TestFixtures;

/// <summary>
/// Simple test database with single table
/// </summary>
public class TheDatabaseContext : DbContext
{
    public DbSet<User> Users { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            // Note: This connection string is a placeholder and is not used during DACPAC generation
            // DACPAC generation only requires the EF Core model, not a live database connection
            optionsBuilder.UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=TestDb;Trusted_Connection=True;");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Email).HasMaxLength(255);
        });
    }
}

public class User
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
}

/// <summary>
/// Test database with multiple tables and relationships
/// </summary>
public class OrdersDbContext : DbContext
{
    public DbSet<Order> Orders { get; set; } = null!;
    public DbSet<OrderItem> OrderItems { get; set; } = null!;
    public DbSet<Product> Products { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            // Note: This connection string is a placeholder and is not used during DACPAC generation
            optionsBuilder.UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=OrdersDb;Trusted_Connection=True;");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.OrderNumber).IsRequired().HasMaxLength(50);
            entity.Property(e => e.OrderDate).IsRequired();
            entity.Property(e => e.TotalAmount).HasColumnType("decimal(18,2)");
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Price).HasColumnType("decimal(18,2)");
        });

        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Quantity).IsRequired();
            entity.Property(e => e.UnitPrice).HasColumnType("decimal(18,2)");

            entity.HasOne<Order>()
                .WithMany()
                .HasForeignKey(e => e.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne<Product>()
                .WithMany()
                .HasForeignKey(e => e.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}

public class Order
{
    public int Id { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public DateTime OrderDate { get; set; }
    public decimal TotalAmount { get; set; }
}

public class OrderItem
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
}

/// <summary>
/// Test database with custom schema
/// </summary>
public class ReportingContext : DbContext
{
    public DbSet<Report> Reports { get; set; } = null!;
    public DbSet<ReportData> ReportData { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            // Note: This connection string is a placeholder and is not used during DACPAC generation
            optionsBuilder.UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=ReportingDb;Trusted_Connection=True;");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("reporting");

        modelBuilder.Entity<Report>(entity =>
        {
            entity.ToTable("Reports", "reporting");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.CreatedDate).IsRequired();
        });

        modelBuilder.Entity<ReportData>(entity =>
        {
            entity.ToTable("ReportData", "reporting");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Data).HasColumnType("nvarchar(max)");

            entity.HasOne<Report>()
                .WithMany()
                .HasForeignKey(e => e.ReportId);
        });
    }
}

public class Report
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
}

public class ReportData
{
    public int Id { get; set; }
    public int ReportId { get; set; }
    public string? Data { get; set; }
}

/// <summary>
/// Test database with composite key
/// </summary>
public class InventoryContext : DbContext
{
    public DbSet<InventoryItem> InventoryItems { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            // Note: This connection string is a placeholder and is not used during DACPAC generation
            optionsBuilder.UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=InventoryDb;Trusted_Connection=True;");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InventoryItem>(entity =>
        {
            entity.HasKey(e => new { e.WarehouseId, e.ProductId });
            entity.Property(e => e.Quantity).IsRequired();
            entity.Property(e => e.LastUpdated).IsRequired();
        });
    }
}

public class InventoryItem
{
    public int WarehouseId { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public DateTime LastUpdated { get; set; }
}

/// <summary>
/// Test database with views - includes simple view, view with column rename, and indexed view
/// </summary>
public class ParticipantsDbContext : DbContext
{
    public DbSet<StudyEnrollment> StudyEnrollments { get; set; } = null!;
    public DbSet<ParticipantProfile> ParticipantProfiles { get; set; } = null!;
    
    // Views
    public DbSet<DeidentifiedStudyEnrollmentView> DeidentifiedStudyEnrollmentView { get; set; } = null!;
    public DbSet<DeidentifiedParticipantProfileView> DeidentifiedParticipantProfileView { get; set; } = null!;
    public DbSet<SimpleStudyView> SimpleStudyView { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=ParticipantsDb;Trusted_Connection=True;");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Tables
        modelBuilder.Entity<StudyEnrollment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.StudyId).IsRequired();
            entity.Property(e => e.ParticipantId).IsRequired();
            entity.Property(e => e.ParticipantName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.EnrolledAtUtc).IsRequired();
        });

        modelBuilder.Entity<ParticipantProfile>(entity =>
        {
            entity.HasKey(e => e.ParticipantId);
            entity.Property(e => e.BirthYear);
            entity.Property(e => e.AssignedSex).HasMaxLength(10);
            entity.Property(e => e.GenderIdentity).HasMaxLength(50);
            entity.Property(e => e.TrackedIllnesses).HasMaxLength(500);
        });

        // Simple view over single table (no column renames)
        modelBuilder.Entity<SimpleStudyView>(entity =>
        {
            entity.ToView("SimpleStudyView")
                  .HasViewDefinition<SimpleStudyView, ParticipantsDbContext>(ctx =>
                      from se in ctx.StudyEnrollments
                      select new SimpleStudyView
                      {
                          Id = se.Id,
                          StudyId = se.StudyId,
                          EnrolledAtUtc = se.EnrolledAtUtc
                      });
            
            entity.HasKey(e => e.Id);
        });

        // View with column rename - ParticipantName becomes StudyScopedParticipantId
        modelBuilder.Entity<DeidentifiedStudyEnrollmentView>(entity =>
        {
            entity.ToView("DeidentifiedStudyEnrollmentView")
                  .HasViewDefinition<DeidentifiedStudyEnrollmentView, ParticipantsDbContext>(ctx =>
                      from se in ctx.StudyEnrollments
                      select new DeidentifiedStudyEnrollmentView
                      {
                          Id = se.Id,
                          StudyId = se.StudyId,
                          StudyScopedParticipantId = se.ParticipantName,
                          EnrolledAtUtc = se.EnrolledAtUtc,
                          WithdrawnAtUtc = se.WithdrawnAtUtc,
                          CompletedAtUtc = se.CompletedAtUtc
                      })
                  .WithSchemaBinding()
                  .HasClusteredIndex(v => v.Id);
            
            entity.HasKey(e => e.Id);
        });

        // View with JOIN
        modelBuilder.Entity<DeidentifiedParticipantProfileView>(entity =>
        {
            entity.ToView("DeidentifiedParticipantProfileView")
                  .HasViewDefinition<DeidentifiedParticipantProfileView, ParticipantsDbContext>(ctx =>
                      from se in ctx.StudyEnrollments
                      join pp in ctx.ParticipantProfiles on se.ParticipantId equals pp.ParticipantId
                      select new DeidentifiedParticipantProfileView
                      {
                          StudyId = se.StudyId,
                          StudyScopedParticipantId = se.ParticipantName,
                          BirthYear = pp.BirthYear,
                          AssignedSex = pp.AssignedSex,
                          GenderIdentity = pp.GenderIdentity,
                          TrackedIllnesses = pp.TrackedIllnesses
                      });
            
            // Composite key for this view
            entity.HasKey(e => new { e.StudyId, e.StudyScopedParticipantId });
        });
    }
}

// Table entities
public class StudyEnrollment
{
    public int Id { get; set; }
    public int StudyId { get; set; }
    public int ParticipantId { get; set; }
    public string ParticipantName { get; set; } = string.Empty;
    public DateTime EnrolledAtUtc { get; set; }
    public DateTime? WithdrawnAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

public class ParticipantProfile
{
    public int ParticipantId { get; set; }
    public int? BirthYear { get; set; }
    public string? AssignedSex { get; set; }
    public string? GenderIdentity { get; set; }
    public string? TrackedIllnesses { get; set; }
}

// View entities
public class SimpleStudyView
{
    public int Id { get; set; }
    public int StudyId { get; set; }
    public DateTime EnrolledAtUtc { get; set; }
}

public class DeidentifiedStudyEnrollmentView
{
    public int Id { get; set; }
    public int StudyId { get; set; }
    public string StudyScopedParticipantId { get; set; } = string.Empty;
    public DateTime EnrolledAtUtc { get; set; }
    public DateTime? WithdrawnAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

public class DeidentifiedParticipantProfileView
{
    public int StudyId { get; set; }
    public string StudyScopedParticipantId { get; set; } = string.Empty;
    public int? BirthYear { get; set; }
    public string? AssignedSex { get; set; }
    public string? GenderIdentity { get; set; }
    public string? TrackedIllnesses { get; set; }
}

