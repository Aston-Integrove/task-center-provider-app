using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tcp.Infrastructure.Persistence;
using Tcp.Infrastructure.Tasks;
using Tcp.TestSupport;

namespace Tcp.IntegrationTests;

[Collection(SqlCollection.Name)]
public class SpiRoutingAndAuthTests(SqlServerFixture sql)
{
    private SpiHost Host => SpiHost.Get(sql, "shared");

    public static IEnumerable<object[]> BasePaths() =>
    [
        ["/task-provider/v2"],
        ["/api/task-provider/v2"],
    ];

    [Theory] // T004-08: both bases are served (R-03)
    [MemberData(nameof(BasePaths))]
    public async Task Both_base_paths_serve_the_spi_for_technical_and_user_tokens(string basePath)
    {
        var tech = await Host.TechAsync(basePath);
        var user = await Host.UserAsync(await Host.AddUserAsync(), basePath);

        (await tech.Get("/capabilities")).Status.Should().Be(HttpStatusCode.OK);
        (await user.Get("/capabilities")).Status.Should().Be(HttpStatusCode.OK);
        (await tech.Get("/taskDefinitions?languages=en-US")).Status.Should().Be(HttpStatusCode.OK);
    }

    [Theory] // T004-08: 401 shape
    [MemberData(nameof(BasePaths))]
    public async Task Missing_or_invalid_tokens_get_a_401_with_the_sap_error_body(string basePath)
    {
        var anonymous = Host.Anonymous(basePath);

        var missing = await anonymous.Get("/capabilities");
        missing.Status.Should().Be(HttpStatusCode.Unauthorized);
        missing.ErrorCode.Should().Be("tcp.auth.unauthorized");
        missing.Raw.Headers.WwwAuthenticate.ToString().Should().Be("Bearer");
        missing.Raw.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        missing.Body!["error"]!["details"]!.AsArray().Should().BeEmpty();

        anonymous.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "garbage");
        var invalid = await anonymous.Get("/capabilities");
        invalid.Status.Should().Be(HttpStatusCode.Unauthorized);
        invalid.Raw.Headers.WwwAuthenticate.ToString().Should().Contain("invalid_token");
        invalid.ErrorCode.Should().Be("tcp.auth.unauthorized");
    }

    [Fact] // T004-08: 403 shapes
    public async Task Wrong_scope_is_403_forbidden_and_technical_tokens_cannot_use_user_endpoints()
    {
        var http = Host.Factory.CreateClient();
        var scim = await OAuthTestClient.GetScimTokenAsync(http);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", scim);
        var scimClient = new SpiClient(http);

        var forbidden = await scimClient.Get("/capabilities");
        forbidden.Status.Should().Be(HttpStatusCode.Forbidden);
        forbidden.ErrorCode.Should().Be("tcp.auth.forbidden");

        var tech = await Host.TechAsync();
        var urn = Uri.EscapeDataString(SpiHost.TaskUrn("whatever"));
        foreach (var response in new[]
                 {
                     await tech.Get($"/tasks/{urn}/description"),
                     await tech.Respond(SpiHost.TaskUrn("whatever"), "approve"),
                     await tech.Act(SpiHost.TaskUrn("whatever"), "claim"),
                 })
        {
            response.Status.Should().Be(HttpStatusCode.Forbidden);
            response.ErrorCode.Should().Be("tcp.auth.userContextRequired");
        }
    }

    [Theory] // T004-19, FR-SPI-05
    [MemberData(nameof(BasePaths))]
    public async Task Unsupported_spi_paths_return_501_not_implemented(string basePath)
    {
        var tech = await Host.TechAsync(basePath);
        var user = await Host.UserAsync(await Host.AddUserAsync(), basePath);
        var urn = Uri.EscapeDataString(SpiHost.TaskUrn("x"));

        var attempts = new (SpiClient Client, HttpMethod Method, string Path)[]
        {
            (tech, HttpMethod.Post, "/bulkOperation"),
            (tech, HttpMethod.Post, "/configuration/push"),
            (tech, HttpMethod.Get, $"/tasks/{urn}/details"),
            (tech, HttpMethod.Get, $"/tasks/{urn}/attachments"),
            (tech, HttpMethod.Get, $"/tasks/{urn}/attachments/$count"),
            (tech, HttpMethod.Get, $"/tasks/{urn}/comments"),
            (tech, HttpMethod.Post, $"/tasks/{urn}/comments"),
            (tech, HttpMethod.Put, $"/tasks/{urn}"),
            (tech, HttpMethod.Delete, $"/tasks/{urn}"),
            (user, HttpMethod.Get, "/users"),
            (user, HttpMethod.Get, $"/tasks/{urn}/response"), // wrong method on a supported path
            (tech, HttpMethod.Get, "/something/entirely/else"),
        };
        foreach (var (client, method, path) in attempts)
        {
            var response = await client.SendAsync(method, path, method == HttpMethod.Get ? null : new JsonObject());
            response.Status.Should().Be(HttpStatusCode.NotImplemented, $"{method} {path}");
            response.ErrorCode.Should().Be("tcp.spi.notImplemented");
        }
    }

    [Fact] // T004-19: unsupported paths still need authentication
    public async Task Unsupported_paths_require_authentication()
    {
        var response = await Host.Anonymous().Post("/bulkOperation", new JsonObject());
        response.Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact] // FR-SPI-04: messages are localised from Accept-Language
    public async Task Error_messages_are_localised()
    {
        var tech = await Host.TechAsync();

        var en = await tech.SendAsync(HttpMethod.Post, "/bulkOperation", new JsonObject(), "en-US");
        var de = await tech.SendAsync(HttpMethod.Post, "/bulkOperation", new JsonObject(), "de-DE,de;q=0.9");
        var fr = await tech.SendAsync(HttpMethod.Post, "/bulkOperation", new JsonObject(), "fr-FR");

        en.ErrorMessage.Should().Be("This operation is not supported by this provider.");
        de.ErrorMessage.Should().Contain("nicht unterstützt");
        fr.ErrorMessage.Should().Be(en.ErrorMessage); // unsupported language -> English
    }

    [Fact]
    public async Task Responses_use_json_utf8_content_type()
    {
        var tech = await Host.TechAsync();
        var response = await tech.Get("/capabilities");
        response.Raw.Content.Headers.ContentType!.ToString().Should().Be("application/json; charset=utf-8");
    }
}

