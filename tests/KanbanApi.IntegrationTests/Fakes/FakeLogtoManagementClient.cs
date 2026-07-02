using KanbanApi.Services;

namespace KanbanApi.IntegrationTests.Fakes;

/// <summary>Deterministic stand-in for the Logto directory — no network in tests.</summary>
public sealed class FakeLogtoManagementClient : ILogtoManagementClient
{
    public List<DirectoryUser> Users { get; } =
    [
        new("user-a", "User A", "a@test.local"),
        new("user-b", "User B", "b@test.local"),
        new("user-c", "User C", "c@test.local"),
    ];

    public Task<IReadOnlyList<DirectoryUser>> GetUsersAsync(string? search, CancellationToken ct = default)
    {
        IEnumerable<DirectoryUser> q = Users;
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(u => (u.Name ?? "").Contains(search, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<IReadOnlyList<DirectoryUser>>(q.ToList());
    }
}
