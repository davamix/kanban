using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KanbanApi.IntegrationTests.Fixtures;
using KanbanApi.Models;

namespace KanbanApi.IntegrationTests;

/// <summary>
/// GET /api/me resolves the caller's name/email from the local user mirror (kept in sync from
/// Logto), so a thin session — e.g. a username-only account with no name claim — still shows a real
/// name instead of the raw id. Falls back to the session claims when there's no mirror row yet.
/// </summary>
[Collection("Api")]
public sealed class MeEndpointTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private record MeDto(string Id, string? Email, string? DisplayName);

    [Fact]
    public async Task Me_Anonymous_Returns401()
    {
        var res = await factory.CreateClient().GetAsync("/api/me");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_PrefersMirroredNameOverThinClaims()
    {
        var sub = $"user-{Guid.NewGuid()}";
        // The test auth handler sets name == sub (a thin claim). The mirror carries the real name.
        await factory.WithDbAsync(async db =>
        {
            db.Users.Add(new AppUser { Id = sub, DisplayName = "Ada Lovelace", Email = "ada@test.local" });
            await db.SaveChangesAsync();
        });

        var me = (await factory.CreateClientAs(sub).GetFromJsonAsync<MeDto>("/api/me", Json))!;

        me.Id.Should().Be(sub);
        me.DisplayName.Should().Be("Ada Lovelace");
        me.Email.Should().Be("ada@test.local");
    }

    [Fact]
    public async Task Me_FallsBackToClaims_WhenNoMirrorRow()
    {
        var sub = $"user-{Guid.NewGuid()}"; // no mirror row seeded

        var me = (await factory.CreateClientAs(sub).GetFromJsonAsync<MeDto>("/api/me", Json))!;

        me.Id.Should().Be(sub);
        me.DisplayName.Should().Be(sub); // the handler sets the name claim to the sub
    }
}
