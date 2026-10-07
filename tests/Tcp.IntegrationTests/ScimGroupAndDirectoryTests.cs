using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tcp.Domain.Identity;
using Tcp.Infrastructure.Persistence;
using Tcp.TestSupport;

namespace Tcp.IntegrationTests;

[Collection(SqlCollection.Name)]
public class ScimGroupTests(SqlServerFixture sql)
{
    private ScimHost Host => ScimHost.Get(sql, "main");

    private static string[] MemberIds(ScimResponse r) =>
        r.Body!["members"]!.AsArray().Select(m => m!["value"]!.GetValue<string>()).ToArray();

    [Fact] // US-003-4.1
    public async Task Post_creates_group_with_members()
    {
        var scim = await Host.ClientAsync();
        var u1 = await scim.CreateUser();
        var u2 = await scim.CreateUser();

        var res = await scim.CreateGroup(null, u1.Id!, u2.Id!);

        res.Body!["meta"]!["resourceType"]!.GetValue<string>().Should().Be("Group");
        MemberIds(res).Should().BeEquivalentTo(u1.Id!, u2.Id!);
        res.Body["members"]![0]!["$ref"]!.GetValue<string>().Should().Contain("/scim/v2/Users/");
        res.Raw.Headers.Location!.ToString().Should().EndWith($"/scim/v2/Groups/{res.Id}");
    }

    [Fact] // US-003-4.5
    public async Task Unknown_member_is_400_invalid_value()
    {
        var scim = await Host.ClientAsync();

        var res = await scim.Post("/Groups", Scim.GroupPayload(Scim.Name("bad"), Guid.NewGuid().ToString()));

        (res.Status, res.ScimType).Should().Be((HttpStatusCode.BadRequest, "invalidValue"));
        res.Body!["detail"]!.GetValue<string>().Should().Contain("Unknown member");
    }

    [Fact] // FR-SCIM-08
    public async Task Duplicate_display_name_is_409_case_insensitively()
    {
        var scim = await Host.ClientAsync();
        var name = Scim.Name("dupg");
        await scim.CreateGroup(name);

        var res = await scim.Post("/Groups", Scim.GroupPayload(name.ToUpperInvariant()));

        (res.Status, res.ScimType).Should().Be((HttpStatusCode.Conflict, "uniqueness"));
    }

    [Fact] // US-003-4.2
    public async Task Patch_adds_removes_and_replaces_members_and_display_name()
    {
        var scim = await Host.ClientAsync();
        var u1 = await scim.CreateUser();
        var u2 = await scim.CreateUser();
        var u3 = await scim.CreateUser();
        var group = await scim.CreateGroup(null, u1.Id!);
        var path = $"/Groups/{group.Id}";

        var add = await scim.Patch(path, Scim.PatchBody(Scim.Op("add", "members",
            new JsonArray(new JsonObject { ["value"] = u2.Id }, new JsonObject { ["value"] = u3.Id }))));
        add.Status.Should().Be(HttpStatusCode.OK, add.Body?.ToJsonString());
        MemberIds(add).Should().BeEquivalentTo(u1.Id!, u2.Id!, u3.Id!);

        var removeOne = await scim.Patch(path, Scim.PatchBody(Scim.Op("remove", $"members[value eq \"{u1.Id}\"]")));
        MemberIds(removeOne).Should().BeEquivalentTo(u2.Id!, u3.Id!);

        var removeList = await scim.Patch(path, Scim.PatchBody(Scim.Op("remove", "members", new JsonArray(new JsonObject { ["value"] = u3.Id }))));
        MemberIds(removeList).Should().BeEquivalentTo(u2.Id!);

        var rename = await scim.Patch(path, Scim.PatchBody(Scim.Op("replace", "displayName", "Renamed Group " + Guid.NewGuid().ToString("N")[..6])));
        rename.Body!["displayName"]!.GetValue<string>().Should().StartWith("Renamed Group");
        MemberIds(rename).Should().BeEquivalentTo(u2.Id!);

        var removeAll = await scim.Patch(path, Scim.PatchBody(Scim.Op("remove", "members")));
        MemberIds(removeAll).Should().BeEmpty();
    }

