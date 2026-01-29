using Microsoft.EntityFrameworkCore;

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
