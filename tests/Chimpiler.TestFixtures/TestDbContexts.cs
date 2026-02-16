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
public class LibraryContext : DbContext
{
    public DbSet<Book> Books { get; set; } = null!;
    public DbSet<Author> Authors { get; set; } = null!;
    
    // Views
    public DbSet<BookSummaryView> BookSummaryView { get; set; } = null!;
    public DbSet<BookAuthorView> BookAuthorView { get; set; } = null!;
    public DbSet<SimpleBookView> SimpleBookView { get; set; } = null!;
    public DbSet<BookCountView> BookCountView { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=LibraryDb;Trusted_Connection=True;");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Tables
        modelBuilder.Entity<Book>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ISBN).IsRequired().HasMaxLength(20);
            entity.Property(e => e.AuthorId).IsRequired();
            entity.Property(e => e.PublishedYear);
            entity.Property(e => e.Genre).HasMaxLength(50);
        });

        modelBuilder.Entity<Author>(entity =>
        {
            entity.HasKey(e => e.AuthorId);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.BirthYear);
            entity.Property(e => e.Country).HasMaxLength(50);
            entity.Property(e => e.Biography).HasMaxLength(1000);
        });

        // Simple view over single table (no column renames)
        modelBuilder.Entity<SimpleBookView>(entity =>
        {
            entity.ToView("SimpleBookView")
                  .HasViewDefinition<SimpleBookView, LibraryContext>(ctx =>
                      from b in ctx.Books
                      select new SimpleBookView
                      {
                          Id = b.Id,
                          Title = b.Title,
                          PublishedYear = b.PublishedYear
                      });
            
            entity.HasKey(e => e.Id);
        });

        // View with column rename - ISBN becomes BookCode
        modelBuilder.Entity<BookSummaryView>(entity =>
        {
            entity.ToView("BookSummaryView")
                  .HasViewDefinition<BookSummaryView, LibraryContext>(ctx =>
                      from b in ctx.Books
                      select new BookSummaryView
                      {
                          Id = b.Id,
                          Title = b.Title,
                          BookCode = b.ISBN,
                          PublishedYear = b.PublishedYear,
                          Genre = b.Genre
                      })
                  .WithSchemaBinding()
                  .HasClusteredIndex(v => v.Id);
            
            entity.HasKey(e => e.Id);
        });

        // View with JOIN
        modelBuilder.Entity<BookAuthorView>(entity =>
        {
            entity.ToView("BookAuthorView")
                  .HasViewDefinition<BookAuthorView, LibraryContext>(ctx =>
                      from b in ctx.Books
                      join a in ctx.Authors on b.AuthorId equals a.AuthorId
                      select new BookAuthorView
                      {
                          BookId = b.Id,
                          Title = b.Title,
                          AuthorName = a.Name,
                          Country = a.Country,
                          PublishedYear = b.PublishedYear
                      });
            
            // Composite key for this view
            entity.HasKey(e => new { e.BookId, e.Title });
        });

        // View using raw SQL (escape hatch for complex SQL like CTEs)
        modelBuilder.Entity<BookCountView>(entity =>
        {
            entity.ToView("BookCountView")
                  .HasViewSql(@"SELECT 
                      a.AuthorId,
                      a.Name,
                      COUNT(b.Id) as BookCount
                  FROM Authors a
                  LEFT JOIN Books b ON a.AuthorId = b.AuthorId
                  GROUP BY a.AuthorId, a.Name");
            
            entity.HasKey(e => e.AuthorId);
        });
    }
}

// Table entities
public class Book
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ISBN { get; set; } = string.Empty;
    public int AuthorId { get; set; }
    public int? PublishedYear { get; set; }
    public string? Genre { get; set; }
}

public class Author
{
    public int AuthorId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? BirthYear { get; set; }
    public string? Country { get; set; }
    public string? Biography { get; set; }
}

// View entities
public class SimpleBookView
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public int? PublishedYear { get; set; }
}

public class BookSummaryView
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string BookCode { get; set; } = string.Empty;
    public int? PublishedYear { get; set; }
    public string? Genre { get; set; }
}

public class BookAuthorView
{
    public int BookId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;
    public string? Country { get; set; }
    public int? PublishedYear { get; set; }
}

public class BookCountView
{
    public int AuthorId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int BookCount { get; set; }
}

