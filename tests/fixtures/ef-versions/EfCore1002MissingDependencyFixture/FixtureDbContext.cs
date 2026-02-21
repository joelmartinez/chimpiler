using Microsoft.EntityFrameworkCore;

namespace EfCore1002MissingDependencyFixture;

public class FixtureDbContext : DbContext
{
    public DbSet<Widget> Widgets => Set<Widget>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlServer(
            "Server=(localdb)\\mssqllocaldb;Database=EfCore1002MissingDependencyFixture;Trusted_Connection=True;");
    }
}

public class Widget
{
    public int Id { get; set; }

    public MissingDependencyLib.ExternalType? External { get; set; }
}
