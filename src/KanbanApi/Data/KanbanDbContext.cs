using KanbanApi.Models;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace KanbanApi.Data;

/// <summary>
/// EF Core context backing Kanban. At the infrastructure stage it holds the local user
/// projection (FK target for the owner/assignee model added in the implementation phase) and
/// the Data Protection key ring, so auth/antiforgery cookies survive container recreates.
/// The kanban domain entities (boards, columns, projects, tasks) are added later.
/// </summary>
public sealed class KanbanDbContext(DbContextOptions<KanbanDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<AppUser> Users => Set<AppUser>();

    /// <summary>Data Protection key ring (persisted so cookies survive restarts).</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>(e =>
        {
            e.ToTable("users");
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).HasMaxLength(64);
            e.Property(u => u.Email).HasMaxLength(320);
            e.Property(u => u.DisplayName).HasMaxLength(200);
        });
    }

    /// <summary>Ensures a local user row exists for the given Logto sub (FK target for owner/assignee).</summary>
    public async Task EnsureUserAsync(string userId)
    {
        if (!await Users.AnyAsync(u => u.Id == userId))
            Users.Add(new AppUser { Id = userId });
    }
}
