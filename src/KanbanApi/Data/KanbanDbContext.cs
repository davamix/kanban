using KanbanApi.Models;
using KanbanApi.Services;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace KanbanApi.Data;

/// <summary>
/// EF Core context backing Kanban. Holds the domain (projects + their assignees), the local user
/// projection (FK target for owner/assignee), and the Data Protection key ring (so auth/antiforgery
/// cookies survive container recreates).
///
/// A global query filter on <see cref="Project"/>, keyed off <see cref="ICurrentUser"/>, enforces
/// per-user read isolation (owner or assignee) — see docs/security/asvs-l2/V08-authorization.md.
/// Board columns and tasks hang off a project and are added in a later screen.
/// </summary>
public sealed class KanbanDbContext : DbContext, IDataProtectionKeyContext
{
    // Captured once per context instance; EF parameterises it into the compiled query filter.
    private readonly string? _currentUserId;

    public KanbanDbContext(DbContextOptions<KanbanDbContext> options, ICurrentUser currentUser)
        : base(options)
    {
        _currentUserId = currentUser.Id;
    }

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectAssignee> ProjectAssignees => Set<ProjectAssignee>();
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

        modelBuilder.Entity<Project>(e =>
        {
            e.ToTable("projects");
            e.HasKey(p => p.Id);
            e.Property(p => p.Name).IsRequired().HasMaxLength(200);
            e.Property(p => p.Description).HasMaxLength(2000);
            e.Property(p => p.OwnerId).IsRequired().HasMaxLength(64);
            e.Property(p => p.CreatedBy).HasMaxLength(64);
            e.HasIndex(p => p.OwnerId);
            e.HasOne<AppUser>().WithMany()
                .HasForeignKey(p => p.OwnerId).OnDelete(DeleteBehavior.Restrict);
            // DateOnly maps to Postgres `date` natively via Npgsql.
        });

        modelBuilder.Entity<ProjectAssignee>(e =>
        {
            e.ToTable("project_assignees");
            e.HasKey(a => new { a.ProjectId, a.UserId });
            e.Property(a => a.UserId).HasMaxLength(64);
            e.HasOne(a => a.Project).WithMany(p => p.Assignees)
                .HasForeignKey(a => a.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.User).WithMany()
                .HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        // Per-user read isolation: a user sees only projects they own or are assigned to.
        // An unset current user (no HTTP context) matches no row — fail closed (ASVS V8.4.1).
        modelBuilder.Entity<Project>().HasQueryFilter(p =>
            p.OwnerId == _currentUserId || p.Assignees.Any(a => a.UserId == _currentUserId));
    }

    /// <summary>Ensures a local user row exists for the given Logto sub (FK target for owner/assignee).</summary>
    public async Task EnsureUserAsync(string userId)
    {
        if (!await Users.AnyAsync(u => u.Id == userId))
            Users.Add(new AppUser { Id = userId });
    }
}