[Collection(SqlCollection.Name)]
public class SpiCapabilitiesAndDefinitionTests(SqlServerFixture sql)
{
    private SpiHost Host => SpiHost.Get(sql, "shared");

    [Fact] // T004-09, US-004-1: exact payload, values are strings
    public async Task Capabilities_payload_is_exact()
    {
        var tech = await Host.TechAsync();

        var response = await tech.Get("/capabilities");

        response.Status.Should().Be(HttpStatusCode.OK);
        var expected = JsonNode.Parse("""
        {"value":[
          {"name":"tasks.pull","value":"true"},
          {"name":"taskDefinitions.pull","value":"true"},
          {"name":"tasks.push","value":"false"},
          {"name":"substitutions","value":"false"},
          {"name":"user.existence","value":"false"},
          {"name":"global.operations","value":"false"}
        ]}
        """);
        JsonNode.DeepEquals(response.Body, expected).Should().BeTrue(response.RawText);
        response.Value.Select(c => c!["value"]!.GetValueKind()).Should().OnlyContain(k => k == System.Text.Json.JsonValueKind.String);
    }

    private static string[] LocalIds(SpiResponse r) =>
        r.Value.Select(d => d!["localId"]!.GetValue<string>()).ToArray();

    [Fact] // US-004-2.1: paging by urn, ordinal
    public async Task Definitions_are_paged_in_urn_order()
    {
        var tech = await Host.TechAsync();

        var first = await tech.Get("/taskDefinitions?$top=2&$skip=0&languages=en-US");
        var second = await tech.Get("/taskDefinitions?$top=2&$skip=2&languages=en-US");
        var beyond = await tech.Get("/taskDefinitions?$top=2&$skip=4&languages=en-US");
        var all = await tech.Get("/taskDefinitions?languages=en-US");

        LocalIds(first).Should().Equal("INVOICE_EXCEPTION", "LEAVE_APPROVAL");
        LocalIds(second).Should().Equal("PR_APPROVAL");
        beyond.Status.Should().Be(HttpStatusCode.OK);
        beyond.Value.Should().BeEmpty();
        LocalIds(all).Should().Equal("INVOICE_EXCEPTION", "LEAVE_APPROVAL", "PR_APPROVAL");
        all.Value.Select(d => d!["urn"]!.GetValue<string>()).Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Theory] // US-004-2.2
    [InlineData("$top=0&languages=en-US", "$top")]
    [InlineData("$top=1001&languages=en-US", "$top")]
    [InlineData("$top=-1&languages=en-US", "$top")]
    [InlineData("$top=abc&languages=en-US", "$top")]
    [InlineData("$skip=-1&languages=en-US", "$skip")]
    [InlineData("$skip=x&languages=en-US", "$skip")]
    [InlineData("$top=10", "languages")]
    [InlineData("languages=", "languages")]
    [InlineData("languages=en", "languages")]
    [InlineData("languages=en-us", "languages")]
    [InlineData("languages=en_US", "languages")]
    [InlineData("languages=en-US,xx", "languages")]
    [InlineData("languages=a-A,b-B,c-C,d-D,e-E,f-F,g-G,h-H,i-I,j-J,k-K", "languages")]
    public async Task Invalid_parameters_are_400_with_the_parameter_as_target(string query, string target)
    {
        var tech = await Host.TechAsync();

        var response = await tech.Get("/taskDefinitions?" + query);

        response.Status.Should().Be(HttpStatusCode.BadRequest);
        response.ErrorCode.Should().Be("tcp.spi.invalidParameter");
        response.ErrorTarget.Should().Be(target);
        response.ErrorMessage.Should().Contain(target);
    }

