using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Tcp.Domain.Tasks;
using Tcp.TestSupport;

namespace Tcp.IntegrationTests;

[Collection(SqlCollection.Name)]
public class AppPageBasicTests(SqlServerFixture sql)
{
    private SpiHost Host => SpiHost.Get(sql, "app");

    private static string Path(TaskInstance t) => $"/app/tasks/{Uri.EscapeDataString(t.Urn)}";

    private async Task<HttpResponseMessage> GetAsync(string path, bool admin = true, string? acceptLanguage = null)
    {
        var http = Host.Factory.CreateClient();
        if (admin) http.DefaultRequestHeaders.Authorization = OAuthTestClient.Basic(TcpFactory.AdminUser, TcpFactory.AdminPassword);
        if (acceptLanguage is not null) http.DefaultRequestHeaders.AcceptLanguage.ParseAdd(acceptLanguage);
        return await http.GetAsync(path);
    }

    [Fact] // US-005-6.2 (Basic mode) + security headers
    public async Task Page_requires_admin_credentials_and_sets_security_headers()
    {
        var task = await Host.AddTaskAsync(new TaskSpec());

        var anonymous = await GetAsync(Path(task), admin: false);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        anonymous.Headers.WwwAuthenticate.ToString().Should().Contain("Basic");

        var http = Host.Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", await OAuthTestClient.GetTechTokenAsync(http));
        (await http.GetAsync(Path(task))).StatusCode.Should().Be(HttpStatusCode.Unauthorized); // bearer tokens are not accepted here

        var ok = await GetAsync(Path(task));
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        ok.Content.Headers.ContentType!.ToString().Should().Be("text/html; charset=utf-8");
        ok.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("default-src 'none'").And.Contain("frame-ancestors 'none'");
        ok.Headers.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
        ok.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact] // US-005-6.1
    public async Task Page_shows_subject_status_attributes_description_recipients_and_history()
    {
        var alice = await Host.AddUserAsync(name: "alice.app", email: "alice.app@corp.example");
        var bob = await Host.AddUserAsync(name: "bob.app");
        var task = await Host.AddTaskAsync(new TaskSpec
        {
            Users = [alice, bob], Groups = ["APP_APPROVERS"], Priority = TaskPriorities.High,
            Subject = "Approve PR 4711 - Laptop", SubjectDe = "BANF 4711 genehmigen - Laptop",
            Descriptions = [new("en-US", "text/html", "<p>Please approve <b>PR 4711</b></p>"), new("de-DE", "text/html", "<p>Bitte genehmigen</p>")],
            Attributes = new Dictionary<string, string> { ["amount"] = "1234.50", ["currency"] = "ZAR", ["neededBy"] = "2030-02-01" },
        });
        await (await Host.UserAsync(alice)).Act(task.Urn, "claim");
        await (await Host.UserAsync(alice)).Act(task.Urn, "increasePriority");

        var html = await (await GetAsync(Path(task))).Content.ReadAsStringAsync();

        html.Should().Contain("<h1>Approve PR 4711 - Laptop</h1>").And.Contain("RESERVED").And.Contain(task.Urn);
        html.Should().Contain("Approve Purchase Requisition"); // definition name
        html.Should().Contain("<th scope=\"row\">Amount</th><td>1234.5</td>").And.Contain("ZAR").And.Contain("2030-02-01");
        html.Should().Contain("<p>Please approve <b>PR 4711</b></p>");
        html.Should().Contain("alice.app").And.Contain("alice.app@corp.example").And.Contain("bob.app").And.Contain("APP_APPROVERS");
        html.Should().Contain("action: claim").And.Contain("action: increasePriority").And.Contain("OK");
        html.Should().Contain("Read-only view");
        html.Should().NotContain("<form"); // administrators cannot act here
    }

    [Fact] // language selection
    public async Task Page_follows_the_requested_language()
    {
        var user = await Host.AddUserAsync();
        var task = await Host.AddTaskAsync(new TaskSpec
        {
            Users = [user], Subject = "Approve PR", SubjectDe = "BANF genehmigen",
            Descriptions = [new("en-US", "text/html", "<p>English text</p>"), new("de-DE", "text/html", "<p>Deutscher Text</p>")],
            Attributes = new Dictionary<string, string> { ["amount"] = "5" },
        });

        var de = WebUtility.HtmlDecode(await (await GetAsync(Path(task) + "?lang=de-DE")).Content.ReadAsStringAsync());
        de.Should().Contain("<h1>BANF genehmigen</h1>").And.Contain("Deutscher Text").And.Contain("Beschreibung").And.Contain("Empfänger").And.Contain("Betrag");
        de.Should().Contain("<html lang=\"de\">");

        var viaHeader = await (await GetAsync(Path(task), acceptLanguage: "de-DE")).Content.ReadAsStringAsync();
        viaHeader.Should().Contain("<h1>BANF genehmigen</h1>");

        var en = await (await GetAsync(Path(task))).Content.ReadAsStringAsync();
        en.Should().Contain("<h1>Approve PR</h1>").And.Contain("English text").And.Contain("Description");
    }

