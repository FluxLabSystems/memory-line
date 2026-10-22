using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MemoryTimeline.Data;

/// <summary>
/// Lets the <c>dotnet ef</c> tooling build an <see cref="AppDbContext"/> at
/// design time (migration generation, model diffing) without the app host.
/// The connection string is a throwaway file name: <c>migrations add</c> never
/// opens it, and the runtime path is unchanged — the app still configures its
/// context from <c>AppDbContext.OnConfiguring</c> and raises its schema through
/// <c>SchemaUpgrader.EnsureSchemaAsync</c>.
/// </summary>
public sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=design-time-only.db")
            .Options;
        return new AppDbContext(options);
    }
}
