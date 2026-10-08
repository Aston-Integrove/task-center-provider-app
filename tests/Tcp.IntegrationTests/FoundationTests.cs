using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tcp.Infrastructure.Persistence;
using Tcp.TestSupport;

namespace Tcp.IntegrationTests;

[Collection(SqlCollection.Name)]
public class FoundationTests(SqlServerFixture sql)
{
    private static AuthenticationHeaderValue Basic(string user, string pw) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pw}")));

    [Fact] // T001-02
    public async Task Healthz_returns_healthy_and_ready_when_db_up()
    {
        await using var factory = new TcpFactory(sql.ConnectionString("tcp_health"));
        var client = factory.CreateClient();

        var live = await client.GetAsync("/healthz");
        live.StatusCode.Should().Be(HttpStatusCode.OK);
        (await live.Content.ReadAsStringAsync()).Should().Contain("\"Healthy\"");

        var ready = await client.GetAsync("/healthz/ready");
        ready.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact] // T001-02
    public async Task Ready_returns_503_when_db_unreachable_but_liveness_stays_200()
    {
        await using var factory = new TcpFactory(TcpFactory.UnreachableDb, migrate: false);
        var client = factory.CreateClient();

        (await client.GetAsync("/healthz")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/healthz/ready")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact] // T001-05
    public async Task Unknown_route_and_unlisted_endpoints_require_authentication()
    {
        await using var factory = new TcpFactory(sql.ConnectionString("tcp_auth"));
        var client = factory.CreateClient();

        (await client.GetAsync("/no/such/route")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/task-provider/v2/tasks")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var bogus = new HttpRequestMessage(HttpMethod.Get, "/task-provider/v2/tasks");
        bogus.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.jwt");
        (await client.SendAsync(bogus)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await client.GetAsync("/healthz")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact] // T001-03
    public async Task Correlation_id_is_echoed_and_generated()
    {
        await using var factory = new TcpFactory(sql.ConnectionString("tcp_corr"));
        var client = factory.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        req.Headers.Add("X-Correlation-Id", "test-corr-1");
        var res = await client.SendAsync(req);
        res.Headers.GetValues("X-Correlation-Id").Should().ContainSingle("test-corr-1");

        var res2 = await client.GetAsync("/healthz");
        res2.Headers.GetValues("X-Correlation-Id").Single().Should().HaveLength(32);
    }

    [Fact] // T001-06
    public async Task Migrations_are_applied_on_startup()
    {
        await using var factory = new TcpFactory(sql.ConnectionString("tcp_migrate"));
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TcpDbContext>();
        (await db.Database.GetAppliedMigrationsAsync()).Should().NotBeEmpty();
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Fact] // T001-13, T002-12 (admin auth)
    public async Task Admin_request_log_requires_basic_auth_and_returns_redacted_entries_newest_first()
    {
        await using var factory = new TcpFactory(sql.ConnectionString("tcp_reqlog"));
        var client = factory.CreateClient();

        (await client.GetAsync("/admin/api/requests")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var wrong = new HttpRequestMessage(HttpMethod.Get, "/admin/api/requests");
        wrong.Headers.Authorization = Basic("admin", "nope");
        (await client.SendAsync(wrong)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Generate traffic: SPI call with a (fake) bearer, plus a token request carrying secrets.
        var spi = new HttpRequestMessage(HttpMethod.Get, "/task-provider/v2/tasks?languages=en-US");
        spi.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "fake.jwt.value");
        await client.SendAsync(spi);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = "tc-tech", ["client_secret"] = "TOPSECRET",
        });
        await client.PostAsync("/oauth/token", form);

        var req = new HttpRequestMessage(HttpMethod.Get, "/admin/api/requests");
        req.Headers.Authorization = Basic(TcpFactory.AdminUser, TcpFactory.AdminPassword);
        var res = await client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await res.Content.ReadAsStringAsync();

        raw.Should().NotContain("TOPSECRET").And.NotContain("fake.jwt.value");
        using var doc = JsonDocument.Parse(raw);
        var paths = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("path").GetString()).ToList();
        paths.Should().Equal("/oauth/token", "/task-provider/v2/tasks");

        var filtered = new HttpRequestMessage(HttpMethod.Get, "/admin/api/requests?prefix=/task-provider&status=401");
        filtered.Headers.Authorization = Basic(TcpFactory.AdminUser, TcpFactory.AdminPassword);
        var list = await (await client.SendAsync(filtered)).Content.ReadFromJsonAsync<JsonElement>();
        list.GetArrayLength().Should().Be(1);
        list[0].GetProperty("requestHeaders").GetProperty("Authorization").GetString().Should().Be("***");
    }
}
