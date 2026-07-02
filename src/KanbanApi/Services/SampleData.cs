using KanbanApi.Data;
using KanbanApi.Models;
using Microsoft.EntityFrameworkCore;

namespace KanbanApi.Services;

/// <summary>
/// Development-only illustrative data so a freshly signed-in user lands on a populated
/// project-selection screen instead of an empty state. Seeded per user on first login (see
/// <c>AuthenticationExtensions.UpsertLocalUserAsync</c>) because the owner/assignee query filter
/// is per-user — projects owned by someone else would be invisible. Idempotent and never runs
/// outside Development.
/// </summary>
public static class SampleData
{
    // Synthetic teammates, so cards show assignee avatars and the owner-vs-shared distinction.
    private static readonly (string Id, string Name)[] Teammates =
    [
        ("seed-alex", "Alex Rivera"),
        ("seed-sam", "Sam Chen"),
        ("seed-jordan", "Jordan Lee"),
    ];

    public static async Task SeedForUserAsync(KanbanDbContext db, string userId, string? displayName, CancellationToken ct = default)
    {
        // Idempotent: only seed the first time this user appears (no projects they own yet).
        // reason: this runs from OIDC OnTokenValidated, before HttpContext.User (hence ICurrentUser)
        // is set, so the per-user query filter would fail closed and hide the user's own rows. The
        // guard checks one specific user's ownership, not the ambient current user, so bypass it.
        if (await db.Projects.IgnoreQueryFilters().AnyAsync(p => p.OwnerId == userId, ct))
            return;

        foreach (var (id, name) in Teammates)
        {
            if (!await db.Users.AnyAsync(u => u.Id == id, ct))
                db.Users.Add(new AppUser { Id = id, DisplayName = name });
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var now = DateTimeOffset.UtcNow;

        // Three projects the user owns (owner is auto-added as an assignee) …
        await AddAsync(db, userId, displayName, "Website Redesign",
            "Complete overhaul of the enterprise portal with a focus on performance and engagement.",
            today.AddDays(-10), today.AddDays(35), now,
            assignees: [userId, "seed-alex", "seed-sam"], ct: ct);

        await AddAsync(db, userId, displayName, "Mobile App",
            "iOS and Android native applications for field operations and inventory synchronization.",
            today.AddDays(-3), today.AddDays(60), now.AddSeconds(-1),
            assignees: [userId, "seed-jordan"], ct: ct);

        await AddAsync(db, userId, displayName, "Data Integration",
            "Consolidating legacy database clusters into the primary cloud-native architecture.",
            today, today.AddDays(90), now.AddSeconds(-2),
            assignees: [userId], ct: ct);

        // … and one owned by a teammate that the user is assigned to (shows the "Shared" role).
        await AddAsync(db, "seed-alex", "Alex Rivera", "Marketing Campaign",
            "Global brand awareness initiative across digital and physical touchpoints for launch.",
            today.AddDays(-20), today.AddDays(10), now.AddSeconds(-3),
            assignees: ["seed-alex", userId, "seed-sam"], ct: ct);

        await db.SaveChangesAsync(ct);
    }

    private static async Task AddAsync(
        KanbanDbContext db, string ownerId, string? ownerName, string name, string description,
        DateOnly start, DateOnly end, DateTimeOffset createdAt, string[] assignees, CancellationToken ct)
    {
        // Make sure the owner exists as a user row (FK target); refresh the display name if given.
        // FindAsync checks the change tracker before the DB, so Local-tracked teammates are reused.
        var owner = await db.Users.FindAsync([ownerId], ct);
        if (owner is null)
            db.Users.Add(new AppUser { Id = ownerId, DisplayName = ownerName });
        else if (ownerName is not null && owner.DisplayName is null)
            owner.DisplayName = ownerName;

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            StartDate = start,
            EndDate = end,
            OwnerId = ownerId,
            CreatedAt = createdAt,
            CreatedBy = ownerId,
        };
        foreach (var uid in assignees.Distinct())
            project.Assignees.Add(new ProjectAssignee { ProjectId = project.Id, UserId = uid });

        db.Projects.Add(project);
    }
}