    [Fact] // US-004-2.3
    public async Task Definition_contains_all_required_and_defined_properties()
    {
        var tech = await Host.TechAsync();

        var response = await tech.Get($"/taskDefinitions/{Uri.EscapeDataString(SpiHost.DefinitionUrn("PR_APPROVAL"))}?languages=en-US,de-DE");

        response.Status.Should().Be(HttpStatusCode.OK);
        var d = response.Body!.AsObject();
        d["urn"]!.GetValue<string>().Should().Be("urn:sap.odm.bpm.taskdefinition:integrove:tcproto:dev:PR_APPROVAL");
        (d["applicationId"]!.GetValue<string>(), d["applicationInstanceId"]!.GetValue<string>(), d["tenantId"]!.GetValue<string>(), d["localId"]!.GetValue<string>())
            .Should().Be(("integrove", "tcproto", "dev", "PR_APPROVAL"));
        d["name"]!.AsArray().Select(n => n!["languageCode"]!.GetValue<string>()).Should().Equal("en-US", "de-DE");

        var responses = d["possibleResponses"]!.AsArray();
        responses.Select(r => r!["code"]!.GetValue<string>()).Should().Equal("approve", "reject");
        var reject = responses[1]!;
        (reject["nature"]!.GetValue<string>(), reject["commentRequired"]!.GetValue<string>(), reject["reasonRequired"]!.GetValue<string>())
            .Should().Be(("NEGATIVE", "REQUIRED", "OPTIONAL"));
        reject["possibleReasons"]!.AsArray().Select(r => r!["code"]!.GetValue<string>()).Should().Equal("budget", "duplicate", "other");
        responses[0]!["capabilities"]![0]!["name"]!.GetValue<string>().Should().Be("tasks.bulk.operations");
        responses[0]!["capabilities"]![0]!["value"]!.GetValue<bool>().Should().BeFalse();

        d["possibleActions"]!.AsArray().Select(a => a!["code"]!.GetValue<string>()).Should().Equal("claim", "release", "increasePriority");
        d["customAttributes"]!.AsArray().Select(a => $"{a!["code"]}:{a["type"]}").Should()
            .Equal("amount:FLOAT", "currency:STRING", "requester:STRING", "costCenter:STRING", "neededBy:DATE");
        d["capabilities"]![0]!["name"]!.GetValue<string>().Should().Be("tasks.description");
        d["capabilities"]![0]!["value"]!.GetValue<bool>().Should().BeTrue();
        d["taskDetailsSettings"]!["webUISettings"]!["uiType"]!.GetValue<string>().Should().Be("Default");
        d["taskDetailsSettings"]!["mobileUISettings"]!["uiType"]!.GetValue<string>().Should().Be("Default");
    }

    [Fact] // the seed table of the spec
    public async Task Seeded_definitions_match_the_spec_table()
    {
        var tech = await Host.TechAsync();
        var all = (await tech.Get("/taskDefinitions?languages=en-US")).Value;
        JsonNode Find(string id) => all.Single(d => d!["localId"]!.GetValue<string>() == id)!;

        var leave = Find("LEAVE_APPROVAL");
        leave["possibleResponses"]!.AsArray().Select(r => r!["code"]!.GetValue<string>()).Should().Equal("approve", "reject");
        leave["possibleActions"]!.AsArray().Select(a => a!["code"]!.GetValue<string>()).Should().Equal("claim", "release");
        leave["customAttributes"]!.AsArray().Select(a => $"{a!["code"]}:{a["type"]}").Should()
            .BeEquivalentTo("leaveType:STRING", "fromDate:DATE", "toDate:DATE", "days:INTEGER");

        var invoice = Find("INVOICE_EXCEPTION");
        invoice["possibleResponses"]!.AsArray().Select(r => r!["code"]!.GetValue<string>()).Should().Equal("accept", "return");
        var ret = invoice["possibleResponses"]![1]!;
        ret["reasonRequired"]!.GetValue<string>().Should().Be("REQUIRED");
        ret["possibleReasons"]!.AsArray().Select(r => r!["code"]!.GetValue<string>()).Should().BeEquivalentTo("price", "quantity");
        invoice["customAttributes"]!.AsArray().Select(a => $"{a!["code"]}:{a["type"]}").Should()
            .BeEquivalentTo("vendor:STRING", "invoiceNo:STRING", "variance:FLOAT", "postingDate:DATE");

        all.Should().OnlyContain(d => d!["capabilities"]![0]!["name"]!.GetValue<string>() == "tasks.description");
    }