    [Fact] // T005-08: encoded output (XSS)
    public async Task Every_dynamic_value_is_html_encoded()
    {
        const string payload = "<script>alert('x')</script>";
        var user = await Host.AddUserAsync(name: "xss.user", email: "x@corp.example");
        await Host.DbAsync(db => db.ScimUsers.Where(u => u.GlobalUserId == user)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.DisplayName, "\"><img src=x onerror=alert(1)>")));
        var task = await Host.AddTaskAsync(new TaskSpec
        {
            Users = [user], Groups = ["<b>evil-group</b>"], Subject = payload + " subject", SubjectDe = payload,
            Attributes = new Dictionary<string, string> { ["requester"] = "<img src=x onerror=alert(2)>", ["currency"] = payload },
            // stored raw, as if written by an older version or straight into the database
            Descriptions = [new("en-US", "text/html", "<p>Hello</p><script>alert('desc')</script><img src=x onerror=alert(3)><a href=\"javascript:alert(4)\">link</a>")],
        });
        await Host.DbAsync(async db =>
        {
            db.OperationLog.Add(new OperationLogEntry
            {
                TaskUrn = task.Urn, Kind = "RESPONSE", Code = "approve", Comment = "<script>alert('comment')</script>", UserId = user,
                At = DateTime.UtcNow, Outcome = "OK",
            });
            db.TaskOperationErrors.Add(new TaskOperationError
                { TaskUrn = task.Urn, ExecutedAt = DateTime.UtcNow, Code = "approve", Message = "<img src=x onerror=alert(5)>", ExecutedBy = user });
            await db.SaveChangesAsync();
        });

        var html = await (await GetAsync(Path(task))).Content.ReadAsStringAsync();

        // encoded text such as "&lt;img src=x onerror=...&gt;" is the correct output; what must never appear is live markup
        html.Should().NotContain("<script").And.NotContain("javascript:").And.NotContain("<img").And.NotContain("<b>evil-group</b>");
        Regex.IsMatch(html, @"<[a-zA-Z][^>]*\son[a-z]+\s*=").Should().BeFalse("no element may carry an event handler attribute");
        html.Should().Contain("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt; subject");
        html.Should().Contain("&lt;b&gt;evil-group&lt;/b&gt;");
        html.Should().Contain("&lt;img src=x onerror=alert(2)&gt;");
        html.Should().Contain("<p>Hello</p>"); // the safe part of the description survives the second sanitising pass
    }

    [Theory]
    [InlineData("/app/tasks/urn%3Asap.odm.bpm.task%3Aintegrove%3Atcproto%3Adev%3Adoes-not-exist")]
    [InlineData("/app/tasks/%3Cscript%3Ealert(1)%3C%2Fscript%3E")]
    [InlineData("/app/tasks/garbage")]
    public async Task Unknown_tasks_get_a_404_page_that_echoes_nothing(string path)
    {
        var response = await GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("The task was not found.").And.NotContain("alert(1)").And.NotContain("garbage");
    }

    [Fact]
    public async Task Stylesheet_is_public_and_actions_are_not_available_in_basic_mode()
    {
        var css = await Host.Factory.CreateClient().GetAsync("/app/app.css");
        css.StatusCode.Should().Be(HttpStatusCode.OK);
        css.Content.Headers.ContentType!.MediaType.Should().Be("text/css");

        var user = await Host.AddUserAsync();
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [user] });
        var http = Host.Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = OAuthTestClient.Basic(TcpFactory.AdminUser, TcpFactory.AdminPassword);
        var post = await http.PostAsync(Path(task) + "/respond", new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = "approve" }));
        post.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Host.LoadTaskAsync(task.Urn)).Status.Should().Be(TaskStatuses.Ready);
    }
}

[Collection(SqlCollection.Name)]
public partial class AppPageOidcTests(SqlServerFixture sql)
{
    private static readonly FakeIas Ias = new();

    private SpiHost Host => SpiHost.Get(sql, "app-oidc", FakeIas.Settings(), Ias.Wire());

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex TokenField();

    private static string Path(TaskInstance t) => $"/app/tasks/{Uri.EscapeDataString(t.Urn)}";

