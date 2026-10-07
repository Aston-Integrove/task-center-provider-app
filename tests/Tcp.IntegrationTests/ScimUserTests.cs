using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tcp.Domain.Identity;
using Tcp.Infrastructure.Persistence;
using Tcp.TestSupport;

namespace Tcp.IntegrationTests;

[Collection(SqlCollection.Name)]
public class ScimSchemaTests(SqlServerFixture sql)
{
    private ScimHost Host => ScimHost.Get(sql, "main");

    private TcpDbContext NewContext() =>
        Host.Factory.Services.CreateScope().ServiceProvider.GetRequiredService<TcpDbContext>();

    private static ScimUser NewUser(string userName, string? gid = null) => new()
    {
        Id = Guid.NewGuid(), UserName = userName, GlobalUserId = gid, Created = DateTime.UtcNow, LastModified = DateTime.UtcNow,
    };

    [Fact] // T003-01
    public async Task Tables_live_in_the_idm_schema()
    {
        _ = Host.Factory.CreateClient(); // start host => migrations
        await using var db = NewContext();
        var tables = await db.Database.SqlQueryRaw<string>(
            "SELECT TABLE_NAME AS Value FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'idm'").ToListAsync();

        tables.Should().BeEquivalentTo("ScimUser", "ScimGroup", "ScimGroupMember", "ScimAudit");
    }

    [Fact] // T003-01
    public async Task User_name_is_unique_case_insensitively()
    {
        _ = Host.Factory.CreateClient();
        var name = Scim.Name("uniq");
        await using var db = NewContext();
        db.ScimUsers.Add(NewUser(name));
        await db.SaveChangesAsync();

        await using var db2 = NewContext();
        db2.ScimUsers.Add(NewUser(name.ToUpperInvariant()));
        var act = () => db2.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact] // T003-01
    public async Task Global_user_id_is_unique_but_nulls_are_allowed_many_times()
    {
        _ = Host.Factory.CreateClient();
        var gid = Guid.NewGuid().ToString();
        await using var db = NewContext();
        db.ScimUsers.AddRange(NewUser(Scim.Name("n1")), NewUser(Scim.Name("n2")), NewUser(Scim.Name("n3"), gid));
        await db.SaveChangesAsync();

        await using var db2 = NewContext();
        db2.ScimUsers.Add(NewUser(Scim.Name("n4"), gid));
        var act = () => db2.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact] // T003-01
    public async Task Group_display_name_is_unique_case_insensitively_and_json_columns_are_checked()
    {
        _ = Host.Factory.CreateClient();
        var name = Scim.Name("grp");
        await using var db = NewContext();
        db.ScimGroups.Add(new ScimGroup { Id = Guid.NewGuid(), DisplayName = name, Created = DateTime.UtcNow, LastModified = DateTime.UtcNow });
        await db.SaveChangesAsync();

        await using var db2 = NewContext();
        db2.ScimGroups.Add(new ScimGroup { Id = Guid.NewGuid(), DisplayName = name.ToUpperInvariant(), Created = DateTime.UtcNow, LastModified = DateTime.UtcNow });
        var dup = () => db2.SaveChangesAsync();
        await dup.Should().ThrowAsync<DbUpdateException>();

        await using var db3 = NewContext();
        var bad = NewUser(Scim.Name("badjson"));
        bad.EmailsJson = "not json";
        db3.ScimUsers.Add(bad);
        var act = () => db3.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }
}

[Collection(SqlCollection.Name)]
public class ScimUserTests(SqlServerFixture sql)
{
    private ScimHost Host => ScimHost.Get(sql, "main");

    // ---- create / read (T003-06) -------------------------------------------------------------