    [Fact] // US-004-2.3 / digest section 7: languages filter + isDefault
    public async Task Names_honour_the_languages_parameter()
    {
        var tech = await Host.TechAsync();
        var urn = Uri.EscapeDataString(SpiHost.DefinitionUrn("PR_APPROVAL"));

        var german = (await tech.Get($"/taskDefinitions/{urn}?languages=de-DE")).Body!;
        german["name"]!.AsArray().Should().ContainSingle();
        german["name"]![0]!["text"]!.GetValue<string>().Should().Be("Bestellanforderung genehmigen");
        german["name"]![0]!["isDefault"]!.GetValue<bool>().Should().BeTrue();

        var both = (await tech.Get($"/taskDefinitions/{urn}?languages=de-DE,en-US")).Body!;
        both["name"]!.AsArray().Select(n => (n!["languageCode"]!.GetValue<string>(), n["isDefault"]!.GetValue<bool>()))
            .Should().Equal(("de-DE", false), ("en-US", true));

        var unknown = (await tech.Get($"/taskDefinitions/{urn}?languages=fr-FR")).Body!;
        unknown["name"]!.AsArray().Should().ContainSingle().Which!["languageCode"]!.GetValue<string>().Should().Be("en-US");
    }

    [Fact] // US-004-2.4
    public async Task Single_definition_returns_404_when_unknown_and_accepts_raw_and_encoded_urns()
    {
        var tech = await Host.TechAsync();
        var urn = SpiHost.DefinitionUrn("PR_APPROVAL");

        (await tech.Get($"/taskDefinitions/{urn}?languages=en-US")).Status.Should().Be(HttpStatusCode.OK);
        (await tech.Get($"/taskDefinitions/{Uri.EscapeDataString(urn)}?languages=en-US")).Status.Should().Be(HttpStatusCode.OK);
        (await tech.Get($"/taskDefinitions/{Uri.EscapeDataString(Uri.EscapeDataString(urn))}?languages=en-US")).Status.Should().Be(HttpStatusCode.OK);

        foreach (var missing in new[] { SpiHost.DefinitionUrn("NOPE"), SpiHost.TaskUrn("PR_APPROVAL"), "garbage" })
        {
            var response = await tech.Get($"/taskDefinitions/{Uri.EscapeDataString(missing)}?languages=en-US");
            response.Status.Should().Be(HttpStatusCode.NotFound, missing);
            response.ErrorCode.Should().Be("tcp.spi.taskDefinitionNotFound");
        }

        (await tech.Get($"/taskDefinitions/{urn}")).Status.Should().Be(HttpStatusCode.BadRequest); // languages missing
    }

    [Fact]
    public async Task Definitions_are_identical_on_both_base_paths()
    {
        var a = await (await Host.TechAsync("/task-provider/v2")).Get("/taskDefinitions?languages=en-US");
        var b = await (await Host.TechAsync("/api/task-provider/v2")).Get("/taskDefinitions?languages=en-US");
        JsonNode.DeepEquals(a.Body, b.Body).Should().BeTrue();
    }
}

[Collection(SqlCollection.Name)]
public class DefinitionSeederTests(SqlServerFixture sql)
{
    private static string SeedJson() => File.ReadAllText(Path.Combine(
        OpenApiContract.RepositoryRoot, "specs", "004-task-provider-spi", "seed", "task-definitions.json"));

    private static readonly ProviderIdentity Identity = new(SpiHost.AppId, SpiHost.InstanceId, SpiHost.Tenant);