    private HttpClient Browser() => Host.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>Plays the browser through the authorization code flow and returns once the app has set its session cookie.</summary>
    private async Task<(HttpClient Browser, HttpResponseMessage Landing)> SignInAsync(string pagePath, JsonObject claims, string? nonceOverride = null)
    {
        var browser = Browser();
        var challenge = await browser.GetAsync(pagePath);
        challenge.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var authorize = challenge.Headers.Location!;
        authorize.GetLeftPart(UriPartial.Path).Should().Be("https://ias.test/oauth2/authorize");
        var query = System.Web.HttpUtility.ParseQueryString(authorize.Query);

        // what we told IAS
        query["client_id"].Should().Be(FakeIas.ClientId);
        query["response_type"].Should().Be("code");
        query["scope"].Should().Be("openid email profile");
        query["code_challenge_method"].Should().Be("S256");
        query["code_challenge"].Should().NotBeNullOrEmpty();
        query["redirect_uri"].Should().EndWith("/app/signin-oidc");

        var code = Ias.IssueCode(nonceOverride ?? query["nonce"]!, claims);
        var callback = await browser.GetAsync($"/app/signin-oidc?code={code}&state={Uri.EscapeDataString(query["state"]!)}");
        return (browser, callback);
    }

    private static JsonObject Login(string globalUserId, string? email = null) => new()
    {
        ["sub"] = "ias-" + globalUserId[..8], ["user_uuid"] = globalUserId, ["email"] = email ?? "user@corp.example", ["name"] = "IAS User",
    };

