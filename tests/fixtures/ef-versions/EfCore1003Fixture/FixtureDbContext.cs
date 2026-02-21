using Microsoft.EntityFrameworkCore;

namespace EfCore1003Fixture;

public class FixtureDbContext : DbContext
{
    public DbSet<Widget> Widgets => Set<Widget>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlServer(
            "Server=(localdb)\\mssqllocaldb;Database=EfCore1003Fixture;Trusted_Connection=True;");
    }
}

public class Widget
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