    private async Task<SeedResult> SeedAsync(SpiHost host, string json)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        _ = host.Factory.Server;
        return await scope.ServiceProvider.GetRequiredService<DefinitionSeeder>().SeedAsync(json, Identity, CancellationToken.None);
    }

    [Fact] // T004-07: loaded at startup, idempotent upsert
    public async Task Seeding_is_idempotent_and_updates_only_what_changed()
    {
        var host = SpiHost.Isolated(sql);
        _ = host.Factory.Server; // startup seeds

        var initial = await host.DbAsync(db => db.TaskDefinitions.AsNoTracking().OrderBy(d => d.Urn).ToListAsync());
        initial.Select(d => d.LocalId).Should().Equal("INVOICE_EXCEPTION", "LEAVE_APPROVAL", "PR_APPROVAL");

        var again = await SeedAsync(host, SeedJson());
        again.Should().Be(new SeedResult(0, 0, 3));
        var afterNoop = await host.DbAsync(db => db.TaskDefinitions.AsNoTracking().OrderBy(d => d.Urn).ToListAsync());
        afterNoop.Select(d => d.ModifiedAt).Should().Equal(initial.Select(d => d.ModifiedAt)); // untouched

        await Task.Delay(20);
        var changed = JsonNode.Parse(SeedJson())!.AsObject();
        changed["value"]![0]!["name"]![0]!["text"] = "Changed name";
        var update = await SeedAsync(host, changed.ToJsonString());
        update.Should().Be(new SeedResult(0, 1, 2));

        var rows = await host.DbAsync(db => db.TaskDefinitions.AsNoTracking().OrderBy(d => d.Urn).ToListAsync());
        rows.Single(r => r.LocalId == "PR_APPROVAL").NameJson.Should().Contain("Changed name");
        rows.Single(r => r.LocalId == "PR_APPROVAL").ModifiedAt.Should().BeAfter(initial.Single(r => r.LocalId == "PR_APPROVAL").ModifiedAt);
        rows.Single(r => r.LocalId == "LEAVE_APPROVAL").ModifiedAt.Should().Be(initial.Single(r => r.LocalId == "LEAVE_APPROVAL").ModifiedAt);
    }

    [Fact] // definition changes never bump tasks
    public async Task Changing_a_definition_does_not_touch_tasks()
    {
        var host = SpiHost.Isolated(sql);
        var task = await host.AddTaskAsync(new TaskSpec());

        var changed = JsonNode.Parse(SeedJson())!.AsObject();
        changed["value"]![2]!["name"]![0]!["text"] = "Renamed definition";
        await SeedAsync(host, changed.ToJsonString());

        (await host.LoadTaskAsync(task.Urn)).ModifiedAt.Should().Be(task.ModifiedAt);
    }

    [Theory]
    [InlineData("""{"value":[{"name":[{"languageCode":"en-US","text":"x"}]}]}""")]            // no localId
    [InlineData("""{"value":[{"localId":"A B","name":[{"languageCode":"en-US","text":"x"}]}]}""")] // invalid urn part
    [InlineData("""{"value":[{"localId":"A","name":[]}]}""")]                                  // empty name
    [InlineData("""{"nothing":true}""")]
    public async Task Invalid_seed_files_are_rejected(string json)
    {
        var host = SpiHost.Isolated(sql);
        var act = () => SeedAsync(host, json);
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact] // T004-02: collation / ordering
    public async Task Urns_sort_ordinally_in_the_database()
    {
        var host = SpiHost.Isolated(sql);
        var when = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var id in new[] { "Z", "a", "_", "A", "0", "m" })
            await host.AddTaskAsync(new TaskSpec { LocalId = id, ModifiedAt = when });

        var ordered = await host.DbAsync(db => db.Tasks.AsNoTracking().OrderBy(t => t.ModifiedAt).ThenBy(t => t.Urn).Select(t => t.LocalId).ToListAsync());

        // ordinal: digits < upper-case < '_' < lower-case
        ordered.Should().Equal("0", "A", "Z", "_", "a", "m");
    }

    [Fact] // T004-02: indexes and constraints
    public async Task Pull_index_is_unique_and_urn_is_the_primary_key()
    {
        var host = SpiHost.Isolated(sql);
        _ = host.Factory.Server;

        // name:unique:origin, origin 'c' = CREATE INDEX, 'pk' = PRIMARY KEY constraint
        var indexes = await host.DbAsync(db => db.Database.SqlQueryRaw<string>(
            "SELECT name || ':' || \"unique\" || ':' || origin AS Value FROM pragma_index_list('TaskInstance')").ToListAsync());

        indexes.Should().Contain("IX_TaskInstance_Pull:1:c");
        indexes.Should().Contain(i => i.EndsWith(":1:pk"));
        indexes.Should().Contain(i => i.StartsWith("IX_TaskInstance_Processor"));

        var act = () => host.AddTaskAsync(new TaskSpec { Status = "NOT_A_STATUS" });
        await act.Should().ThrowAsync<DbUpdateException>(); // check constraint
    }
}
