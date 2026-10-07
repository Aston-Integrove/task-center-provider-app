using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Tcp.Domain.Tasks;
using Tcp.TestSupport;

namespace Tcp.IntegrationTests;

[Collection(SqlCollection.Name)]
public class AdminTaskCreateTests(SqlServerFixture sql)
{
    private SpiHost Host => SpiHost.Get(sql, "admin");

    private static string UrnOf(AdminResponse r) => r.Body!["urn"]!.GetValue<string>();

    [Fact] // US-005-1.1, 1.6
    public async Task Creating_a_task_returns_201_with_the_spi_representation_and_it_is_pullable()
    {
        var admin = Host.Admin();
        var alice = await Host.AddUserAsync(email: "alice.admin@corp.example");
        var bob = await Host.AddUserAsync(groups: "TC_ADMIN_GROUP");
        var request = AdminClient.NewTask([alice, "ALICE.ADMIN@corp.example"], ["tc_admin_group"], subject: "Approve PR 4711 - Laptop");
        request["subject"]!["de-DE"] = "BANF 4711 genehmigen - Laptop";
        request["priority"] = "HIGH";
        request["dueAt"] = "2030-01-15T10:00:00.000Z";
        request["customAttributes"] = new JsonObject { ["amount"] = "1234.50", ["currency"] = "ZAR", ["neededBy"] = "2030-02-01" };
        request["description"] = new JsonObject { ["en-US"] = new JsonObject { ["contentType"] = "text/html", ["body"] = "<p>Please approve</p>" } };
        var before = DateTime.UtcNow.AddSeconds(-2);

        var response = await admin.Post("/tasks", request);

        response.Status.Should().Be(HttpStatusCode.Created, response.RawText);
        response.Raw.Headers.Location!.ToString().Should().Contain("/admin/api/tasks/");
        var json = response.Body!.AsObject();
        json["status"]!.GetValue<string>().Should().Be("READY");
        json["priority"]!.GetValue<string>().Should().Be("HIGH");
        json["createdAt"]!.GetValue<string>().Should().Be(json["modifiedAt"]!.GetValue<string>());
        DateTime.Parse(json["createdAt"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal).Should().BeAfter(before);
        json.ContainsKey("createdBy").Should().BeFalse(); // created by the system
        json["processor"].Should().BeNull();
        json["dueAt"]!.GetValue<string>().Should().Be("2030-01-15T10:00:00.000Z");
        json["recipientUsers"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal(alice); // e-mail resolved, deduplicated
        json["recipientGroups"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("TC_ADMIN_GROUP"); // canonical case
        json["customAttributes"]!.AsArray().Select(a => $"{a!["code"]}={a["value"]}").Should().Equal("amount=1234.5", "currency=ZAR", "neededBy=2030-02-01");
        json["subject"]!.AsArray().Select(s => s!["languageCode"]!.GetValue<string>()).Should().Equal("en-US", "de-DE");
        json["localId"]!.GetValue<string>().Should().MatchRegex(@"^PR_APPROVAL-\d{8}-\d{6}$");
        json["urn"]!.GetValue<string>().Should().Be($"urn:sap.odm.bpm.task:integrove:tcproto:dev:{json["localId"]!.GetValue<string>()}");

        // what Task Center would get
        var tech = await Host.TechAsync();
        var pulled = await tech.Get($"/tasks/{AdminClient.Seg(UrnOf(response))}?languages=en-US");
        pulled.Body!["subject"]![0]!["text"]!.GetValue<string>().Should().Be("Approve PR 4711 - Laptop");

        // the group member can read the (plain text) description over the SPI
        var description = await (await Host.UserAsync(bob)).Get($"/tasks/{AdminClient.Seg(UrnOf(response))}/description");
        description.Status.Should().Be(HttpStatusCode.OK);
        description.RawText.Should().Be("Please approve");
    }

    [Fact] // US-005-1.2
    public async Task Unknown_inactive_users_and_unknown_groups_are_listed_in_a_400()
    {
        var admin = Host.Admin();
        var active = await Host.AddUserAsync();
        var inactive = await Host.AddUserAsync(active: false, email: "sleeping@corp.example");
        var unknown = Guid.NewGuid().ToString();

        var response = await admin.Post("/tasks", AdminClient.NewTask([active, inactive, unknown, "nobody@corp.example", "plain-name"], ["NO_SUCH_GROUP"]));

        response.Status.Should().Be(HttpStatusCode.BadRequest);
        response.Raw.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var messages = string.Join(" | ", response.ErrorMessages);
        messages.Should().Contain(inactive).And.Contain(unknown).And.Contain("nobody@corp.example").And.Contain("plain-name").And.Contain("NO_SUCH_GROUP");
        messages.Should().Contain("inactive");
        response.ErrorFields.Should().Contain(["recipients.users", "recipients.groups"]);
    }

    [Fact] // US-005-1.3
    public async Task Duplicate_subjects_get_the_business_number_appended()
    {
        var admin = Host.Admin();
        var user = await Host.AddUserAsync();
        var subject = "Approve PR 9999 - Same subject " + Guid.NewGuid().ToString("N")[..6];

        var first = await admin.Post("/tasks", AdminClient.NewTask([user], subject: subject));
        var second = await admin.Post("/tasks", AdminClient.NewTask([user], subject: subject));

        first.Body!["subject"]![0]!["text"]!.GetValue<string>().Should().Be(subject);
        var secondLocalId = second.Body!["localId"]!.GetValue<string>();
        second.Body["subject"]![0]!["text"]!.GetValue<string>().Should().Be($"{subject} ({secondLocalId})");
        secondLocalId.Should().NotBe(first.Body["localId"]!.GetValue<string>());
    }

    [Fact] // FR-SPI-02: local ids count up per definition and day
    public async Task Local_ids_are_sequential_per_definition()
    {
        var host = SpiHost.Isolated(sql);
        var admin = host.Admin();
        var user = await host.AddUserAsync();

        var ids = new List<string>();
        for (var i = 0; i < 3; i++) ids.Add((await admin.Post("/tasks", AdminClient.NewTask([user]))).Body!["localId"]!.GetValue<string>());
        var leave = (await admin.Post("/tasks", AdminClient.NewTask([user], definition: "LEAVE_APPROVAL"))).Body!["localId"]!.GetValue<string>();

        ids.Select(i => i[^6..]).Should().Equal("000001", "000002", "000003");
        leave.Should().StartWith("LEAVE_APPROVAL-").And.EndWith("-000001");
    }

    [Fact] // US-005-1.4
    public async Task Invalid_attribute_values_and_other_input_errors_name_the_offending_fields()
    {
        var admin = Host.Admin();
        var user = await Host.AddUserAsync();
        var request = AdminClient.NewTask([user]);
        request["customAttributes"] = new JsonObject { ["amount"] = "lots", ["neededBy"] = "31/10/2030", ["nonsense"] = "x", ["currency"] = "ZAR" };
        request["priority"] = "URGENT";

        var response = await admin.Post("/tasks", request);

        response.Status.Should().Be(HttpStatusCode.BadRequest);
        response.ErrorFields.Should().BeEquivalentTo("customAttributes.amount", "customAttributes.neededBy", "customAttributes.nonsense", "priority");
    }

    [Theory]
    [InlineData("""{"recipients":{"users":["x"]},"subject":{"en-US":"s"}}""", "definitionLocalId")]
    [InlineData("""{"definitionLocalId":"NOPE","recipients":{"groups":["g"]},"subject":{"en-US":"s"}}""", "definitionLocalId")]
    [InlineData("""{"definitionLocalId":"PR_APPROVAL","recipients":{"groups":["g"]}}""", "subject")]
    [InlineData("""{"definitionLocalId":"PR_APPROVAL","recipients":{"groups":["g"]},"subject":{"english":"s"}}""", "subject.english")]
    [InlineData("""{"definitionLocalId":"PR_APPROVAL","recipients":{"groups":["g"]},"subject":{"en-US":""}}""", "subject.en-US")]
    [InlineData("""{"definitionLocalId":"PR_APPROVAL","subject":{"en-US":"s"}}""", "recipients")]
    [InlineData("""{"definitionLocalId":"PR_APPROVAL","recipients":{},"subject":{"en-US":"s"}}""", "recipients")]
    public async Task Required_fields_are_enforced(string json, string expectedField)
    {
        var response = await Host.Admin().Post("/tasks", JsonNode.Parse(json));

        response.Status.Should().Be(HttpStatusCode.BadRequest);
        response.ErrorFields.Should().Contain(expectedField);
    }

    [Fact] // US-005-1.5, T005-02 in context
    public async Task Descriptions_are_sanitised_before_they_are_stored()
    {
        var admin = Host.Admin();
        var user = await Host.AddUserAsync();
        var request = AdminClient.NewTask([user]);
        request["description"] = new JsonObject
        {
            ["en-US"] = new JsonObject
            {
                ["contentType"] = "text/html",
                ["body"] = "<p onclick=\"x()\">Hi <b>there</b></p><script>alert(1)</script><a href=\"javascript:alert(1)\">bad</a><table><tr><td>cell</td></tr></table>",
            },
            ["de-DE"] = new JsonObject { ["contentType"] = "text/plain", ["body"] = "Nur Text <b>bleibt</b>" },
        };

        var response = await admin.Post("/tasks", request);

        response.Status.Should().Be(HttpStatusCode.Created, response.RawText);
        var stored = await Host.DbAsync(db => db.Tasks.AsNoTracking().Where(t => t.Urn == UrnOf(response)).Select(t => t.DescriptionJson).SingleAsync());
        var html = JsonNode.Parse(stored!)!.AsArray().Single(d => d!["languageCode"]!.GetValue<string>() == "en-US")!["body"]!.GetValue<string>();
        html.Should().NotContain("script").And.NotContain("onclick").And.NotContain("javascript:");
        html.Should().Contain("<table>").And.Contain("<b>there</b>");
        var detail = await admin.Get($"/tasks/{AdminClient.Seg(UrnOf(response))}");
        detail.Body!["descriptions"]!.AsArray().Single(d => d!["languageCode"]!.GetValue<string>() == "de-DE")!["body"]!.GetValue<string>()
            .Should().Be("Nur Text <b>bleibt</b>"); // plain text is stored as is

        var bad = AdminClient.NewTask([user]);
        bad["description"] = new JsonObject { ["en-US"] = new JsonObject { ["contentType"] = "application/pdf", ["body"] = "x" } };
        (await admin.Post("/tasks", bad)).ErrorFields.Should().Contain("description.en-US.contentType");
    }

    [Fact]
    public async Task Created_by_can_be_set_to_a_known_user()
    {
        var admin = Host.Admin();
        var user = await Host.AddUserAsync();
        var creator = await Host.AddUserAsync();
        var request = AdminClient.NewTask([user]);
        request["createdBy"] = creator;

        (await admin.Post("/tasks", request)).Body!["createdBy"]!.GetValue<string>().Should().Be(creator);

        request["createdBy"] = Guid.NewGuid().ToString();
        (await admin.Post("/tasks", request)).ErrorFields.Should().Contain("createdBy");
    }

    [Fact] // authentication
    public async Task The_admin_api_needs_admin_credentials_and_rejects_bearer_tokens()
    {
        var http = Host.Factory.CreateClient();
        (await http.GetAsync("/admin/api/tasks")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var bearer = await OAuthTestClient.GetTechTokenAsync(http);
        http.DefaultRequestHeaders.Authorization = new("Bearer", bearer);
        (await http.GetAsync("/admin/api/tasks")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await http.PostAsync("/admin/api/tasks/generate", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }
}

[Collection(SqlCollection.Name)]
public class AdminTaskUpdateTests(SqlServerFixture sql)
{
    private SpiHost Host => SpiHost.Get(sql, "admin");

    private async Task<(string User, AdminClient Admin, string Urn, DateTime Modified)> NewTaskAsync(string definition = "PR_APPROVAL")
    {
        var user = await Host.AddUserAsync();
        var admin = Host.Admin();
        var created = await admin.Post("/tasks", AdminClient.NewTask([user], definition: definition));
        created.Status.Should().Be(HttpStatusCode.Created, created.RawText);
        return (user, admin, created.Body!["urn"]!.GetValue<string>(), DateTime.Parse(created.Body["modifiedAt"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal));
    }

    [Fact] // US-005-2.1
    public async Task Patch_changes_fields_bumps_modified_at_and_clears_modified_by()
    {
        var (user, admin, urn, modified) = await NewTaskAsync();
        var other = await Host.AddUserAsync();
        await Task.Delay(5);

        var response = await admin.Patch($"/tasks/{AdminClient.Seg(urn)}", new JsonObject
        {
            ["subject"] = new JsonObject { ["en-US"] = "Renamed subject " + Guid.NewGuid().ToString("N")[..6], ["de-DE"] = "Umbenannt" },
            ["priority"] = "VERY_HIGH",
            ["dueAt"] = "2031-03-04T05:06:07.890Z",
            ["recipients"] = new JsonObject { ["users"] = new JsonArray(other), ["groups"] = new JsonArray() },
            ["customAttributes"] = new JsonObject { ["amount"] = "99.9", ["currency"] = "EUR" },
            ["description"] = new JsonObject { ["en-US"] = new JsonObject { ["body"] = "<p>New text</p>" } },
        });

        response.Status.Should().Be(HttpStatusCode.OK, response.RawText);
        var json = response.Body!.AsObject();
        json["subject"]!.AsArray().Select(s => s!["languageCode"]!.GetValue<string>()).Should().Equal("en-US", "de-DE");
        json["priority"]!.GetValue<string>().Should().Be("VERY_HIGH");
        json["dueAt"]!.GetValue<string>().Should().Be("2031-03-04T05:06:07.890Z");
        json["recipientUsers"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal(other);
        json["customAttributes"]!.AsArray().Select(a => $"{a!["code"]}={a["value"]}").Should().Equal("amount=99.9", "currency=EUR");
        DateTime.Parse(json["modifiedAt"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal).Should().BeAfter(modified);
        json.ContainsKey("modifiedBy").Should().BeFalse();
        user.Should().NotBe(other);

        // it re-enters the delta pull
        var tech = await Host.TechAsync();
        var pull = await tech.Get($"/tasks?languages=en-US&modifiedAfter={SpiHost.Iso(modified)}&$top=1000");
        pull.Value.Select(t => t!["urn"]!.GetValue<string>()).Should().Contain(urn);
    }

    [Fact]
    public async Task Patch_can_clear_due_date_remove_attributes_and_leave_the_rest_alone()
    {
        var (_, admin, urn, _) = await NewTaskAsync();
        var path = $"/tasks/{AdminClient.Seg(urn)}";
        await admin.Patch(path, new JsonObject { ["dueAt"] = "2031-01-01T00:00:00.000Z", ["customAttributes"] = new JsonObject { ["amount"] = "5", ["currency"] = "ZAR" } });

        var cleared = await admin.Patch(path, new JsonObject { ["dueAt"] = null, ["customAttributes"] = new JsonObject { ["currency"] = null } });

        cleared.Body!["dueAt"].Should().BeNull();
        cleared.Body["customAttributes"]!.AsArray().Select(a => (string)a!["code"]!.GetValue<string>()).Should().Equal("amount");
        cleared.Body["priority"]!.GetValue<string>().Should().Be("MEDIUM"); // untouched
    }

    [Fact]
    public async Task Invalid_patches_are_400_and_change_nothing()
    {
        var (_, admin, urn, modified) = await NewTaskAsync();
        var path = $"/tasks/{AdminClient.Seg(urn)}";

        var response = await admin.Patch(path, new JsonObject
        {
            ["priority"] = "URGENT", ["customAttributes"] = new JsonObject { ["amount"] = "lots" },
            ["recipients"] = new JsonObject { ["users"] = new JsonArray(Guid.NewGuid().ToString()) }, ["dueAt"] = "tomorrow",
        });

        response.Status.Should().Be(HttpStatusCode.BadRequest);
        response.ErrorFields.Should().Contain(["priority", "customAttributes.amount", "recipients.users", "dueAt"]);
        var stored = await Host.LoadTaskAsync(urn);
        (stored.Priority, stored.ModifiedAt).Should().Be((TaskPriorities.Medium, SpiHost.Iso(modified) is { } ? stored.ModifiedAt : modified));
        stored.ModifiedAt.Should().Be(modified);
    }

    [Fact]
    public async Task Unknown_tasks_are_404_and_final_tasks_cannot_be_edited()
    {
        var (user, admin, urn, _) = await NewTaskAsync();

        (await admin.Patch($"/tasks/{AdminClient.Seg(SpiHost.TaskUrn("nope"))}", new JsonObject())).Status.Should().Be(HttpStatusCode.NotFound);
        (await admin.Patch("/tasks/garbage", new JsonObject())).Status.Should().Be(HttpStatusCode.NotFound);

        await admin.Post($"/tasks/{AdminClient.Seg(urn)}/cancel");
        var edit = await admin.Patch($"/tasks/{AdminClient.Seg(urn)}", new JsonObject { ["priority"] = "LOW" });
        edit.Status.Should().Be(HttpStatusCode.Conflict);
        edit.Body!["title"]!.GetValue<string>().Should().Be("tcp.spi.taskFinal");
        user.Should().NotBeNull();
    }

    [Fact] // US-005-2.2
    public async Task Complete_simulates_a_user_finishing_the_task_in_the_provider_ui()
    {
        var (user, admin, urn, modified) = await NewTaskAsync();
        var path = $"/tasks/{AdminClient.Seg(urn)}";

        var response = await admin.Post(path + "/complete", new JsonObject { ["userId"] = user, ["comment"] = "done in provider ui" });

        response.Status.Should().Be(HttpStatusCode.OK, response.RawText);
        response.Body!["status"]!.GetValue<string>().Should().Be("COMPLETED");
        (response.Body["completedBy"]!.GetValue<string>(), response.Body["processor"]!.GetValue<string>()).Should().Be((user, user));
        DateTime.Parse(response.Body["modifiedAt"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal).Should().BeAfter(modified);
        response.Body["validResponseCodes"]!.AsArray().Should().BeEmpty();

        var log = await Host.DbAsync(db => db.OperationLog.AsNoTracking().Where(o => o.TaskUrn == urn).ToListAsync());
        log.Should().ContainSingle().Which.Should().Match<OperationLogEntry>(o => o.Code == "approve" && o.Outcome == "OK" && o.UserId == user && o.Comment == "done in provider ui");

        var again = await admin.Post(path + "/complete", new JsonObject { ["userId"] = user });
        again.Status.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Complete_validates_user_code_and_rules()
    {
        var (user, admin, urn, _) = await NewTaskAsync();
        var path = $"/tasks/{AdminClient.Seg(urn)}/complete";

        (await admin.Post(path, new JsonObject { ["userId"] = Guid.NewGuid().ToString() })).ErrorFields.Should().Contain("userId");
        (await admin.Post(path, new JsonObject())).ErrorFields.Should().Contain("userId");

        var unknownCode = await admin.Post(path, new JsonObject { ["userId"] = user, ["code"] = "teleport" });
        unknownCode.Status.Should().Be(HttpStatusCode.BadRequest);

        var needsComment = await admin.Post(path, new JsonObject { ["userId"] = user, ["code"] = "reject" });
        needsComment.Status.Should().Be(HttpStatusCode.BadRequest);
        needsComment.Body!["title"]!.GetValue<string>().Should().Be("tcp.spi.commentRequired");

        (await admin.Post(path, new JsonObject { ["userId"] = user, ["code"] = "reject", ["comment"] = "no", ["reasonCode"] = "budget" })).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact] // US-005-2.3, 2.4
    public async Task Cancel_deactivate_and_reactivate_follow_the_lifecycle()
    {
        var (_, admin, urn, modified) = await NewTaskAsync();
        var path = $"/tasks/{AdminClient.Seg(urn)}";

        var reactivateTooEarly = await admin.Post(path + "/reactivate");
        reactivateTooEarly.Status.Should().Be(HttpStatusCode.Conflict);

        var deactivated = await admin.Post(path + "/deactivate");
        deactivated.Body!["status"]!.GetValue<string>().Should().Be("INACTIVE");
        (await admin.Post(path + "/deactivate")).Status.Should().Be(HttpStatusCode.Conflict);

        var reactivated = await admin.Post(path + "/reactivate");
        reactivated.Body!["status"]!.GetValue<string>().Should().Be("READY");

        var canceled = await admin.Post(path + "/cancel");
        canceled.Body!["status"]!.GetValue<string>().Should().Be("CANCELED");
        canceled.Body["validActionCodes"]!.AsArray().Should().BeEmpty();
        DateTime.Parse(canceled.Body["modifiedAt"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal).Should().BeAfter(modified);

        foreach (var action in new[] { "/cancel", "/deactivate", "/reactivate" })
            (await admin.Post(path + action)).Status.Should().Be(HttpStatusCode.Conflict, action);

        // the tombstone is still delivered to Task Center
        var tech = await Host.TechAsync();
        (await tech.Get($"/tasks/{path[7..]}?languages=en-US")).Body!["status"]!.GetValue<string>().Should().Be("CANCELED");
    }

    [Fact] // US-005-2.5
    public async Task Tasks_cannot_be_deleted_through_the_admin_api()
    {
        var (_, admin, urn, _) = await NewTaskAsync();

        var response = await admin.Delete($"/tasks/{AdminClient.Seg(urn)}");

        response.Status.Should().Be(HttpStatusCode.MethodNotAllowed);
        (await Host.LoadTaskAsync(urn)).Should().NotBeNull();
    }
}

[Collection(SqlCollection.Name)]
public class AdminInspectTests(SqlServerFixture sql)
{
    [Fact] // US-005-4.1
    public async Task Task_list_filters_sorts_and_pages()
    {
        var host = SpiHost.Isolated(sql);
        var admin = host.Admin();
        var alice = await host.AddUserAsync();
        var bob = await host.AddUserAsync();
        var t0 = DateTime.UtcNow.AddHours(-5);
        var a = await host.AddTaskAsync(new TaskSpec { LocalId = "L1", Users = [alice], Subject = "Approve laptop purchase", ModifiedAt = t0 });
        var b = await host.AddTaskAsync(new TaskSpec { LocalId = "L2", Definition = "LEAVE_APPROVAL", Users = [bob], Subject = "Leave request for 50% of days", ModifiedAt = t0.AddHours(1) });
        var c = await host.AddTaskAsync(new TaskSpec { LocalId = "L3", Users = [alice, bob], Status = TaskStatuses.Completed, CreatedBy = alice, Subject = "Old purchase", ModifiedAt = t0.AddHours(2) });

        var all = await admin.Get("/tasks");
        all.Status.Should().Be(HttpStatusCode.OK);
        all.Body!["total"]!.GetValue<int>().Should().Be(3);
        all.Body["items"]!.AsArray().Select(i => i!["localId"]!.GetValue<string>()).Should().Equal("L3", "L2", "L1"); // newest first
        var first = all.Body["items"]![0]!;
        (first["definition"]!.GetValue<string>(), first["status"]!.GetValue<string>(), first["recipientUsers"]!.GetValue<int>()).Should().Be(("PR_APPROVAL", "COMPLETED", 2));

        string[] Ids(AdminResponse r) => r.Body!["items"]!.AsArray().Select(i => i!["localId"]!.GetValue<string>()).ToArray();
        Ids(await admin.Get("/tasks?status=READY")).Should().Equal("L2", "L1");
        Ids(await admin.Get("/tasks?definition=LEAVE_APPROVAL")).Should().Equal("L2");
        Ids(await admin.Get($"/tasks?definition={AdminClient.Seg(SpiHost.DefinitionUrn("LEAVE_APPROVAL"))}")).Should().Equal("L2");
        Ids(await admin.Get($"/tasks?user={alice}")).Should().Equal("L3", "L1");
        Ids(await admin.Get("/tasks?search=purchase")).Should().Equal("L3", "L1");
        Ids(await admin.Get("/tasks?search=50%25")).Should().Equal("L2"); // LIKE wildcards are literal
        Ids(await admin.Get("/tasks?search=l2")).Should().Equal("L2"); // matches the local id, case-insensitively
        Ids(await admin.Get("/tasks?top=1&skip=1")).Should().Equal("L2");
        (await admin.Get("/tasks?top=1&skip=1")).Body!["total"]!.GetValue<int>().Should().Be(3);
        Ids(await admin.Get("/tasks?definition=NOPE")).Should().BeEmpty();

        foreach (var bad in new[] { "status=WHATEVER", "top=0", "top=501", "skip=-1", "top=abc" })
            (await admin.Get("/tasks?" + bad)).Status.Should().Be(HttpStatusCode.BadRequest, bad);
        _ = (a, b, c);
    }

    [Fact] // the detail view used by the UI and the app page
    public async Task Task_detail_includes_users_descriptions_operations_and_errors()
    {
        var host = SpiHost.Isolated(sql);
        var admin = host.Admin();
        var user = await host.AddUserAsync(email: "detail@corp.example");
        var task = await host.AddTaskAsync(new TaskSpec
        {
            Users = [user], Groups = ["DETAIL_GROUP"], Descriptions = [new("en-US", "text/html", "<p>Hi</p>")],
        });
        await (await host.UserAsync(user)).Act(task.Urn, "claim");
        await host.DbAsync(async db =>
        {
            db.TaskOperationErrors.Add(new TaskOperationError { TaskUrn = task.Urn, ExecutedAt = DateTime.UtcNow, Code = "approve", Message = "boom", ExecutedBy = user });
            await db.SaveChangesAsync();
        });

        var detail = await admin.Get($"/tasks/{AdminClient.Seg(task.Urn)}");

        detail.Status.Should().Be(HttpStatusCode.OK, detail.RawText);
        detail.Body!["task"]!["status"]!.GetValue<string>().Should().Be("RESERVED");
        detail.Body["users"]!.AsArray().Single()!["email"]!.GetValue<string>().Should().Be("detail@corp.example");
        detail.Body["groups"]!.AsArray().Single()!.GetValue<string>().Should().Be("DETAIL_GROUP");
        detail.Body["descriptions"]!.AsArray().Single()!["body"]!.GetValue<string>().Should().Be("<p>Hi</p>");
        detail.Body["operations"]!.AsArray().Single()!["code"]!.GetValue<string>().Should().Be("claim");
        detail.Body["errors"]!.AsArray().Single()!["message"]!.GetValue<string>().Should().Be("boom");
        (await admin.Get($"/tasks/{AdminClient.Seg(SpiHost.TaskUrn("nope"))}")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact] // US-005-4.2, 4.4
    public async Task Users_groups_and_operations_can_be_inspected()
    {
        var host = SpiHost.Isolated(sql);
        var admin = host.Admin();
        var withId = await host.AddUserAsync(name: "inspect.alice", email: "inspect.alice@corp.example", groups: "INSPECT_GROUP");
        await host.DbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            db.ScimUsers.Add(new Domain.Identity.ScimUser { Id = Guid.NewGuid(), UserName = "inspect.nogid", GlobalUserId = null, Created = now, LastModified = now });
            db.ScimUsers.Add(new Domain.Identity.ScimUser { Id = Guid.NewGuid(), UserName = "inspect.deleted", GlobalUserId = null, IsDeleted = true, Created = now, LastModified = now });
            await db.SaveChangesAsync();
        });

        var users = await admin.Get("/users?search=inspect");
        users.Body!["total"]!.GetValue<int>().Should().Be(2); // deleted users are not listed
        var byName = users.Body["items"]!.AsArray().ToDictionary(i => i!["userName"]!.GetValue<string>());
        byName["inspect.alice"]!["globalUserId"]!.GetValue<string>().Should().Be(withId);
        byName["inspect.alice"]!["missingGlobalUserId"]!.GetValue<bool>().Should().BeFalse();
        byName["inspect.alice"]!["groups"]![0]!.GetValue<string>().Should().Be("INSPECT_GROUP");
        byName["inspect.nogid"]!["missingGlobalUserId"]!.GetValue<bool>().Should().BeTrue(); // highlighted in the UI
        (await admin.Get("/users?search=alice@corp")).Body!["total"]!.GetValue<int>().Should().Be(1);

        var groups = await admin.Get("/groups");
        var group = groups.Body!["items"]!.AsArray().Single(g => g!["displayName"]!.GetValue<string>() == "INSPECT_GROUP")!;
        group["memberCount"]!.GetValue<int>().Should().Be(1);
        group["members"]![0]!["globalUserId"]!.GetValue<string>().Should().Be(withId);

        var task = await host.AddTaskAsync(new TaskSpec { Users = [withId] });
        var client = await host.UserAsync(withId);
        await client.Act(task.Urn, "claim");
        await client.Respond(task.Urn, "approve");
        await client.Respond(task.Urn, "approve"); // rejected: final

        var ops = await admin.Get($"/operations?taskUrn={AdminClient.Seg(task.Urn)}");
        ops.Body!["total"]!.GetValue<int>().Should().Be(3);
        ops.Body["items"]!.AsArray().Select(i => $"{i!["kind"]}:{i["code"]}:{i["outcome"]}").Should()
            .Equal("RESPONSE:approve:REJECTED", "RESPONSE:approve:OK", "ACTION:claim:OK"); // newest first
        (await admin.Get($"/operations?user={withId}")).Body!["total"]!.GetValue<int>().Should().Be(3);
        (await admin.Get("/operations?top=0")).Status.Should().Be(HttpStatusCode.BadRequest);
    }
}

[Collection(SqlCollection.Name)]
public class AdminDiagnosticsAndGeneratorTests(SqlServerFixture sql)
{
    [Fact] // US-005-4.5, T005-06
    public async Task Technical_token_is_a_working_token_for_the_spi()
    {
        var host = SpiHost.Get(sql, "admin");
        var admin = host.Admin();

        var response = await admin.Post("/diagnostics/technical-token");

        response.Status.Should().Be(HttpStatusCode.OK, response.RawText);
        response.Body!["scope"]!.GetValue<string>().Should().Be("spi.tech");
        response.Body["claims"]!["client_id"]!.GetValue<string>().Should().Be("tc-tech");
        var token = response.Body["access_token"]!.GetValue<string>();

        var http = host.Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        (await new SpiClient(http).Get("/capabilities")).Status.Should().Be(HttpStatusCode.OK);

        var scim = await admin.Post("/diagnostics/technical-token", new JsonObject { ["clientId"] = "ips-scim" });
        scim.Body!["scope"]!.GetValue<string>().Should().Be("scim");
        (await admin.Post("/diagnostics/technical-token", new JsonObject { ["clientId"] = "tc-pp" })).Status.Should().Be(HttpStatusCode.BadRequest);
        (await admin.Post("/diagnostics/technical-token", new JsonObject { ["clientId"] = "ghost" })).Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact] // US-005-4.5
    public async Task Simulated_pull_returns_what_the_spi_returns()
    {
        var host = SpiHost.Isolated(sql);
        var admin = host.Admin();
        var user = await host.AddUserAsync();
        var t0 = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++) await host.AddTaskAsync(new TaskSpec { LocalId = $"sim{i}", Users = [user], ModifiedAt = t0.AddMilliseconds(i / 2) });
        var tech = await host.TechAsync();

        var simulated = await admin.Post("/diagnostics/simulate-pull", new JsonObject
            { ["modifiedAfter"] = SpiHost.Iso(t0), ["lastId"] = SpiHost.TaskUrn("sim0"), ["top"] = 3, ["languages"] = "en-US" });
        var real = await tech.Get($"/tasks?languages=en-US&modifiedAfter={SpiHost.Iso(t0)}&lastId={Uri.EscapeDataString(SpiHost.TaskUrn("sim0"))}&$top=3");

        simulated.Status.Should().Be(HttpStatusCode.OK, simulated.RawText);
        simulated.Body!["count"]!.GetValue<int>().Should().Be(3);
        JsonNode.DeepEquals(simulated.Body["response"], real.Body).Should().BeTrue();
        simulated.Body["durationMs"]!.GetValue<long>().Should().BeGreaterThanOrEqualTo(0);

        (await admin.Post("/diagnostics/simulate-pull", new JsonObject { ["modifiedAfter"] = "yesterday" })).ErrorFields.Should().Contain("modifiedAfter");
        (await admin.Post("/diagnostics/simulate-pull", new JsonObject { ["lastId"] = "x" })).ErrorFields.Should().Contain("lastId");
        (await admin.Post("/diagnostics/simulate-pull", new JsonObject())).Status.Should().Be(HttpStatusCode.OK); // defaults
    }

    [Fact] // US-005-3 / T005-09: 2 500 tasks, one timestamp, pulled exactly once in pages of 1000
    public async Task Generator_with_same_timestamp_exercises_last_id_paging()
    {
        var host = SpiHost.Isolated(sql);
        var admin = host.Admin();
        var user = await host.AddUserAsync();

        var response = await admin.Post("/tasks/generate", new JsonObject
        {
            ["count"] = 2500, ["definitionLocalId"] = "PR_APPROVAL", ["sameTimestamp"] = true,
            ["recipients"] = new JsonObject { ["users"] = new JsonArray(user) },
        });

        response.Status.Should().Be(HttpStatusCode.OK, response.RawText);
        response.Body!["created"]!.GetValue<int>().Should().Be(2500);
        var stamp = response.Body["modifiedAt"]!.GetValue<string>();
        (await host.DbAsync(db => db.Tasks.Select(t => t.ModifiedAt).Distinct().CountAsync())).Should().Be(1);

        var tech = await host.TechAsync();
        var seen = new List<string>();
        var pages = new List<int>();
        string? lastId = null;
        while (true)
        {
            var path = $"/tasks?languages=en-US&$top=1000&modifiedAfter={stamp}" + (lastId is null ? "" : $"&lastId={Uri.EscapeDataString(lastId)}");
            var page = await tech.Get(path);
            if (page.Value.Count == 0) break;
            pages.Add(page.Value.Count);
            seen.AddRange(page.Value.Select(t => t!["urn"]!.GetValue<string>()));
            lastId = seen[^1];
        }

        pages.Should().Equal(1000, 1000, 500);
        seen.Should().HaveCount(2500).And.OnlyHaveUniqueItems();

        // generated data is valid: no attribute is dropped by the mapper, subjects are unique
        host.Logs.Messages.Should().NotContain(m => m.Contains("Dropped invalid custom attribute"));
        (await host.DbAsync(db => db.Tasks.Select(t => t.SubjectJson).Distinct().CountAsync())).Should().Be(2500);
    }

    [Fact]
    public async Task Generator_spreads_timestamps_spans_definitions_and_validates_its_input()
    {
        var host = SpiHost.Isolated(sql);
        var admin = host.Admin();
        var user = await host.AddUserAsync();
        var recipients = new JsonObject { ["users"] = new JsonArray(user) };

        var mixed = await admin.Post("/tasks/generate", new JsonObject { ["count"] = 90, ["recipients"] = recipients.DeepClone(), ["seed"] = 7 });
        mixed.Status.Should().Be(HttpStatusCode.OK, mixed.RawText);
        mixed.Body!.AsObject().ContainsKey("modifiedAt").Should().BeFalse();
        (await host.DbAsync(db => db.Tasks.Select(t => t.DefinitionUrn).Distinct().CountAsync())).Should().BeGreaterThan(1);
        (await host.DbAsync(db => db.Tasks.Select(t => t.ModifiedAt).Distinct().CountAsync())).Should().BeGreaterThan(50);

        // every generated task round-trips through the SPI
        var tech = await host.TechAsync();
        (await tech.Get("/tasks?languages=en-US,de-DE&$top=1000")).Value.Should().HaveCount(90);
        host.Logs.Messages.Should().NotContain(m => m.Contains("Dropped invalid custom attribute"));

        foreach (var (body, field) in new (JsonObject, string)[]
                 {
                     (new JsonObject { ["count"] = 0, ["recipients"] = recipients.DeepClone() }, "count"),
                     (new JsonObject { ["count"] = 10001, ["recipients"] = recipients.DeepClone() }, "count"),
                     (new JsonObject { ["count"] = 5 }, "recipients"),
                     (new JsonObject { ["count"] = 5, ["recipients"] = recipients.DeepClone(), ["definitionLocalId"] = "NOPE" }, "definitionLocalId"),
                 })
        {
            var response = await admin.Post("/tasks/generate", body);
            response.Status.Should().Be(HttpStatusCode.BadRequest);
            response.ErrorFields.Should().Contain(field);
        }
    }
}
