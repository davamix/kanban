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

    // The mirror path isn't exercised through this fake in integration tests (a FakeCalendarMirror
    // stands in for the whole flow); return a token so the contract is satisfied if ever called.
    public Task<string?> MintSubjectTokenAsync(string userId, CancellationToken ct = default)
        => Task.FromResult<string?>($"subject-token-for-{userId}");
}