    [Fact]
    public async Task Patch_adding_an_unknown_member_is_rejected_and_changes_nothing()
    {
        var scim = await Host.ClientAsync();
        var u1 = await scim.CreateUser();
        var group = await scim.CreateGroup(null, u1.Id!);

        var res = await scim.Patch($"/Groups/{group.Id}", Scim.PatchBody(Scim.Op("add", "members", new JsonArray(new JsonObject { ["value"] = Guid.NewGuid().ToString() }))));

        (res.Status, res.ScimType).Should().Be((HttpStatusCode.BadRequest, "invalidValue"));
        MemberIds(await scim.Get($"/Groups/{group.Id}")).Should().Equal(u1.Id!);
    }

    [Fact]
    public async Task Put_replaces_members_and_name()
    {
        var scim = await Host.ClientAsync();
        var u1 = await scim.CreateUser();
        var u2 = await scim.CreateUser();
        var group = await scim.CreateGroup(null, u1.Id!);
        var newName = Scim.Name("put");

        var res = await scim.Put($"/Groups/{group.Id}", Scim.GroupPayload(newName, u2.Id!));

        res.Status.Should().Be(HttpStatusCode.OK);
        res.Body!["displayName"]!.GetValue<string>().Should().Be(newName);
        MemberIds(res).Should().Equal(u2.Id!);
    }