    [Fact] // US-003-1.1
    public async Task Post_creates_user_with_meta_location_header_and_scim_content_type()
    {
        var scim = await Host.ClientAsync();
        var uuid = Guid.NewGuid().ToString();

        var res = await scim.CreateUser(userUuid: uuid);

        res.Raw.Content.Headers.ContentType!.MediaType.Should().Be("application/scim+json");
        res.Body!["id"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        res.Body["meta"]!["resourceType"]!.GetValue<string>().Should().Be("User");
        res.Body["meta"]!["created"]!.GetValue<string>().Should().MatchRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$");
        res.Body["meta"]!["lastModified"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        var location = res.Body["meta"]!["location"]!.GetValue<string>();
        location.Should().EndWith($"/scim/v2/Users/{res.Id}");
        res.Raw.Headers.Location!.ToString().Should().Be(location);
    }

    [Fact] // US-003-1.2: Global User ID derived, stored normalised, visible via SAP extension
    public async Task Global_user_id_is_normalised_and_returned_in_the_sap_extension()
    {
        var scim = await Host.ClientAsync();
        var uuid = Guid.NewGuid();

        var res = await scim.CreateUser(userUuid: uuid.ToString("N").ToUpperInvariant());

        res.Body![Scim.SapExt]!["userUuid"]!.GetValue<string>().Should().Be(uuid.ToString("D"));
        (await scim.Get($"/Users/{res.Id}")).Body![Scim.SapExt]!["userUuid"]!.GetValue<string>().Should().Be(uuid.ToString("D"));
    }

    [Fact] // FR-SCIM-07 fallback
    public async Task Guid_external_id_is_used_when_no_sap_extension_is_sent()
    {
        var scim = await Host.ClientAsync();
        var gid = Guid.NewGuid().ToString();
        var payload = Scim.UserPayload(Scim.Name("ext"), gid);
        payload.Remove(Scim.SapExt);
        payload["externalId"] = gid;

        var res = await scim.Post("/Users", payload);

        res.Status.Should().Be(HttpStatusCode.Created);
        res.Body![Scim.SapExt]!["userUuid"]!.GetValue<string>().Should().Be(gid);
    }

    [Fact] // US-003-1.3
    public async Task Duplicate_user_name_is_409_uniqueness_case_insensitively()
    {
        var scim = await Host.ClientAsync();
        var name = Scim.Name("dup");
        await scim.CreateUser(name);

        var res = await scim.Post("/Users", Scim.UserPayload(name.ToUpperInvariant(), Guid.NewGuid().ToString()));

        res.Status.Should().Be(HttpStatusCode.Conflict);
        res.ScimType.Should().Be("uniqueness");
        res.Body!["status"]!.GetValue<string>().Should().Be("409");
        res.Body["schemas"]![0]!.GetValue<string>().Should().Be("urn:ietf:params:scim:api:messages:2.0:Error");
        res.Raw.Content.Headers.ContentType!.MediaType.Should().Be("application/scim+json");
    }

    [Fact]
    public async Task Duplicate_global_user_id_is_409()
    {
        var scim = await Host.ClientAsync();
        var uuid = Guid.NewGuid().ToString();
        await scim.CreateUser(userUuid: uuid);

        var res = await scim.Post("/Users", Scim.UserPayload(Scim.Name("other"), uuid));

        res.Status.Should().Be(HttpStatusCode.Conflict);
        res.ScimType.Should().Be("uniqueness");
    }

    [Fact] // US-003-1.4
    public async Task Unknown_attributes_and_extensions_are_ignored()
    {
        var scim = await Host.ClientAsync();

        var res = await scim.CreateUser(customise: u =>
        {
            u["somethingNew"] = new JsonObject { ["a"] = 1 };
            u["urn:example:custom:1.0:User"] = new JsonObject { ["costCenter"] = "42" };
        });

        res.Body!.ContainsKey("somethingNew").Should().BeFalse();
        res.Body.ContainsKey("urn:example:custom:1.0:User").Should().BeFalse();
    }

    [Fact] // US-003-1.5
    public async Task Missing_global_user_id_is_400_invalid_value()
    {
        var scim = await Host.ClientAsync();
        var payload = Scim.UserPayload(Scim.Name("nogid"), "x");
        payload.Remove(Scim.SapExt);
        payload["externalId"] = "EMPLOYEE-123"; // not a GUID

        var res = await scim.Post("/Users", payload);

        res.Status.Should().Be(HttpStatusCode.BadRequest);
        res.ScimType.Should().Be("invalidValue");
        res.Body!["detail"]!.GetValue<string>().Should().Contain("Global User ID missing");
    }

    [Fact] // US-003-1.5 (RequireGlobalUserId=false)
    public async Task Missing_global_user_id_is_stored_when_not_required()
    {
        var host = ScimHost.Get(sql, "nogid", new() { ["Scim:RequireGlobalUserId"] = "false" });
        var scim = await host.ClientAsync();
        var payload = Scim.UserPayload(Scim.Name("flag"), "x");
        payload.Remove(Scim.SapExt);

        var res = await scim.Post("/Users", payload);

        res.Status.Should().Be(HttpStatusCode.Created);
        res.Body!.ContainsKey(Scim.SapExt).Should().BeFalse();
    }

    [Fact]
    public async Task Id_equals_global_user_id_when_configured()
    {
        var host = ScimHost.Get(sql, "gid-as-id", new() { ["Scim:UseGlobalUserIdAsId"] = "true" });
        var scim = await host.ClientAsync();
        var uuid = Guid.NewGuid().ToString();

        var res = await scim.CreateUser(userUuid: uuid);

        res.Id.Should().Be(uuid);
    }

    [Fact]
    public async Task Plain_json_content_type_is_accepted_and_malformed_json_is_400()
    {
        var scim = await Host.ClientAsync();

        var ok = await scim.SendAsync(HttpMethod.Post, "/Users", Scim.UserPayload(Scim.Name("json"), Guid.NewGuid().ToString()), "application/json");
        ok.Status.Should().Be(HttpStatusCode.Created);

        var bad = await scim.SendRawAsync(HttpMethod.Post, "/Users", "{ not json");
        var notObject = await scim.SendRawAsync(HttpMethod.Post, "/Users", "[1,2]");
        notObject.ScimType.Should().Be("invalidSyntax");
        bad.Status.Should().Be(HttpStatusCode.BadRequest);
        bad.ScimType.Should().Be("invalidSyntax");
    }

    [Fact]
    public async Task Missing_user_name_is_400()
    {
        var scim = await Host.ClientAsync();
        var payload = Scim.UserPayload("x", Guid.NewGuid().ToString());
        payload.Remove("userName");

        var res = await scim.Post("/Users", payload);

        (res.Status, res.ScimType).Should().Be((HttpStatusCode.BadRequest, "invalidValue"));
    }

    [Fact]
    public async Task Get_returns_404_for_unknown_and_malformed_ids()
    {
        var scim = await Host.ClientAsync();

        (await scim.Get($"/Users/{Guid.NewGuid()}")).Status.Should().Be(HttpStatusCode.NotFound);
        var bad = await scim.Get("/Users/not-a-guid");
        bad.Status.Should().Be(HttpStatusCode.NotFound);
        bad.Body!["schemas"]![0]!.GetValue<string>().Should().Contain("Error");
    }

    [Fact] // US-003-5 / FR-SCIM-11 / 002 policy
    public async Task Scim_requires_a_scim_scoped_bearer_token()
    {
        var scim = await Host.ClientAsync();
        var http = Host.Factory.CreateClient();
        var tech = await OAuthTestClient.GetTechTokenAsync(http);

        (await scim.SendAsync(HttpMethod.Get, "/Users", noAuth: true)).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await scim.SendAsync(HttpMethod.Get, "/Users", auth: new("Bearer", tech))).Status.Should().Be(HttpStatusCode.Forbidden);
        (await scim.SendAsync(HttpMethod.Get, "/Users", auth: new("Bearer", "garbage"))).Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- filter + paging (T003-07, T003-08) ---------------------------------------------------

    private async Task<(ScimClient Scim, string Prefix, ScimResponse Alice, ScimResponse Bob)> SeedAsync()
    {
        var scim = await Host.ClientAsync();
        var prefix = Scim.Name("flt");
        var alice = await scim.CreateUser(prefix + "-alice", customise: u =>
        {
            u["externalId"] = $"Ext-{prefix}-ALICE";
            u["emails"] = new JsonArray(
                new JsonObject { ["value"] = prefix + ".alice@work.example", ["type"] = "work", ["primary"] = true },
                new JsonObject { ["value"] = prefix + ".alice@home.example", ["type"] = "home" });
        });
        var bob = await scim.CreateUser(prefix + "-bob", customise: u =>
        {
            u["active"] = false;
            u["displayName"] = $"Bob The Builder {prefix}";
        });
        return (scim, prefix, alice, bob);
    }

    private static string Q(string filter) => "/Users?filter=" + Uri.EscapeDataString(filter);

    [Theory] // US-003-2.2 (T003-07 integration table)
    [InlineData("userName eq \"{p}-ALICE\"", 1)] // case-insensitive
    [InlineData("userName sw \"{p}\"", 2)]
    [InlineData("userName co \"-bo\" and userName sw \"{p}\"", 1)]
    [InlineData("userName ew \"-alice\" and userName sw \"{p}\"", 1)]
    [InlineData("userName ne \"{p}-alice\" and userName sw \"{p}\"", 1)]
    [InlineData("userName sw \"{p}\" and active eq false", 1)]
    [InlineData("userName sw \"{p}\" and active eq true", 1)]
    [InlineData("userName sw \"{p}\" and not (active eq true)", 1)]
    [InlineData("userName eq \"{p}-alice\" or userName eq \"{p}-bob\"", 2)]
    [InlineData("emails.value eq \"{p}.alice@work.example\"", 1)]
    [InlineData("emails.value eq \"{p}.alice@home.example\"", 1)] // non-primary address is searchable too
    [InlineData("emails.value co \"alice@\" and userName sw \"{p}\"", 1)]
    [InlineData("emails.value sw \"{p}.alice\"", 1)]
    [InlineData("emails.value ew \"@home.example\" and userName sw \"{p}\"", 1)]
    [InlineData("emails[type eq \"work\"].value eq \"{p}.alice@work.example\"", 1)]
    [InlineData("emails[type eq \"home\"].value eq \"{p}.alice@home.example\"", 1)]
    [InlineData("emails[type eq \"home\"].value eq \"{p}.alice@work.example\"", 0)] // type must match
    [InlineData("emails.value eq \"nobody@nowhere.example\"", 0)]
    [InlineData("externalId eq \"Ext-{p}-ALICE\"", 1)]
    [InlineData("externalId eq \"ext-{p}-alice\"", 0)] // externalId is case-sensitive
    [InlineData("externalId pr and userName sw \"{p}\"", 1)]
    [InlineData("displayName co \"Builder {p}\"", 1)]
    [InlineData("name.givenName eq \"given\" and userName sw \"{p}\"", 2)]
    [InlineData("userName eq \"x\\\"y\"", 0)] // escaped quote does not break the SQL
    [InlineData("userName eq \"'; DROP TABLE idm.ScimUser; --\"", 0)]
    [InlineData("userName co \"%\" and userName sw \"{p}\"", 0)] // LIKE wildcards are literals
    public async Task Filters_return_the_expected_users(string template, int expected)
    {
        var (scim, prefix, _, _) = await SeedAsync();

        var res = await scim.Get(Q(template.Replace("{p}", prefix)));

        res.Status.Should().Be(HttpStatusCode.OK, res.Body?.ToJsonString());
        res.Body!["totalResults"]!.GetValue<int>().Should().Be(expected);
        res.Body["Resources"]!.AsArray().Count.Should().Be(expected);
    }

    [Fact]
    public async Task Filters_by_id_global_user_id_and_last_modified()
    {
        var (scim, _, alice, _) = await SeedAsync();
        var uuid = alice.Body![Scim.SapExt]!["userUuid"]!.GetValue<string>();

        (await scim.Get(Q($"id eq \"{alice.Id}\""))).Body!["totalResults"]!.GetValue<int>().Should().Be(1);
        (await scim.Get(Q($"id eq \"{Guid.NewGuid()}\""))).Body!["totalResults"]!.GetValue<int>().Should().Be(0);
        (await scim.Get(Q($"id eq \"not-a-guid\""))).Body!["totalResults"]!.GetValue<int>().Should().Be(0);
        (await scim.Get(Q($"{Scim.SapExt}:userUuid eq \"{uuid.ToUpperInvariant()}\""))).Body!["totalResults"]!.GetValue<int>().Should().Be(1);
        (await scim.Get(Q($"{Scim.SapExt}:userUuid eq \"{uuid}\" and meta.lastModified gt \"2000-01-01T00:00:00Z\""))).Body!["totalResults"]!.GetValue<int>().Should().Be(1);
        (await scim.Get(Q($"{Scim.SapExt}:userUuid eq \"{uuid}\" and meta.lastModified gt \"2999-01-01T00:00:00Z\""))).Body!["totalResults"]!.GetValue<int>().Should().Be(0);
    }

    [Theory] // US-003-2.5
    [InlineData("userName")]
    [InlineData("userName eq")]
    [InlineData("password eq \"x\"")]
    [InlineData("userName gt \"a\"")]
    [InlineData("(userName eq \"x\"")]
    [InlineData("emails[type co \"w\"].value eq \"x\"")]
    public async Task Invalid_or_unsupported_filters_are_400_invalid_filter(string filter)
    {
        var scim = await Host.ClientAsync();

        var res = await scim.Get(Q(filter));

        (res.Status, res.ScimType).Should().Be((HttpStatusCode.BadRequest, "invalidFilter"));
    }

    [Fact] // US-003-2.3 / T003-08
    public async Task List_pages_with_start_index_and_count()
    {
        var scim = await Host.ClientAsync();
        var prefix = Scim.Name("pg");
        for (var i = 0; i < 25; i++) await scim.CreateUser($"{prefix}-{i:D2}");
        var filter = Uri.EscapeDataString($"userName sw \"{prefix}\"");

        var page1 = (await scim.Get($"/Users?filter={filter}&startIndex=1&count=10")).Body!;
        var page2 = (await scim.Get($"/Users?filter={filter}&startIndex=11&count=10")).Body!;
        var page3 = (await scim.Get($"/Users?filter={filter}&startIndex=21&count=10")).Body!;

        foreach (var page in new[] { page1, page2, page3 })
        {
            page["totalResults"]!.GetValue<int>().Should().Be(25);
            page["schemas"]![0]!.GetValue<string>().Should().Be("urn:ietf:params:scim:api:messages:2.0:ListResponse");
        }
        (page1["startIndex"]!.GetValue<int>(), page1["itemsPerPage"]!.GetValue<int>()).Should().Be((1, 10));
        (page2["startIndex"]!.GetValue<int>(), page2["itemsPerPage"]!.GetValue<int>()).Should().Be((11, 10));
        (page3["startIndex"]!.GetValue<int>(), page3["itemsPerPage"]!.GetValue<int>()).Should().Be((21, 5));

        var all = Scim.Ids(page1).Concat(Scim.Ids(page2)).Concat(Scim.Ids(page3)).ToList();
        all.Should().HaveCount(25).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Count_zero_returns_only_the_total_and_bad_paging_values_are_handled()
    {
        var scim = await Host.ClientAsync();
        await scim.CreateUser();

        var zero = (await scim.Get("/Users?count=0")).Body!;
        zero["Resources"]!.AsArray().Should().BeEmpty();
        zero["totalResults"]!.GetValue<int>().Should().BeGreaterThan(0);

        (await scim.Get("/Users?startIndex=-5&count=1")).Body!["startIndex"]!.GetValue<int>().Should().Be(1);
        var bad = await scim.Get("/Users?count=abc");
        (bad.Status, bad.ScimType).Should().Be((HttpStatusCode.BadRequest, "invalidValue"));
    }

    [Fact] // US-003-2.6
    public async Task Attributes_and_excluded_attributes_project_the_response()
    {
        var scim = await Host.ClientAsync();
        var user = await scim.CreateUser();

        var only = (await scim.Get($"/Users/{user.Id}?attributes=userName")).Body!;
        only.ContainsKey("userName").Should().BeTrue();
        only.ContainsKey("emails").Should().BeFalse();
        only.ContainsKey("id").Should().BeTrue();

        var without = (await scim.Get($"/Users/{user.Id}?excludedAttributes=emails,name")).Body!;
        without.ContainsKey("emails").Should().BeFalse();
        without.ContainsKey("name").Should().BeFalse();
        without.ContainsKey("userName").Should().BeTrue();
    }

    // ---- replace (T003-09) ---------------------------------------------------------------------

    [Fact] // US-003-3.1
    public async Task Put_replaces_mutable_attributes()
    {
        var scim = await Host.ClientAsync();
        var created = await scim.CreateUser();
        var uuid = created.Body![Scim.SapExt]!["userUuid"]!.GetValue<string>();
        var update = Scim.UserPayload(Scim.Name("renamed"), uuid);
        update["displayName"] = "Brand New";
        update["emails"] = new JsonArray(new JsonObject { ["value"] = "new@corp.example", ["primary"] = true });
        update.Remove("name");

        var res = await scim.Put($"/Users/{created.Id}", update);

        res.Status.Should().Be(HttpStatusCode.OK, res.Body?.ToJsonString());
        res.Body!["displayName"]!.GetValue<string>().Should().Be("Brand New");
        res.Body["userName"]!.GetValue<string>().Should().StartWith("renamed-");
        res.Body.ContainsKey("name").Should().BeFalse();
        res.Body["meta"]!["created"]!.GetValue<string>().Should().Be(created.Body["meta"]!["created"]!.GetValue<string>());
        (await scim.Get($"/Users/{created.Id}")).Body!["emails"]![0]!["value"]!.GetValue<string>().Should().Be("new@corp.example");
    }

    [Fact]
    public async Task Put_with_taken_user_name_is_409_and_unknown_user_is_404()
    {
        var scim = await Host.ClientAsync();
        var first = await scim.CreateUser();
        var second = await scim.CreateUser();

        var clash = Scim.UserPayload(first.Body!["userName"]!.GetValue<string>(), second.Body![Scim.SapExt]!["userUuid"]!.GetValue<string>());
        var res = await scim.Put($"/Users/{second.Id}", clash);
        (res.Status, res.ScimType).Should().Be((HttpStatusCode.Conflict, "uniqueness"));

        (await scim.Put($"/Users/{Guid.NewGuid()}", Scim.UserPayload("x", Guid.NewGuid().ToString()))).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact] // US-003-3.5
    public async Task Changing_the_global_user_id_is_rejected_once_tasks_reference_the_user_but_allowed_before()
    {
        var scim = await Host.ClientAsync();
        var created = await scim.CreateUser();
        var oldId = created.Body![Scim.SapExt]!["userUuid"]!.GetValue<string>();
        var name = created.Body["userName"]!.GetValue<string>();

        // not referenced yet -> allowed
        var newId = Guid.NewGuid().ToString();
        var allowed = await scim.Put($"/Users/{created.Id}", Scim.UserPayload(name, newId));
        allowed.Status.Should().Be(HttpStatusCode.OK);
        allowed.Body![Scim.SapExt]!["userUuid"]!.GetValue<string>().Should().Be(newId);

        // referenced -> mutability error, on both PUT and PATCH
        Host.TaskReferences.ReferencedUsers.Add(newId);
        var rejected = await scim.Put($"/Users/{created.Id}", Scim.UserPayload(name, Guid.NewGuid().ToString()));
        (rejected.Status, rejected.ScimType).Should().Be((HttpStatusCode.BadRequest, "mutability"));

        var patched = await scim.Patch($"/Users/{created.Id}", Scim.PatchBody(Scim.Op("replace", $"{Scim.SapExt}:userUuid", Guid.NewGuid().ToString())));
        (patched.Status, patched.ScimType).Should().Be((HttpStatusCode.BadRequest, "mutability"));

        // re-sending the same id is fine
        (await scim.Put($"/Users/{created.Id}", Scim.UserPayload(name, newId))).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Put_without_any_global_id_source_keeps_the_existing_one()
    {
        var scim = await Host.ClientAsync();
        var created = await scim.CreateUser();
        var uuid = created.Body![Scim.SapExt]!["userUuid"]!.GetValue<string>();
        var update = Scim.UserPayload(created.Body["userName"]!.GetValue<string>(), "x");
        update.Remove(Scim.SapExt);

        var res = await scim.Put($"/Users/{created.Id}", update);

        res.Status.Should().Be(HttpStatusCode.OK);
        res.Body![Scim.SapExt]!["userUuid"]!.GetValue<string>().Should().Be(uuid);
    }

    // ---- patch (T003-10) -----------------------------------------------------------------------

    [Fact] // US-003-3.2 / 3.3: deactivation
    public async Task Patch_active_false_deactivates_and_blocks_user_tokens()
    {
        var scim = await Host.ClientAsync();
        var uuid = Guid.NewGuid().ToString();
        var user = await scim.CreateUser(userUuid: uuid);
        var http = Host.Factory.CreateClient();
        var assertion = () => ScimHost.Oidc.Mint(new JsonObject { ["user_uuid"] = uuid });
        var exchange = () => OAuthTestClient.PostTokenAsync(http,
            new() { ["grant_type"] = Tcp.Api.Auth.GrantTypes.JwtBearer, ["assertion"] = assertion() }, "tc-pp", TcpFactory.PpSecret);

        (await exchange()).StatusCode.Should().Be(HttpStatusCode.OK); // end-to-end with the real SCIM store

        var res = await scim.Patch($"/Users/{user.Id}", Scim.PatchBody(Scim.Op("replace", "active", false)));
        res.Status.Should().Be(HttpStatusCode.OK);
        res.Body!["active"]!.GetValue<bool>().Should().BeFalse();

        var blocked = await exchange();
        blocked.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("unknown user");

        await scim.Patch($"/Users/{user.Id}", Scim.PatchBody(Scim.Op("replace", "active", true)));
        (await exchange()).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Patch_variants_from_common_identity_providers()
    {
        var scim = await Host.ClientAsync();
        var user = await scim.CreateUser();
        var path = $"/Users/{user.Id}";

        // Azure style: no path, boolean as string, capitalised op
        var azure = await scim.Patch(path, Scim.PatchBody(Scim.Op("Replace", null, new JsonObject { ["active"] = "False", ["displayName"] = "Azure Name" })));
        azure.Body!["active"]!.GetValue<bool>().Should().BeFalse();
        azure.Body["displayName"]!.GetValue<string>().Should().Be("Azure Name");

        // nested + typed e-mail replacement
        var nested = await scim.Patch(path, Scim.PatchBody(
            Scim.Op("replace", "name.givenName", "Changed"),
            Scim.Op("replace", "emails[type eq \"work\"].value", "patched@corp.example"),
            Scim.Op("add", "emails", new JsonArray(new JsonObject { ["value"] = "extra@corp.example", ["type"] = "other" }))));
        nested.Body!["name"]!["givenName"]!.GetValue<string>().Should().Be("Changed");
        nested.Body["emails"]!.AsArray().Select(e => e!["value"]!.GetValue<string>())
            .Should().Equal("patched@corp.example", "extra@corp.example");

        // remove
        var removed = await scim.Patch(path, Scim.PatchBody(Scim.Op("remove", "displayName"), Scim.Op("remove", "emails[type eq \"other\"]")));
        removed.Body!.ContainsKey("displayName").Should().BeFalse();
        removed.Body["emails"]!.AsArray().Should().ContainSingle();
    }

    [Fact]
    public async Task Invalid_patches_are_400_with_scim_types_and_unknown_user_is_404()
    {
        var scim = await Host.ClientAsync();
        var user = await scim.CreateUser();

        var noTarget = await scim.Patch($"/Users/{user.Id}", Scim.PatchBody(Scim.Op("remove")));
        (noTarget.Status, noTarget.ScimType).Should().Be((HttpStatusCode.BadRequest, "noTarget"));

        var badOp = await scim.Patch($"/Users/{user.Id}", Scim.PatchBody(Scim.Op("explode", "x", 1)));
        (badOp.Status, badOp.ScimType).Should().Be((HttpStatusCode.BadRequest, "invalidSyntax"));

        var badValue = await scim.Patch($"/Users/{user.Id}", Scim.PatchBody(Scim.Op("replace", "active", "perhaps")));
        (badValue.Status, badValue.ScimType).Should().Be((HttpStatusCode.BadRequest, "invalidValue"));

        // a rejected patch changes nothing
        (await scim.Get($"/Users/{user.Id}")).Body!["active"]!.GetValue<bool>().Should().BeTrue();

        (await scim.Patch($"/Users/{Guid.NewGuid()}", Scim.PatchBody(Scim.Op("replace", "active", false)))).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact] // Scim:PatchReturnsNoContent
    public async Task Patch_can_return_204()
    {
        var host = ScimHost.Get(sql, "patch204", new() { ["Scim:PatchReturnsNoContent"] = "true" });
        var scim = await host.ClientAsync();
        var user = await scim.CreateUser();

        var res = await scim.Patch($"/Users/{user.Id}", Scim.PatchBody(Scim.Op("replace", "active", false)));

        res.Status.Should().Be(HttpStatusCode.NoContent);
        (await scim.Get($"/Users/{user.Id}")).Body!["active"]!.GetValue<bool>().Should().BeFalse();
    }

    // ---- delete / GDPR (T003-11) ---------------------------------------------------------------

    [Fact] // US-003-3.4
    public async Task Delete_is_204_anonymises_and_frees_user_name_and_global_id()
    {
        var scim = await Host.ClientAsync();
        var uuid = Guid.NewGuid().ToString();
        var name = Scim.Name("gdpr");
        var user = await scim.CreateUser(name, uuid);
        var group = await scim.CreateGroup(null, user.Id!);
        var before = DateTime.UtcNow.AddSeconds(-1);

        (await scim.Delete($"/Users/{user.Id}")).Status.Should().Be(HttpStatusCode.NoContent);

        (await scim.Get($"/Users/{user.Id}")).Status.Should().Be(HttpStatusCode.NotFound);
        (await scim.Delete($"/Users/{user.Id}")).Status.Should().Be(HttpStatusCode.NotFound);
        (await scim.Get(Q($"userName eq \"{name}\""))).Body!["totalResults"]!.GetValue<int>().Should().Be(0);

        // the task side was told, inside the same transaction, with a fresh timestamp
        Host.TaskReferences.Anonymised.Should().Contain(a => a.GlobalUserId == uuid && a.At > before);

        // membership is gone but the group stays
        var groupAfter = await scim.Get($"/Groups/{group.Id}");
        groupAfter.Status.Should().Be(HttpStatusCode.OK);
        groupAfter.Body!["members"]!.AsArray().Should().BeEmpty();

        // personal data is gone from the row
        var row = Host.Resolve(sp => sp.GetRequiredService<TcpDbContext>().ScimUsers.AsNoTracking().Single(u => u.Id == Guid.Parse(user.Id!)));
        row.IsDeleted.Should().BeTrue();
        (row.GlobalUserId, row.ExternalId, row.DisplayName, row.PrimaryEmail, row.GivenName, row.FamilyName).Should().Be((null, null, null, null, null, null));
        row.UserName.Should().StartWith("deleted-").And.NotContain(name);
        row.RawJson.Should().Be("{}");
        row.EmailsJson.Should().Be("[]");

        // IPS can re-provision the same person later
        var again = await scim.Post("/Users", Scim.UserPayload(name, uuid));
        again.Status.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Delete_of_unknown_user_is_404()
    {
        var scim = await Host.ClientAsync();
        (await scim.Delete($"/Users/{Guid.NewGuid()}")).Status.Should().Be(HttpStatusCode.NotFound);
        (await scim.Delete("/Users/garbage")).Status.Should().Be(HttpStatusCode.NotFound);
    }
}
