using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using KanbanApi.Models;
using KanbanApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KanbanApi.IntegrationTests;

/// <summary>
/// The real <see cref="CalendarMirror"/> against a stubbed Calendar API: it calls Calendar with the
/// on-behalf-of bearer (so Calendar sets the *user* as owner), creates the project, adds non-owner
/// assignees, and never throws — every failure degrades to Failed/Skipped. See ADR 0009.
/// </summary>
public sealed class CalendarMirrorServiceTests
{
    private sealed record Call(string Method, string Path, string? Authorization, string? Body);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Calls.Add(new Call(request.Method.Method, request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString(), body));
            return respond(request);
        }
    }

    // Stands in for the token-exchange leg: returns a bearer for the requested user (or null).
    private sealed class FakeExchange(string? token) : ILogtoTokenExchange
    {
        public string? RequestedUserId { get; private set; }
        public string? RequestedResource { get; private set; }
        public Task<string?> GetOnBehalfOfTokenAsync(string userId, string resource, CancellationToken ct = default)
        {
            RequestedUserId = userId;
            RequestedResource = resource;
            return Task.FromResult(token);
        }
    }

    private static IConfiguration Config(string? baseUrl = "https://calendar.test", string? resource = "https://calendar.api") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Calendar:Api:BaseUrl"] = baseUrl,
            ["Calendar:Api:Resource"] = resource,
        }).Build();

    private static Project NewProject(string owner = "owner-1", string name = "Proj") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Description = "d",
        StartDate = new DateOnly(2026, 7, 1),
        EndDate = new DateOnly(2026, 8, 1),
        OwnerId = owner,
    };

    [Fact]
    public async Task Mirror_CreatesProject_AddsNonOwnerAssignees_ReturnsMirrored()
    {
        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/assignees")
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(new { id = "cal-xyz" }) });
        var exchange = new FakeExchange("obo-token");
        var sut = new CalendarMirror(new HttpClient(handler), Config(), exchange, NullLogger<CalendarMirror>.Instance);
        var project = NewProject(owner: "owner-1");

        var (status, calId) = await sut.MirrorProjectAsync(project, ["owner-1", "user-b"]);

        status.Should().Be(ProjectMirrorStatus.Mirrored);
        calId.Should().Be("cal-xyz");
        exchange.RequestedUserId.Should().Be("owner-1");             // owner's sub, from the entity
        exchange.RequestedResource.Should().Be("https://calendar.api"); // Calendar audience from config

        var create = handler.Calls.Should().ContainSingle(c => c.Path.EndsWith("/api/projects")).Subject;
        create.Method.Should().Be("POST");
        create.Authorization.Should().Be("Bearer obo-token");        // the exchanged bearer, not m2m
        create.Body.Should().Contain("\"name\":\"Proj\"").And.Contain("\"startDate\":\"2026-07-01\"");

        // Only the non-owner assignee is POSTed (Calendar auto-adds the owner).
        var assigneeCalls = handler.Calls.Where(c => c.Path.EndsWith("/assignees")).ToList();
        assigneeCalls.Should().ContainSingle();
        assigneeCalls[0].Body.Should().Contain("user-b").And.NotContain("owner-1");
    }

    [Fact]
    public async Task Mirror_WhenBaseUrlUnset_ReturnsSkipped_WithoutCalling()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var exchange = new FakeExchange("obo-token");
        var sut = new CalendarMirror(new HttpClient(handler), Config(baseUrl: null), exchange, NullLogger<CalendarMirror>.Instance);

        var (status, calId) = await sut.MirrorProjectAsync(NewProject(), []);

        status.Should().Be(ProjectMirrorStatus.Skipped);
        calId.Should().BeNull();
        exchange.RequestedUserId.Should().BeNull();
        handler.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Mirror_WhenResourceUnset_ReturnsSkipped_WithoutCalling()
    {
        // Calendar's audience is config, never baked in — with it unset, mirroring is off.
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var exchange = new FakeExchange("obo-token");
        var sut = new CalendarMirror(new HttpClient(handler), Config(resource: null), exchange, NullLogger<CalendarMirror>.Instance);

        var (status, _) = await sut.MirrorProjectAsync(NewProject(), []);

        status.Should().Be(ProjectMirrorStatus.Skipped);
        exchange.RequestedUserId.Should().BeNull();
        handler.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Mirror_WhenExchangeUnavailable_ReturnsSkipped()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var sut = new CalendarMirror(new HttpClient(handler), Config(), new FakeExchange(null), NullLogger<CalendarMirror>.Instance);

        var (status, _) = await sut.MirrorProjectAsync(NewProject(), []);

        status.Should().Be(ProjectMirrorStatus.Skipped);
        handler.Calls.Should().BeEmpty();   // no token → never calls Calendar
    }

    [Fact]
    public async Task Mirror_WhenCreateErrors_ReturnsFailed()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var sut = new CalendarMirror(new HttpClient(handler), Config(), new FakeExchange("obo-token"), NullLogger<CalendarMirror>.Instance);

        var (status, calId) = await sut.MirrorProjectAsync(NewProject(), []);

        status.Should().Be(ProjectMirrorStatus.Failed);
        calId.Should().BeNull();
    }

    [Fact]
    public async Task Mirror_WhenCalendarUnreachable_ReturnsFailed_NeverThrows()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("Connection refused"));
        var sut = new CalendarMirror(new HttpClient(handler), Config(), new FakeExchange("obo-token"), NullLogger<CalendarMirror>.Instance);

        var (status, _) = await sut.MirrorProjectAsync(NewProject(), []);

        status.Should().Be(ProjectMirrorStatus.Failed);
    }

    [Fact]
    public async Task Mirror_UsesConfiguredResource_WhenSet()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(new { id = "cal-1" }) });
        var exchange = new FakeExchange("obo-token");
        var sut = new CalendarMirror(new HttpClient(handler), Config(resource: "https://calendar.custom"), exchange, NullLogger<CalendarMirror>.Instance);

        await sut.MirrorProjectAsync(NewProject(), []);

        exchange.RequestedResource.Should().Be("https://calendar.custom");
    }

    private static Project Mirrored(string owner = "owner-1", string name = "Proj", string calId = "cal-1")
    {
        var p = NewProject(owner, name);
        p.CalendarProjectId = calId;
        return p;
    }

    [Fact]
    public async Task Update_PutsScalars_RemovesThenAddsAssignees_ReturnsMirrored()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var exchange = new FakeExchange("obo-token");
        var sut = new CalendarMirror(new HttpClient(handler), Config(), exchange, NullLogger<CalendarMirror>.Instance);
        var project = Mirrored(owner: "owner-1", name: "Renamed");

        var status = await sut.UpdateProjectAsync(project, addedAssigneeIds: ["user-b"], removedAssigneeIds: ["user-a"]);

        status.Should().Be(ProjectMirrorStatus.Mirrored);
        exchange.RequestedUserId.Should().Be("owner-1");   // OBO the owner, from the entity

        var put = handler.Calls.Should().ContainSingle(c => c.Method == "PUT").Subject;
        put.Path.Should().Be("/api/projects/cal-1");
        put.Authorization.Should().Be("Bearer obo-token");
        put.Body.Should().Contain("\"name\":\"Renamed\"").And.Contain("\"startDate\":\"2026-07-01\"");

        handler.Calls.Should().ContainSingle(c => c.Method == "DELETE" && c.Path == "/api/projects/cal-1/assignees/user-a");
        var add = handler.Calls.Should().ContainSingle(c => c.Method == "POST" && c.Path == "/api/projects/cal-1/assignees").Subject;
        add.Body.Should().Contain("user-b");
    }

    [Fact]
    public async Task Update_WhenPutErrors_ReturnsFailed_WithoutTouchingAssignees()
    {
        var handler = new RecordingHandler(req =>
            new HttpResponseMessage(req.Method == HttpMethod.Put ? HttpStatusCode.InternalServerError : HttpStatusCode.OK));
        var sut = new CalendarMirror(new HttpClient(handler), Config(), new FakeExchange("obo-token"), NullLogger<CalendarMirror>.Instance);

        var status = await sut.UpdateProjectAsync(Mirrored(), addedAssigneeIds: ["user-b"], removedAssigneeIds: ["user-a"]);

        status.Should().Be(ProjectMirrorStatus.Failed);
        handler.Calls.Should().OnlyContain(c => c.Method == "PUT");   // bailed before assignee reconcile
    }

    [Fact]
    public async Task Update_WhenExchangeUnavailable_ReturnsFailed_SurfacingDrift()
    {
        // The project is already mirrored (has a counterpart), so a transient exchange/Logto outage
        // means this edit did NOT land — that's drift and must surface as Failed, never silently
        // downgrade Mirrored → Skipped ("no counterpart"). See ADR 0010 §4.
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var sut = new CalendarMirror(new HttpClient(handler), Config(), new FakeExchange(null), NullLogger<CalendarMirror>.Instance);

        var status = await sut.UpdateProjectAsync(Mirrored(), [], []);

        status.Should().Be(ProjectMirrorStatus.Failed);
        handler.Calls.Should().BeEmpty();   // no token → never touches Calendar
    }

    [Fact]
    public async Task Update_WhenCalendarUnreachable_ReturnsFailed_NeverThrows()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("Connection refused"));
        var sut = new CalendarMirror(new HttpClient(handler), Config(), new FakeExchange("obo-token"), NullLogger<CalendarMirror>.Instance);

        var status = await sut.UpdateProjectAsync(Mirrored(), [], []);

        status.Should().Be(ProjectMirrorStatus.Failed);
    }

    [Fact]
    public async Task Delete_SendsDelete_ReturnsTrue()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var exchange = new FakeExchange("obo-token");
        var sut = new CalendarMirror(new HttpClient(handler), Config(), exchange, NullLogger<CalendarMirror>.Instance);

        var ok = await sut.DeleteProjectAsync(Mirrored(owner: "owner-1"));

        ok.Should().BeTrue();
        exchange.RequestedUserId.Should().Be("owner-1");
        var del = handler.Calls.Should().ContainSingle().Subject;
        del.Method.Should().Be("DELETE");
        del.Path.Should().Be("/api/projects/cal-1");
        del.Authorization.Should().Be("Bearer obo-token");
    }

    [Fact]
    public async Task Delete_WhenAlreadyGone_ReturnsTrue()
    {
        // A 404 means the Calendar counterpart is already gone — treat it as a successful delete.
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var sut = new CalendarMirror(new HttpClient(handler), Config(), new FakeExchange("obo-token"), NullLogger<CalendarMirror>.Instance);

        (await sut.DeleteProjectAsync(Mirrored())).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_WhenServerErrors_ReturnsFalse()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var sut = new CalendarMirror(new HttpClient(handler), Config(), new FakeExchange("obo-token"), NullLogger<CalendarMirror>.Instance);

        (await sut.DeleteProjectAsync(Mirrored())).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_WhenUnreachable_ReturnsFalse_NeverThrows()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("Connection refused"));
        var sut = new CalendarMirror(new HttpClient(handler), Config(), new FakeExchange("obo-token"), NullLogger<CalendarMirror>.Instance);

        (await sut.DeleteProjectAsync(Mirrored())).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_WhenBaseUrlUnset_ReturnsFalse_WithoutCalling()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var sut = new CalendarMirror(new HttpClient(handler), Config(baseUrl: null), new FakeExchange("obo-token"), NullLogger<CalendarMirror>.Instance);

        (await sut.DeleteProjectAsync(Mirrored())).Should().BeFalse();
        handler.Calls.Should().BeEmpty();
    }
}