    [Fact] // US-003-4.3 / 2.6
    public async Task Filter_by_display_name_and_membership_and_excluded_members()
    {
        var scim = await Host.ClientAsync();
        var user = await scim.CreateUser();
        var name = Scim.Name("flg");
        await scim.CreateGroup(name, user.Id!);
        await scim.CreateGroup(Scim.Name("other"));

        var byName = await scim.Get("/Groups?filter=" + Uri.EscapeDataString($"displayName eq \"{name.ToUpperInvariant()}\""));
        byName.Body!["totalResults"]!.GetValue<int>().Should().Be(1);
        byName.Body["Resources"]![0]!["members"]!.AsArray().Should().ContainSingle();

        var excluded = await scim.Get("/Groups?excludedAttributes=members&filter=" + Uri.EscapeDataString($"displayName eq \"{name}\""));
        excluded.Body!["Resources"]![0]!.AsObject().ContainsKey("members").Should().BeFalse();

        var byMember = await scim.Get("/Groups?filter=" + Uri.EscapeDataString($"members.value eq \"{user.Id}\""));
        byMember.Body!["Resources"]!.AsArray().Select(g => g!["displayName"]!.GetValue<string>()).Should().Contain(name);

        var single = await scim.Get($"/Groups/{(await scim.Get("/Groups?filter=" + Uri.EscapeDataString($"displayName eq \"{name}\""))).Body!["Resources"]![0]!["id"]!.GetValue<string>()}?excludedAttributes=members");
        single.Body!.ContainsKey("members").Should().BeFalse();

        var bad = await scim.Get("/Groups?filter=" + Uri.EscapeDataString("userName eq \"x\""));
        (bad.Status, bad.ScimType).Should().Be((HttpStatusCode.BadRequest, "invalidFilter"));
    }

    [Fact] // US-003-4.4
    public async Task Delete_removes_the_group_and_notifies_the_task_side()
    {
        var scim = await Host.ClientAsync();
        var user = await scim.CreateUser();
        var name = Scim.Name("delg");
        var group = await scim.CreateGroup(name, user.Id!);
        var before = DateTime.UtcNow.AddSeconds(-1);

        (await scim.Delete($"/Groups/{group.Id}")).Status.Should().Be(HttpStatusCode.NoContent);

        (await scim.Get($"/Groups/{group.Id}")).Status.Should().Be(HttpStatusCode.NotFound);
        (await scim.Get($"/Users/{user.Id}")).Status.Should().Be(HttpStatusCode.OK); // user survives
        Host.TaskReferences.RemovedGroups.Should().Contain(g => g.Group == name && g.At > before);
        (await scim.Delete($"/Groups/{group.Id}")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Accepts_global_user_id_as_member_value_when_configured()
    {
        var host = ScimHost.Get(sql, "gid-member", new() { ["Scim:AcceptGlobalUserIdAsMemberValue"] = "true" });
        var scim = await host.ClientAsync();
        var uuid = Guid.NewGuid().ToString();
        var user = await scim.CreateUser(userUuid: uuid);

        var res = await scim.CreateGroup(null, uuid);

        MemberIds(res).Should().Equal(user.Id!);
    }
}

[Collection(SqlCollection.Name)]
public class UserDirectoryTests(SqlServerFixture sql)
{
    private ScimHost Host => ScimHost.Get(sql, "main");

    private async Task<T> WithDirectory<T>(Func<IUserDirectory, Task<T>> action)
    {
        using var scope = Host.Factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IUserDirectory>());
    }

    [Fact] // T003-13
    public async Task Finds_users_by_global_user_id_in_any_notation_and_hides_deleted_users()
    {
        var scim = await Host.ClientAsync();
        var uuid = Guid.NewGuid();
        var user = await scim.CreateUser(userUuid: uuid.ToString());

        var found = await WithDirectory(d => d.FindByGlobalUserIdAsync(uuid.ToString("N").ToUpperInvariant()));
        found.Should().NotBeNull();
        found!.GlobalUserId.Should().Be(uuid.ToString("D"));
        found.Active.Should().BeTrue();
        found.UserName.Should().Be(user.Body!["userName"]!.GetValue<string>());

        (await WithDirectory(d => d.FindByGlobalUserIdAsync("garbage"))).Should().BeNull();
        (await WithDirectory(d => d.FindByGlobalUserIdAsync(Guid.NewGuid().ToString()))).Should().BeNull();

        await scim.Delete($"/Users/{user.Id}");
        (await WithDirectory(d => d.FindByGlobalUserIdAsync(uuid.ToString()))).Should().BeNull();
    }

    [Fact] // T003-13
    public async Task Email_lookup_requires_a_unique_match_and_is_case_insensitive()
    {
        var scim = await Host.ClientAsync();
        var email = $"{Scim.Name("mail")}@dir.example";
        await scim.CreateUser(customise: u => u["emails"] = new JsonArray(new JsonObject { ["value"] = email, ["primary"] = true }));

        (await WithDirectory(d => d.FindUniqueByEmailAsync(email.ToUpperInvariant()))).Should().NotBeNull();

        await scim.CreateUser(customise: u => u["emails"] = new JsonArray(new JsonObject { ["value"] = email, ["primary"] = true }));
        (await WithDirectory(d => d.FindUniqueByEmailAsync(email))).Should().BeNull(); // ambiguous
        (await WithDirectory(d => d.FindUniqueByEmailAsync("nobody@dir.example"))).Should().BeNull();
    }

    [Fact] // T003-13
    public async Task Group_membership_lookup_is_case_insensitive_and_any_of()
    {
        var scim = await Host.ClientAsync();
        var uuid = Guid.NewGuid().ToString();
        var user = await scim.CreateUser(userUuid: uuid);
        var outsider = Guid.NewGuid().ToString();
        await scim.CreateUser(userUuid: outsider);
        var groupName = Scim.Name("TC_APPROVERS");
        var group = await scim.CreateGroup(groupName, user.Id!);

        (await WithDirectory(d => d.IsMemberOfAnyGroupAsync(uuid, [groupName.ToLowerInvariant()]))).Should().BeTrue();
        (await WithDirectory(d => d.IsMemberOfAnyGroupAsync(uuid, ["nope", groupName]))).Should().BeTrue();
        (await WithDirectory(d => d.IsMemberOfAnyGroupAsync(uuid, ["nope"]))).Should().BeFalse();
        (await WithDirectory(d => d.IsMemberOfAnyGroupAsync(outsider, [groupName]))).Should().BeFalse();
        (await WithDirectory(d => d.IsMemberOfAnyGroupAsync(uuid, []))).Should().BeFalse();

        await scim.Patch($"/Groups/{group.Id}", Scim.PatchBody(Scim.Op("remove", "members")));
        (await WithDirectory(d => d.IsMemberOfAnyGroupAsync(uuid, [groupName]))).Should().BeFalse();
    }

    [Fact] // T003-13
    public async Task List_active_excludes_inactive_and_deleted_users()
    {
        var scim = await Host.ClientAsync();
        var active = Guid.NewGuid().ToString();
        var inactive = Guid.NewGuid().ToString();
        var deleted = Guid.NewGuid().ToString();
        await scim.CreateUser(userUuid: active);
        await scim.CreateUser(userUuid: inactive, customise: u => u["active"] = false);
        var del = await scim.CreateUser(userUuid: deleted);
        await scim.Delete($"/Users/{del.Id}");

        var ids = (await WithDirectory(d => d.ListActiveAsync())).Select(u => u.GlobalUserId).ToList();

        ids.Should().Contain(active).And.NotContain(inactive).And.NotContain(deleted);
    }
}

