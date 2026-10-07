using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Tcp.TestSupport;

namespace Tcp.IntegrationTests;

[Collection(SqlCollection.Name)]
public partial class AdminUiHttpTests(SqlServerFixture sql)
{
    private SpiHost Host => SpiHost.Get(sql, "admin-ui");

    [GeneratedRegex(@"(?:src|href|action)\s*=\s*""(?<url>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex UrlAttributes();

    [Fact] // US-005-5: protected, CSP-locked, no external resources
    public async Task Admin_ui_is_served_behind_basic_auth_with_a_strict_csp_and_no_external_resources()
    {
        var anonymous = await Host.Factory.CreateClient().GetAsync("/admin");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        anonymous.Headers.WwwAuthenticate.ToString().Should().Contain("Basic");
        foreach (var file in new[] { "/admin/admin.js", "/admin/admin.css" })
            (await Host.Factory.CreateClient().GetAsync(file)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, file);

        var http = Host.Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = OAuthTestClient.Basic(TcpFactory.AdminUser, TcpFactory.AdminPassword);

        var page = await http.GetAsync("/admin");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        page.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        var csp = page.Headers.GetValues("Content-Security-Policy").Single();
        csp.Should().Contain("default-src 'self'").And.Contain("frame-ancestors 'none'").And.NotContain("unsafe-inline").And.NotContain("unsafe-eval");
        page.Headers.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");

        var html = await page.Content.ReadAsStringAsync();
        foreach (var tab in new[] { "Tasks", "New task", "Users", "Groups", "Requests", "Operations", "Diagnostics" })
            html.Should().Contain($">{tab}</button>");
        UrlAttributes().Matches(html).Select(m => m.Groups["url"].Value).Where(u => !u.StartsWith('#'))
            .Should().OnlyContain(u => u.StartsWith('/') || u.StartsWith("data:", StringComparison.Ordinal), "no CDN or other origin may be referenced");
        html.Should().NotContain("<script>").And.NotMatchRegex(@"\sstyle\s*=").And.NotMatchRegex(@"\son[a-z]+\s*=");

        long total = 0;
        foreach (var file in new[] { "/admin", "/admin/admin.js", "/admin/admin.css" })
        {
            var response = await http.GetAsync(file);
            response.StatusCode.Should().Be(HttpStatusCode.OK, file);
            total += (await response.Content.ReadAsByteArrayAsync()).Length;
        }
        total.Should().BeLessThan(150 * 1024, "NFR: admin UI payload < 150 KB");

        (await http.GetAsync("/admin/other.txt")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await http.GetAsync("/admin/../appsettings.json")).StatusCode.Should().NotBe(HttpStatusCode.OK);
        (await http.GetAsync("/admin/")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_scripts_never_use_unsafe_html_sinks()
    {
        var http = Host.Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = OAuthTestClient.Basic(TcpFactory.AdminUser, TcpFactory.AdminPassword);

        var js = await (await http.GetAsync("/admin/admin.js")).Content.ReadAsStringAsync();

        js.Should().NotContain("innerHTML").And.NotContain("outerHTML").And.NotContain("insertAdjacentHTML")
            .And.NotContain("document.write").And.NotContain("eval(").And.NotContain("new Function");
    }

    [Fact] // the definitions endpoint that feeds the New-task form
    public async Task Definitions_endpoint_describes_the_seeded_task_types()
    {
        var response = await Host.Admin().Get("/definitions");

        response.Status.Should().Be(HttpStatusCode.OK);
        var pr = response.Body!.AsArray().Single(d => d!["localId"]!.GetValue<string>() == "PR_APPROVAL")!;
        pr["attributes"]!.AsArray().Select(a => $"{a!["code"]}:{a["type"]}").Should().Equal("amount:FLOAT", "currency:STRING", "requester:STRING", "costCenter:STRING", "neededBy:DATE");
        pr["responses"]!.AsArray().Select(r => r!.GetValue<string>()).Should().Equal("approve", "reject");
    }
}

/// <summary>
/// Drives the real admin UI in a real browser (T005-07). Uses the Edge/Chrome that is installed on the machine, so no
/// browser download is needed; where none exists the test is skipped, loudly.
/// </summary>
[Collection(SqlCollection.Name)]
public class AdminUiBrowserTests(SqlServerFixture sql)
{
    [SkippableFact]
    public async Task Admin_can_create_a_task_and_see_it_in_the_list()
    {
        var host = SpiHost.Isolated(sql, kestrel: true);
        var user = await host.AddUserAsync(name: "ui.alice", email: "ui.alice@corp.example");
        var baseUrl = host.Factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');

        using var playwright = await Playwright.CreateAsync();
        IBrowser? browser = null;
        foreach (var channel in new[] { "msedge", "chrome" })
        {
            try { browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Channel = channel, Headless = true }); break; }
            catch (PlaywrightException) { /* try the next installed browser */ }
        }
        Skip.If(browser is null, "Neither Microsoft Edge nor Google Chrome is installed; cannot run the browser smoke test.");

        await using var _ = browser;
        var context = await browser!.NewContextAsync(new BrowserNewContextOptions
        {
            HttpCredentials = new HttpCredentials { Username = TcpFactory.AdminUser, Password = TcpFactory.AdminPassword, Origin = baseUrl },
        });
        var page = await context.NewPageAsync();
        var problems = new List<string>();
        page.Console += (_, m) => { if (m.Type is "error" or "warning") problems.Add($"console {m.Type}: {m.Text}"); };
        page.PageError += (_, e) => problems.Add("page error: " + e);
        var offOrigin = new List<string>();
        page.Request += (_, r) => { if (!r.Url.StartsWith(baseUrl, StringComparison.Ordinal) && !r.Url.StartsWith("data:", StringComparison.Ordinal)) offOrigin.Add(r.Url); };

        await page.GotoAsync(baseUrl + "/admin");
        (await page.TitleAsync()).Should().Be("Integrove TP - Admin");

        // New task tab: the form is built from the live definitions
        await page.ClickAsync("#tabs button[data-tab=new]");
        await page.WaitForSelectorAsync("#new-task [name=definitionLocalId] option", new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached });
        await page.SelectOptionAsync("#new-task [name=definitionLocalId]", "PR_APPROVAL");
        await page.WaitForSelectorAsync("[data-attribute=amount]");
        const string subject = "Approve PR 4711 - UI smoke <b>test</b>";
        await page.FillAsync("#new-task [name=users]", "ui.alice@corp.example");
        await page.FillAsync("#new-task [name=subjectEn]", subject);
        await page.FillAsync("[data-attribute=amount]", "1234.50");
        await page.FillAsync("[data-attribute=currency]", "ZAR");
        await page.FillAsync("#new-task [name=description]", "<p>Please approve</p><script>alert(1)</script>");
        await page.ClickAsync("#new-task button[type=submit]");

        await page.WaitForSelectorAsync("#new-result:not([hidden])");
        (await page.TextContentAsync("#banner"))!.Should().Contain("Task created");
        var created = (await page.TextContentAsync("#new-result"))!;
        created.Should().Contain("\"status\": \"READY\"").And.Contain(user);

        // Tasks tab: the new task is listed; the subject is shown as text, not interpreted as markup
        await page.ClickAsync("#tabs button[data-tab=tasks]");
        var row = page.Locator("#task-table tbody tr", new PageLocatorOptions { HasText = "UI smoke" });
        await row.WaitForAsync();
        (await row.InnerTextAsync()).Should().Contain(subject).And.Contain("READY");
        (await page.Locator("#task-table b").CountAsync()).Should().Be(0);

        // detail panel + a lifecycle action
        await row.ClickAsync();
        await page.WaitForSelectorAsync("#task-detail:not([hidden]) h2");
        (await page.InnerTextAsync("#task-detail")).Should().Contain("SPI representation").And.Contain("Please approve");
        (await page.InnerTextAsync("#task-detail")).Should().NotContain("alert(1)");
        await page.ClickAsync("#task-detail button:has-text('Cancel')");
        await page.WaitForSelectorAsync("#task-table tbody tr:has-text('CANCELED')");
        (await host.DbAsync(db => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(
            db.Tasks, t => t.Status == "CANCELED"))).Should().Be(1);

        // other tabs render without errors
        foreach (var tab in new[] { "users", "groups", "operations", "requests", "diagnostics" })
        {
            await page.ClickAsync($"#tabs button[data-tab={tab}]");
            (await page.IsVisibleAsync($"#tab-{tab}")).Should().BeTrue(tab);
        }
        await page.ClickAsync("#tabs button[data-tab=users]");
        await page.WaitForSelectorAsync("#user-table tbody tr:has-text('ui.alice')");
        await page.ClickAsync("#tabs button[data-tab=diagnostics]");
        await page.ClickAsync("#get-token");
        await page.WaitForSelectorAsync("#token-result:not([hidden])");
        (await page.TextContentAsync("#token-result"))!.Should().Contain("access_token").And.Contain("spi.tech");

        problems.Should().BeEmpty("the CSP and the script must not produce console errors");
        offOrigin.Should().BeEmpty("the UI must not load anything from another origin");
    }
}
