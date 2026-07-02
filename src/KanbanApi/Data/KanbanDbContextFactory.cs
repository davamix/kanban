using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KanbanApi.Data;

/// <summary>
/// Design-time factory so <c>dotnet ef</c> can build the model (and scaffold migrations) without
/// running the app — avoiding the runtime connection-string requirement. Not used at runtime.
/// </summary>
public sealed class KanbanDbContextFactory : IDesignTimeDbContextFactory<KanbanDbContext>
{
    public KanbanDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<KanbanDbContext>()
            .UseNpgsql("Host=localhost;Database=kanban;Username=kanban_app;Password=design-time")
            .Options;
        return new KanbanDbContext(options);
    }
}