[Collection(SqlCollection.Name)]
public class ScimAuditTests(SqlServerFixture sql)
{
    private ScimHost Host => ScimHost.Get(sql, "main");

    [Fact] // T003-14, FR-SCIM-10
    public async Task Every_write_is_audited_with_the_client_id()
    {
        var scim = await Host.ClientAsync();
        var name = Scim.Name("aud");
        var user = await scim.CreateUser(name);
        await scim.Put($"/Users/{user.Id}", Scim.UserPayload(name, user.Body![Scim.SapExt]!["userUuid"]!.GetValue<string>()));
        await scim.Patch($"/Users/{user.Id}", Scim.PatchBody(Scim.Op("replace", "active", false)));
        var group = await scim.CreateGroup(null, user.Id!);
        await scim.Delete($"/Groups/{group.Id}");
        await scim.Delete($"/Users/{user.Id}");
        await scim.Get($"/Users/{user.Id}"); // reads are not audited

        var userId = Guid.Parse(user.Id!);
        var rows = Host.Resolve(sp => sp.GetRequiredService<TcpDbContext>().ScimAudit.AsNoTracking()
            .Where(a => a.ResourceId == userId || a.ResourceId == Guid.Parse(group.Id!)).OrderBy(a => a.Id).ToList());

        rows.Select(r => (r.ResourceType, r.Operation)).Should().Equal(
            ("User", "Create"), ("User", "Replace"), ("User", "Patch"), ("Group", "Create"), ("Group", "Delete"), ("User", "Delete"));
        rows.Should().OnlyContain(r => r.ClientId == "ips-scim");
        rows.Should().OnlyContain(r => r.At > DateTime.UtcNow.AddMinutes(-5));
        rows[0].Summary.Should().Contain(name);
        rows[^1].Summary.Should().Be("gdpr-erase"); // no personal data in the audit of an erasure
    }

    [Fact]
    public async Task Failed_writes_are_not_audited()
    {
        var scim = await Host.ClientAsync();
        var name = Scim.Name("noaud");
        await scim.CreateUser(name);
        var count = () => Host.Resolve(sp => sp.GetRequiredService<TcpDbContext>().ScimAudit.Count(a => a.Summary.Contains(name)));
        var before = count();

        (await scim.Post("/Users", Scim.UserPayload(name, Guid.NewGuid().ToString()))).Status.Should().Be(HttpStatusCode.Conflict);

        count().Should().Be(before);
    }
}

