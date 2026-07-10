using KanbanApi.Data;
using KanbanApi.IntegrationTests.Fakes;
using KanbanApi.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace KanbanApi.IntegrationTests.Fixtures;

/// <summary>
/// Hosts the API against a real (Testcontainers) Postgres, with auth swapped for the header-driven
/// <see cref="TestAuthHandler"/> and CSRF/Logto faked. Config is injected via <c>UseSetting</c>
/// (never env vars — those clobber across parallel collections). See docs/testing.md.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .Build();

    async Task IAsyncLifetime.InitializeAsync() => await _db.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _db.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Kanban", _db.GetConnectionString());
        builder.UseSetting("Logto:Issuer", "https://test.logto.local/oidc/");
        builder.UseSetting("Logto:Audience", "https://kanban.api");
        // The OIDC remote handler is initialised per request (to detect its callback) and validates
        // its options, so a non-null ClientId is required even though we authenticate via Test.
        builder.UseSetting("Logto:Web:ClientId", "test-client-id");
        builder.UseSetting("Logto:Web:ClientSecret", "test-client-secret");
        builder.UseEnvironment("Testing");   // not Development → per-user sample seeding is skipped

        builder.ConfigureTestServices(services =>
        {
            // Swap real auth for the header-driven test scheme + accept it in the default policy.
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            services.AddAuthorization(options =>
                options.DefaultPolicy = new AuthorizationPolicyBuilder(TestAuthHandler.SchemeName)
                    .RequireAuthenticatedUser().Build());

            services.AddSingleton<IAntiforgery, NoopAntiforgery>();
            services.AddSingleton<ILogtoManagementClient, FakeLogtoManagementClient>();
            // Deterministic Calendar mirror (outcome driven by project name) so create-path tests
            // don't need Logto/Calendar. Replaces the real HttpClient-backed CalendarMirror.
            services.AddSingleton<ICalendarMirror, FakeCalendarMirror>();
        });
    }

    /// <summary>A client that authenticates as <paramref name="userId"/> (the Logto sub).</summary>
    public HttpClient CreateClientAs(string userId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId);
        return client;
    }

    /// <summary>Runs an action against a fresh DbContext scope (for test setup / assertions).</summary>
    public async Task WithDbAsync(Func<KanbanDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KanbanDbContext>();
        await action(db);
    }
}

[CollectionDefinition("Api")]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>;