    [Fact] // US-005-6.2 (P2): unauthenticated -> IAS, then back to the page
    public async Task Entitled_user_signs_in_with_ias_and_sees_the_task_with_actions()
    {
        var user = await Host.AddUserAsync(name: "ias.alice");
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [user], Subject = "Approve PR 100", Descriptions = [new("en-US", "text/html", "<p>Desc</p>")] });
        Ias.TokenRequests.Clear();

        var (browser, callback) = await SignInAsync(Path(task), Login(user));

        callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
        callback.Headers.Location!.ToString().Should().Contain("/app/tasks/"); // back to where the user was going
        var token = Ias.TokenRequests.Single();
        (token["grant_type"], token["client_id"], token["client_secret"]).Should().Be(("authorization_code", FakeIas.ClientId, FakeIas.ClientSecret));
        token["code_verifier"].Should().NotBeNullOrEmpty(); // PKCE

        var page = await browser.GetAsync(Path(task));
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync();
        html.Should().Contain("Approve PR 100").And.Contain("Signed in as").And.Contain("ias.alice");
        html.Should().Contain("action=\"/app/tasks/").And.Contain("/respond\"").And.Contain("/action\"");
        html.Should().Contain(">Approve</button>").And.Contain(">Reject</button>").And.Contain(">Claim</button>");
        html.Should().NotContain(">Release</button>"); // not valid while nobody has claimed it
        TokenField().IsMatch(html).Should().BeTrue();
        page.Headers.GetValues("Set-Cookie").Should().Contain(c => c.StartsWith(".tcp.antiforgery"));
    }

    [Fact]
    public async Task Users_who_are_unknown_or_not_entitled_get_a_403_page()
    {
        var recipient = await Host.AddUserAsync();
        var stranger = await Host.AddUserAsync();
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [recipient] });

        var (strangerBrowser, _) = await SignInAsync(Path(task), Login(stranger));
        var notEntitled = await strangerBrowser.GetAsync(Path(task));
        notEntitled.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await notEntitled.Content.ReadAsStringAsync()).Should().Contain("not entitled").And.NotContain("Approve purchase");

        var (ghostBrowser, _) = await SignInAsync(Path(task), Login(Guid.NewGuid().ToString(), "ghost@nowhere.example"));
        (await ghostBrowser.GetAsync(Path(task))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact] // the user may also be found by e-mail when the IAS token carries no user_uuid
    public async Task Users_are_resolved_by_email_when_the_global_id_claim_is_missing()
    {
        var user = await Host.AddUserAsync(email: "by.mail@corp.example");
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [user] });

        var (browser, _) = await SignInAsync(Path(task), new JsonObject { ["sub"] = "ias-opaque", ["email"] = "by.mail@corp.example" });

        (await browser.GetAsync(Path(task))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact] // US-005-6.3: completion in the provider flows back to Task Center
    public async Task Responding_on_the_page_completes_the_task_and_reaches_the_delta_pull()
    {
        var user = await Host.AddUserAsync();
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [user], ModifiedAt = DateTime.UtcNow.AddMinutes(-10) });
        var (browser, _) = await SignInAsync(Path(task), Login(user));
        var html = await (await browser.GetAsync(Path(task))).Content.ReadAsStringAsync();
        var antiforgery = TokenField().Match(html).Groups[1].Value;

        var post = await browser.PostAsync(Path(task) + "/respond", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiforgery, ["code"] = "approve", ["comment"] = "approved in the app",
        }));

        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        post.Headers.Location!.ToString().Should().Contain("done=1");
        var stored = await Host.LoadTaskAsync(task.Urn);
        (stored.Status, stored.CompletedBy, stored.Processor).Should().Be((TaskStatuses.Completed, user, user));

        var pull = await (await Host.TechAsync()).Get($"/tasks?languages=en-US&modifiedAfter={SpiHost.Iso(task.ModifiedAt)}&$top=1000");
        pull.Value.Single(t => t!["urn"]!.GetValue<string>() == task.Urn)!["status"]!.GetValue<string>().Should().Be("COMPLETED");

        var after = await (await browser.GetAsync(post.Headers.Location)).Content.ReadAsStringAsync();
        after.Should().Contain("Done.").And.Contain("approved in the app").And.NotContain("<form");
    }

    [Fact]
    public async Task Actions_validate_csrf_rules_and_entitlement_and_show_localised_errors()
    {
        var user = await Host.AddUserAsync();
        var stranger = await Host.AddUserAsync();
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [user] });
        var (browser, _) = await SignInAsync(Path(task), Login(user));
        var antiforgery = TokenField().Match(await (await browser.GetAsync(Path(task))).Content.ReadAsStringAsync()).Groups[1].Value;
        Task<HttpResponseMessage> Post(HttpClient b, string kind, Dictionary<string, string> form) =>
            b.PostAsync($"{Path(task)}/{kind}", new FormUrlEncodedContent(form));

        // no antiforgery token -> rejected before anything happens
        (await Post(browser, "respond", new() { ["code"] = "approve" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // unauthenticated browser -> never executes
        var anonymous = await Post(Browser(), "respond", new() { ["code"] = "approve", ["__RequestVerificationToken"] = antiforgery });
        anonymous.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized);
        (await Host.LoadTaskAsync(task.Urn)).Status.Should().Be(TaskStatuses.Ready);

        // a business-rule rejection comes back as a readable, localised message and changes nothing
        var rejected = await Post(browser, "respond", new() { ["__RequestVerificationToken"] = antiforgery, ["code"] = "reject" });
        rejected.StatusCode.Should().Be(HttpStatusCode.Redirect);
        rejected.Headers.Location!.ToString().Should().Contain("error=tcp.spi.commentRequired");
        var errorPage = await (await browser.GetAsync(rejected.Headers.Location!.ToString().Replace("lang=en", "lang=de"))).Content.ReadAsStringAsync();
        errorPage.Should().Contain("Kommentar");
        (await Host.LoadTaskAsync(task.Urn)).Status.Should().Be(TaskStatuses.Ready);

        // claim through the page (action form)
        var claim = await Post(browser, "action", new() { ["__RequestVerificationToken"] = antiforgery, ["code"] = "claim" });
        claim.Headers.Location!.ToString().Should().Contain("done=1");
        (await Host.LoadTaskAsync(task.Urn)).Processor.Should().Be(user);

        // somebody else cannot act through the page, even with a valid session of their own
        var (strangerBrowser, _) = await SignInAsync(Path(task), Login(stranger));
        var token = TokenField().Match(await (await strangerBrowser.GetAsync(Path(task))).Content.ReadAsStringAsync()).Groups[1].Value;
        (await strangerBrowser.GetAsync(Path(task))).StatusCode.Should().Be(HttpStatusCode.Forbidden); // no page, hence no token to steal
        var attempt = await Post(strangerBrowser, "respond", new() { ["__RequestVerificationToken"] = token, ["code"] = "approve" });
        (await Host.LoadTaskAsync(task.Urn)).Status.Should().NotBe(TaskStatuses.Completed);
        attempt.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    [Fact] // OIDC protocol safety: an id_token with the wrong nonce must not create a session
    public async Task Login_with_a_mismatching_nonce_is_refused()
    {
        var user = await Host.AddUserAsync();
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [user] });

        var (browser, callback) = await SignInAsync(Path(task), Login(user), nonceOverride: "attacker-controlled-nonce");
        Ias.ForcedNonce = null;

        // the handler cannot validate the token against the nonce it generated, so the callback must not sign the user in
        var again = await browser.GetAsync(Path(task));
        again.StatusCode.Should().Be(HttpStatusCode.Redirect); // challenged again, not served
        again.Headers.Location!.GetLeftPart(UriPartial.Path).Should().Be("https://ias.test/oauth2/authorize");
        callback.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }
}