[Collection(SqlCollection.Name)]
public class ScimAuthAndDiscoveryTests(SqlServerFixture sql)
{
    private ScimHost Main => ScimHost.Get(sql, "main");

    [Fact] // T003-15
    public async Task Basic_auth_is_off_by_default()
    {
        var scim = await Main.ClientAsync();

        var res = await scim.SendAsync(HttpMethod.Get, "/Users", auth: Tcp.TestSupport.OAuthTestClient.Basic("ips-scim", TcpFactory.ScimSecret));

        res.Status.Should().Be(HttpStatusCode.Unauthorized);
        (await scim.Get("/ServiceProviderConfig")).Body!["authenticationSchemes"]!.AsArray()
            .Select(s => s!["type"]!.GetValue<string>()).Should().Equal("oauthbearertoken");
    }

    [Fact] // T003-15
    public async Task Basic_auth_works_for_scim_clients_when_enabled()
    {
        var host = ScimHost.Get(sql, "basic", new() { ["Scim:AllowBasic"] = "true" });
        var scim = await host.ClientAsync();
        var basic = (string user, string pw) => scim.SendAsync(HttpMethod.Get, "/Users?count=1", auth: Tcp.TestSupport.OAuthTestClient.Basic(user, pw));

        (await basic("ips-scim", TcpFactory.ScimSecret)).Status.Should().Be(HttpStatusCode.OK);
        var wrong = await basic("ips-scim", "wrong");
        wrong.Status.Should().Be(HttpStatusCode.Unauthorized);
        wrong.Raw.Headers.WwwAuthenticate.ToString().Should().Contain("Basic");
        (await basic("tc-tech", TcpFactory.TechSecret)).Status.Should().Be(HttpStatusCode.Unauthorized); // no scim scope
        (await basic("nobody", "x")).Status.Should().Be(HttpStatusCode.Unauthorized);

        // bearer still works alongside
        (await scim.Get("/Users?count=1")).Status.Should().Be(HttpStatusCode.OK);
        (await scim.Get("/ServiceProviderConfig")).Body!["authenticationSchemes"]!.AsArray()
            .Select(s => s!["type"]!.GetValue<string>()).Should().Equal("oauthbearertoken", "httpbasic");

        // writes through Basic are audited with the client id
        var created = await scim.SendAsync(HttpMethod.Post, "/Users", Scim.UserPayload(Scim.Name("bas"), Guid.NewGuid().ToString()),
            auth: Tcp.TestSupport.OAuthTestClient.Basic("ips-scim", TcpFactory.ScimSecret));
        created.Status.Should().Be(HttpStatusCode.Created);
    }

    [Fact] // US-003-5
    public async Task Discovery_documents_declare_the_supported_capabilities()
    {
        var scim = await Main.ClientAsync();

        var config = (await scim.Get("/ServiceProviderConfig")).Body!;
        config["patch"]!["supported"]!.GetValue<bool>().Should().BeTrue();
        config["bulk"]!["supported"]!.GetValue<bool>().Should().BeFalse();
        config["filter"]!["supported"]!.GetValue<bool>().Should().BeTrue();
        config["filter"]!["maxResults"]!.GetValue<int>().Should().Be(1000);
        config["changePassword"]!["supported"]!.GetValue<bool>().Should().BeFalse();
        config["sort"]!["supported"]!.GetValue<bool>().Should().BeFalse();
        config["etag"]!["supported"]!.GetValue<bool>().Should().BeFalse();

        var types = (await scim.Get("/ResourceTypes")).Body!;
        types["Resources"]!.AsArray().Select(r => r!["name"]!.GetValue<string>()).Should().BeEquivalentTo("User", "Group");
        types["Resources"]![0]!["schemaExtensions"]![0]!["schema"]!.GetValue<string>().Should().Be(Scim.SapExt);

        var schemas = (await scim.Get("/Schemas")).Body!;
        schemas["Resources"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()).Should().Contain(Scim.SapExt);
    }
}
