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
    public DbSet<BoardColumn> BoardColumns => Set<BoardColumn>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
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
            e.Property(p => p.Budget).HasPrecision(18, 2);
            e.Property(p => p.OwnerId).IsRequired().HasMaxLength(64);
            e.Property(p => p.CreatedBy).HasMaxLength(64);
            e.Property(p => p.MirrorStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.CalendarProjectId).HasMaxLength(200);
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

        modelBuilder.Entity<BoardColumn>(e =>
        {
            e.ToTable("board_columns");
            e.HasKey(c => c.Id);
            e.Property(c => c.Name).IsRequired().HasMaxLength(100);
            e.HasIndex(c => new { c.ProjectId, c.Position });
            e.HasOne(c => c.Project).WithMany()
                .HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskItem>(e =>
        {
            e.ToTable("tasks");
            e.HasKey(t => t.Id);
            e.Property(t => t.Title).IsRequired().HasMaxLength(200);
            e.Property(t => t.Description).HasMaxLength(2000);
            e.Property(t => t.Priority).HasConversion<string>().HasMaxLength(20);
            e.Property(t => t.AssigneeId).HasMaxLength(64);
            e.Property(t => t.Labels).HasColumnType("text[]");
            e.Property(t => t.CreatedBy).HasMaxLength(64);
            e.HasIndex(t => new { t.ColumnId, t.Position });
            e.HasIndex(t => t.ProjectId);
            e.HasIndex(t => t.AssigneeId);
            // Deleting a project cascades to both its columns and its tasks. The task→column FK uses
            // NO ACTION (not RESTRICT): both still block a *direct* delete of a non-empty column (the
            // owner must empty it first — the store also pre-checks and returns 409), but RESTRICT is
            // checked immediately in Postgres, which would abort a project-level cascade if a column
            // row were deleted before its sibling task rows. NO ACTION defers to end-of-statement, by
            // which point the tasks are gone via the project cascade, so deleting a project with tasks
            // succeeds. (This diamond is why the two paths must not both fire an immediate check.)
            e.HasOne(t => t.Project).WithMany()
                .HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(t => t.Column).WithMany(c => c.Tasks)
                .HasForeignKey(t => t.ColumnId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(t => t.Assignee).WithMany()
                .HasForeignKey(t => t.AssigneeId).OnDelete(DeleteBehavior.SetNull);
        });

        // Per-user read isolation: a user sees only projects they own or are assigned to.
        // An unset current user (no HTTP context) matches no row — fail closed (ASVS V8.4.1).
        modelBuilder.Entity<Project>().HasQueryFilter(p =>
            p.OwnerId == _currentUserId || p.Assignees.Any(a => a.UserId == _currentUserId));

        // Columns and tasks inherit the parent project's visibility (owner-or-assignee), keyed off
        // the same current user so they also fail closed when no user is set. Because a filtered
        // entity may only reference other filtered entities, these are defined explicitly rather than
        // navigating into Project's filter.
        modelBuilder.Entity<BoardColumn>().HasQueryFilter(c =>
            c.Project.OwnerId == _currentUserId || c.Project.Assignees.Any(a => a.UserId == _currentUserId));
        modelBuilder.Entity<TaskItem>().HasQueryFilter(t =>
            t.Project.OwnerId == _currentUserId || t.Project.Assignees.Any(a => a.UserId == _currentUserId));
    }

    /// <summary>Ensures a local user row exists for the given Logto sub (FK target for owner/assignee).</summary>
    public async Task EnsureUserAsync(string userId)
    {
        if (!await Users.AnyAsync(u => u.Id == userId))
            Users.Add(new AppUser { Id = userId });
    }
}
