using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Tcp.Domain.Tasks;
using Tcp.TestSupport;

namespace Tcp.IntegrationTests;

[Collection(SqlCollection.Name)]
public class SpiDescriptionTests(SqlServerFixture sql)
{
    private SpiHost Host => SpiHost.Get(sql, "ops");

    private static readonly IReadOnlyList<TaskDescription> Descriptions =
    [
        new("en-US", "text/html", "<p>Please approve <b>PR 4711</b></p>"),
        new("de-DE", "text/html", "<p>Bitte BANF <b>4711</b> genehmigen</p>"),
    ];

    private static string Path(TaskInstance t) => $"/tasks/{Uri.EscapeDataString(t.Urn)}/description";

    [Theory] // US-004-6.1
    [InlineData("de-DE", "de-DE", "Bitte BANF")]
    [InlineData("de", "de-DE", "Bitte BANF")]
    [InlineData("en-US", "en-US", "Please approve")]
    [InlineData("fr-FR,de;q=0.8", "de-DE", "Bitte BANF")]
    [InlineData("fr-FR", "en-US", "Please approve")]      // nothing matches -> provider default
    [InlineData("", "en-US", "Please approve")]
    public async Task Entitled_user_gets_the_description_in_the_best_language(string acceptLanguage, string contentLanguage, string expectedText)
    {
        var user = await Host.AddUserAsync();
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [user], Descriptions = Descriptions });
        var client = await Host.UserAsync(user);

        var response = await client.Get(Path(task), acceptLanguage.Length == 0 ? null : acceptLanguage);

        response.Status.Should().Be(HttpStatusCode.OK, response.RawText);
        // TaskProviderV2.json: the description endpoint returns plain text
        response.Raw.Content.Headers.ContentType!.ToString().Should().Be("text/plain; charset=utf-8");
        response.Raw.Content.Headers.ContentLanguage.Should().Equal(contentLanguage);
        response.RawText.Should().Contain(expectedText).And.NotContain("<");
    }

    [Fact] // plain-text descriptions pass through unchanged
    public async Task Plain_text_descriptions_are_served_as_they_are()
    {
        var user = await Host.AddUserAsync();
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [user], Descriptions = [new("en-US", "text/plain", "Just text")] });

        var response = await (await Host.UserAsync(user)).Get(Path(task));

        response.Raw.Content.Headers.ContentType!.ToString().Should().Be("text/plain; charset=utf-8");
        response.RawText.Should().Be("Just text");
    }

    [Fact] // FR-SPI-AUTH: via group membership
    public async Task Group_members_are_entitled()
    {
        var member = await Host.AddUserAsync(groups: "DESC_GROUP");
        var outsider = await Host.AddUserAsync();
        var task = await Host.AddTaskAsync(new TaskSpec { Groups = ["DESC_GROUP"], Descriptions = Descriptions });

        (await (await Host.UserAsync(member)).Get(Path(task))).Status.Should().Be(HttpStatusCode.OK);
        (await (await Host.UserAsync(outsider)).Get(Path(task))).ErrorCode.Should().Be("tcp.spi.notAuthorized");
    }

    [Fact] // US-004-6.2
    public async Task Not_entitled_reserved_unknown_and_missing_descriptions_are_rejected()
    {
        var recipient = await Host.AddUserAsync();
        var stranger = await Host.AddUserAsync();
        var other = await Host.AddUserAsync();
        var open = await Host.AddTaskAsync(new TaskSpec { Users = [recipient], Descriptions = Descriptions });
        var reserved = await Host.AddTaskAsync(new TaskSpec { Users = [recipient, other], Processor = other, Status = TaskStatuses.Reserved, Descriptions = Descriptions });
        var none = await Host.AddTaskAsync(new TaskSpec { Users = [recipient] });

        var strangerResponse = await (await Host.UserAsync(stranger)).Get(Path(open));
        (strangerResponse.Status, strangerResponse.ErrorCode).Should().Be((HttpStatusCode.Forbidden, "tcp.spi.notAuthorized"));

        var reservedResponse = await (await Host.UserAsync(recipient)).Get(Path(reserved));
        (reservedResponse.Status, reservedResponse.ErrorCode).Should().Be((HttpStatusCode.Forbidden, "tcp.spi.reservedByOther"));
        (await (await Host.UserAsync(other)).Get(Path(reserved))).Status.Should().Be(HttpStatusCode.OK); // the processor may read it

        var missing = await (await Host.UserAsync(recipient)).Get(Path(none));
        (missing.Status, missing.ErrorCode).Should().Be((HttpStatusCode.NotFound, "tcp.spi.descriptionNotFound"));

        var unknown = await (await Host.UserAsync(recipient)).Get($"/tasks/{Uri.EscapeDataString(SpiHost.TaskUrn("nope"))}/description");
        (unknown.Status, unknown.ErrorCode).Should().Be((HttpStatusCode.NotFound, "tcp.spi.taskNotFound"));
    }

    [Fact] // entitlement is evaluated live: a deactivated user with an unexpired token is out
    public async Task Deactivated_users_lose_access_immediately()
    {
        var user = await Host.AddUserAsync();
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [user], Descriptions = Descriptions });
        var client = await Host.UserAsync(user);
        (await client.Get(Path(task))).Status.Should().Be(HttpStatusCode.OK);

        await Host.DbAsync(db => db.ScimUsers.Where(u => u.GlobalUserId == user).ExecuteUpdateAsync(s => s.SetProperty(u => u.Active, false)));

        (await client.Get(Path(task))).ErrorCode.Should().Be("tcp.spi.notAuthorized");
    }

    [Fact] // FR-SPI-04
    public async Task Rejections_are_localised()
    {
        var stranger = await Host.UserAsync(await Host.AddUserAsync());
        var task = await Host.AddTaskAsync(new TaskSpec { Descriptions = Descriptions });

        var de = await stranger.Get(Path(task), "de-DE");
        var en = await stranger.Get(Path(task), "en-US");

        de.ErrorMessage.Should().Contain("nicht berechtigt");
        en.ErrorMessage.Should().Contain("not authorized");
    }
}

[Collection(SqlCollection.Name)]
public class SpiResponseTests(SqlServerFixture sql)
{
    private SpiHost Host => SpiHost.Get(sql, "ops");

    private async Task<(string User, SpiClient Client, TaskInstance Task)> OpenTaskAsync(string definition = "PR_APPROVAL", string priority = TaskPriorities.Medium)
    {
        var user = await Host.AddUserAsync();
        var task = await Host.AddTaskAsync(new TaskSpec
        {
            Definition = definition, Users = [user], Priority = priority,
            ModifiedAt = DateTime.UtcNow.AddMinutes(-10), Descriptions = [new("en-US", "text/html", "<p>x</p>")],
        });
        return (user, await Host.UserAsync(user), task);
    }

    private Task<List<OperationLogEntry>> LogOf(string urn) =>
        Host.DbAsync(db => db.OperationLog.AsNoTracking().Where(o => o.TaskUrn == urn).OrderBy(o => o.Id).ToListAsync());

    [Fact] // US-004-7.1
    public async Task Approve_completes_the_task_and_returns_the_full_task()
    {
        var (user, client, task) = await OpenTaskAsync();
        var before = DateTime.UtcNow.AddSeconds(-2);

        var response = await client.Respond(task.Urn, "approve", "looks good");

        response.Status.Should().Be(HttpStatusCode.OK, response.RawText);
        var json = response.Body!.AsObject();
        json["status"]!.GetValue<string>().Should().Be("COMPLETED");
        (json["completedBy"]!.GetValue<string>(), json["modifiedBy"]!.GetValue<string>(), json["processor"]!.GetValue<string>()).Should().Be((user, user, user));
        DateTime.Parse(json["completedAt"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal).Should().BeAfter(before);
        DateTime.Parse(json["modifiedAt"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal).Should().BeAfter(task.ModifiedAt);
        json["validResponseCodes"]!.AsArray().Should().BeEmpty();
        json["validActionCodes"]!.AsArray().Should().BeEmpty();
        json["recipientUsers"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain(user);
        json["urn"]!.GetValue<string>().Should().Be(task.Urn);

        var stored = await Host.LoadTaskAsync(task.Urn);
        (stored.Status, stored.CompletedBy, stored.Processor).Should().Be((TaskStatuses.Completed, user, user));
    }

    [Fact] // the change reaches Task Center through the next DELTA pull
    public async Task A_response_flows_through_the_delta_pull()
    {
        var (_, client, task) = await OpenTaskAsync();
        var tech = await Host.TechAsync();

        await client.Respond(task.Urn, "approve");

        var pull = await tech.Get($"/tasks?languages=en-US&modifiedAfter={SpiHost.Iso(task.ModifiedAt)}&$top=1000");
        var pulled = pull.Value.Single(t => t!["urn"]!.GetValue<string>() == task.Urn)!;
        pulled["status"]!.GetValue<string>().Should().Be("COMPLETED");
    }

    [Fact] // US-004-7.2 + localisation
    public async Task Comment_required_for_reject_with_localised_message()
    {
        var (_, client, task) = await OpenTaskAsync();

        foreach (var comment in new string?[] { null, "", "   " })
        {
            var response = await client.Respond(task.Urn, "reject", comment);
            (response.Status, response.ErrorCode, response.ErrorTarget).Should().Be((HttpStatusCode.BadRequest, "tcp.spi.commentRequired", "comment"));
        }

        var de = await client.Respond(task.Urn, "reject", null, acceptLanguage: "de-DE");
        de.ErrorMessage.Should().Contain("Kommentar");
        var en = await client.Respond(task.Urn, "reject", null, acceptLanguage: "en-US");
        en.ErrorMessage.Should().Contain("comment is required");

        (await Host.LoadTaskAsync(task.Urn)).Status.Should().Be(TaskStatuses.Ready); // nothing changed

        (await client.Respond(task.Urn, "reject", "too expensive", "budget")).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact] // US-004-7.3
    public async Task Reason_required_and_reason_validation()
    {
        var (_, client, task) = await OpenTaskAsync("INVOICE_EXCEPTION");

        var missing = await client.Respond(task.Urn, "return");
        (missing.Status, missing.ErrorCode, missing.ErrorTarget).Should().Be((HttpStatusCode.BadRequest, "tcp.spi.reasonRequired", "reasonCode"));

        var unknown = await client.Respond(task.Urn, "return", null, "nonsense");
        (unknown.Status, unknown.ErrorCode).Should().Be((HttpStatusCode.BadRequest, "tcp.spi.invalidReason"));

        var ok = await client.Respond(task.Urn, "return", null, "price");
        ok.Status.Should().Be(HttpStatusCode.OK, ok.RawText);
    }

    [Fact] // optional reason: absent is fine, wrong code is not
    public async Task Optional_reasons_may_be_omitted_but_must_be_valid_when_given()
    {
        var (_, client, task) = await OpenTaskAsync();

        var wrong = await client.Respond(task.Urn, "reject", "no", "nonsense");
        wrong.ErrorCode.Should().Be("tcp.spi.invalidReason");

        (await client.Respond(task.Urn, "reject", "no")).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact] // US-004-7.4
    public async Task Unknown_response_codes_and_malformed_bodies_are_400()
    {
        var (_, client, task) = await OpenTaskAsync();

        var unknown = await client.Respond(task.Urn, "escalate");
        (unknown.Status, unknown.ErrorCode, unknown.ErrorTarget).Should().Be((HttpStatusCode.BadRequest, "tcp.spi.invalidOperation", "code"));

        // an action code is not a response code
        (await client.Respond(task.Urn, "claim")).ErrorCode.Should().Be("tcp.spi.invalidOperation");

        var path = $"/tasks/{Uri.EscapeDataString(task.Urn)}/response?languages=en-US";
        foreach (var body in new JsonNode[] { new JsonObject(), new JsonObject { ["code"] = "" }, new JsonObject { ["code"] = 5 },
                     new JsonObject { ["code"] = "approve", ["comment"] = 12 }, new JsonArray() })
        {
            var response = await client.Post(path, body);
            (response.Status, response.ErrorCode).Should().Be((HttpStatusCode.BadRequest, "tcp.spi.invalidParameter"), body.ToJsonString());
        }

        var noLanguages = await client.Post($"/tasks/{Uri.EscapeDataString(task.Urn)}/response", SpiClient.Op("approve", null, null));
        (noLanguages.Status, noLanguages.ErrorTarget).Should().Be((HttpStatusCode.BadRequest, "languages"));
    }

    [Theory] // US-004-7.5
    [InlineData(TaskStatuses.Completed)]
    [InlineData(TaskStatuses.Canceled)]
    public async Task Final_tasks_cannot_be_responded_to(string status)
    {
        var user = await Host.AddUserAsync();
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [user], Status = status });

        var response = await (await Host.UserAsync(user)).Respond(task.Urn, "approve");

        (response.Status, response.ErrorCode).Should().Be((HttpStatusCode.Conflict, "tcp.spi.taskFinal"));
    }

    [Fact] // US-004-7.6
    public async Task Reserved_by_another_user_is_403()
    {
        var me = await Host.AddUserAsync();
        var other = await Host.AddUserAsync();
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [me, other], Processor = other, Status = TaskStatuses.Reserved });

        var response = await (await Host.UserAsync(me)).Respond(task.Urn, "approve");
        (response.Status, response.ErrorCode).Should().Be((HttpStatusCode.Forbidden, "tcp.spi.reservedByOther"));

        (await (await Host.UserAsync(other)).Respond(task.Urn, "approve")).Status.Should().Be(HttpStatusCode.OK); // the processor may
    }

    [Fact] // US-004-7.7
    public async Task Not_entitled_users_get_403()
    {
        var recipient = await Host.AddUserAsync();
        var stranger = await Host.AddUserAsync();
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [recipient] });

        var response = await (await Host.UserAsync(stranger)).Respond(task.Urn, "approve");

        (response.Status, response.ErrorCode).Should().Be((HttpStatusCode.Forbidden, "tcp.spi.notAuthorized"));
        (await Host.LoadTaskAsync(task.Urn)).Status.Should().Be(TaskStatuses.Ready);
    }

    [Fact] // US-004-7.8
    public async Task Every_attempt_is_written_to_the_operation_log()
    {
        var (user, client, task) = await OpenTaskAsync();
        var stranger = await Host.UserAsync(await Host.AddUserAsync());

        await stranger.Respond(task.Urn, "approve");                     // rejected: not entitled
        await client.Respond(task.Urn, "reject", null);                  // rejected: comment required
        await client.Respond(task.Urn, "approve", "fine");               // ok
        await client.Respond(task.Urn, "approve", "again");              // rejected: final
        await client.Respond(SpiHost.TaskUrn("no-such-task"), "approve"); // rejected: unknown (logged under that urn)

        var rows = await LogOf(task.Urn);
        rows.Select(r => (r.Kind, r.Code, r.Outcome, r.ErrorCode)).Should().Equal(
            ("RESPONSE", "approve", "REJECTED", "tcp.spi.notAuthorized"),
            ("RESPONSE", "reject", "REJECTED", "tcp.spi.commentRequired"),
            ("RESPONSE", "approve", "OK", null),
            ("RESPONSE", "approve", "REJECTED", "tcp.spi.taskFinal"));
        rows[2].UserId.Should().Be(user);
        rows[2].Comment.Should().Be("fine");
        rows.Should().OnlyContain(r => r.At > DateTime.UtcNow.AddMinutes(-5));

        (await LogOf(SpiHost.TaskUrn("no-such-task"))).Should().ContainSingle().Which.ErrorCode.Should().Be("tcp.spi.taskNotFound");
    }

    [Theory]
    [InlineData("/task-provider/v2")]
    [InlineData("/api/task-provider/v2")]
    public async Task Responses_work_on_both_base_paths(string basePath)
    {
        var user = await Host.AddUserAsync();
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [user] });

        var response = await (await Host.UserAsync(user, basePath)).Respond(task.Urn, "approve");

        response.Status.Should().Be(HttpStatusCode.OK);
    }
}

[Collection(SqlCollection.Name)]
public class SpiActionTests(SqlServerFixture sql)
{
    private SpiHost Host => SpiHost.Get(sql, "ops");

    private async Task<(string User, SpiClient Client, TaskInstance Task)> OpenTaskAsync(string definition = "PR_APPROVAL", string priority = TaskPriorities.Medium)
    {
        var user = await Host.AddUserAsync();
        var task = await Host.AddTaskAsync(new TaskSpec { Definition = definition, Users = [user], Priority = priority, ModifiedAt = DateTime.UtcNow.AddMinutes(-10) });
        return (user, await Host.UserAsync(user), task);
    }

    private static string[] Codes(JsonNode json) => json["validActionCodes"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    [Fact] // US-004-8.1
    public async Task Claim_reserves_the_task_for_the_user()
    {
        var (user, client, task) = await OpenTaskAsync();

        var response = await client.Act(task.Urn, "claim");

        response.Status.Should().Be(HttpStatusCode.OK, response.RawText);
        response.Body!["status"]!.GetValue<string>().Should().Be("RESERVED");
        response.Body["processor"]!.GetValue<string>().Should().Be(user);
        Codes(response.Body!).Should().Equal("release", "increasePriority");
        response.Body["modifiedBy"]!.GetValue<string>().Should().Be(user);
        response.Body["validResponseCodes"].Should().BeNull(); // still open
    }

    [Fact] // US-004-8.2
    public async Task Release_by_the_processor_returns_the_task()
    {
        var (_, client, task) = await OpenTaskAsync();
        await client.Act(task.Urn, "claim");

        var response = await client.Act(task.Urn, "release");

        response.Status.Should().Be(HttpStatusCode.OK, response.RawText);
        response.Body!["status"]!.GetValue<string>().Should().Be("READY");
        response.Body.AsObject().ContainsKey("processor").Should().BeTrue();
        response.Body["processor"].Should().BeNull();
        Codes(response.Body!).Should().Equal("claim", "increasePriority");
    }

    [Fact] // US-004-8.3
    public async Task Release_by_somebody_else_is_403_and_claim_conflicts_are_reported()
    {
        var owner = await Host.AddUserAsync();
        var other = await Host.AddUserAsync();
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [owner, other], Processor = owner, Status = TaskStatuses.Reserved });
        var otherClient = await Host.UserAsync(other);

        var release = await otherClient.Act(task.Urn, "release");
        (release.Status, release.ErrorCode).Should().Be((HttpStatusCode.Forbidden, "tcp.spi.reservedByOther"));

        var claim = await otherClient.Act(task.Urn, "claim");
        (claim.Status, claim.ErrorCode).Should().Be((HttpStatusCode.Forbidden, "tcp.spi.reservedByOther"));

        var again = await (await Host.UserAsync(owner)).Act(task.Urn, "claim"); // already mine
        (again.Status, again.ErrorCode).Should().Be((HttpStatusCode.Conflict, "tcp.spi.actionNotValid"));

        var free = await Host.AddTaskAsync(new TaskSpec { Users = [owner] });
        var nothingToRelease = await (await Host.UserAsync(owner)).Act(free.Urn, "release");
        (nothingToRelease.Status, nothingToRelease.ErrorCode).Should().Be((HttpStatusCode.Conflict, "tcp.spi.actionNotValid"));
    }

    [Fact] // US-004-8.4
    public async Task Increase_priority_climbs_to_very_high_and_then_conflicts()
    {
        var (_, client, task) = await OpenTaskAsync(priority: TaskPriorities.Low);

        var seen = new List<string>();
        foreach (var _ in Enumerable.Range(0, 3))
        {
            var response = await client.Act(task.Urn, "increasePriority");
            response.Status.Should().Be(HttpStatusCode.OK, response.RawText);
            seen.Add(response.Body!["priority"]!.GetValue<string>());
        }
        seen.Should().Equal("MEDIUM", "HIGH", "VERY_HIGH");

        var top = await client.Act(task.Urn, "increasePriority");
        (top.Status, top.ErrorCode).Should().Be((HttpStatusCode.Conflict, "tcp.spi.actionNotValid"));
        (await Host.LoadTaskAsync(task.Urn)).Priority.Should().Be("VERY_HIGH");
    }

    [Fact] // US-004-8.5
    public async Task Actions_outside_the_definition_or_on_final_tasks_are_rejected()
    {
        var (_, client, leave) = await OpenTaskAsync("LEAVE_APPROVAL"); // defines only claim + release

        var notDefined = await client.Act(leave.Urn, "increasePriority");
        (notDefined.Status, notDefined.ErrorCode).Should().Be((HttpStatusCode.BadRequest, "tcp.spi.invalidOperation"));
        (await client.Act(leave.Urn, "approve")).ErrorCode.Should().Be("tcp.spi.invalidOperation"); // a response is not an action
        (await client.Act(leave.Urn, "claim")).Status.Should().Be(HttpStatusCode.OK);

        var user = await Host.AddUserAsync();
        var done = await Host.AddTaskAsync(new TaskSpec { Users = [user], Status = TaskStatuses.Completed });
        var final = await (await Host.UserAsync(user)).Act(done.Urn, "claim");
        (final.Status, final.ErrorCode).Should().Be((HttpStatusCode.Conflict, "tcp.spi.taskFinal"));
    }

    [Fact] // US-004-8.6
    public async Task Every_action_bumps_modified_at_and_flows_through_the_delta_pull()
    {
        var (_, client, task) = await OpenTaskAsync();
        var tech = await Host.TechAsync();
        var cursor = task.ModifiedAt;

        foreach (var code in new[] { "claim", "increasePriority", "release" })
        {
            var response = await client.Act(task.Urn, code);
            response.Status.Should().Be(HttpStatusCode.OK, response.RawText);
            var modified = DateTime.Parse(response.Body!["modifiedAt"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal);
            modified.Should().BeAfter(cursor, $"after {code}");

            var pull = await tech.Get($"/tasks?languages=en-US&modifiedAfter={SpiHost.Iso(cursor)}&$top=1000");
            pull.Value.Select(t => t!["urn"]!.GetValue<string>()).Should().Contain(task.Urn);
            cursor = DateTime.SpecifyKind(modified, DateTimeKind.Utc);
        }
    }

    [Fact] // not entitled / unknown
    public async Task Strangers_and_unknown_tasks_are_rejected_for_actions_too()
    {
        var recipient = await Host.AddUserAsync();
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [recipient] });
        var stranger = await Host.UserAsync(await Host.AddUserAsync());

        (await stranger.Act(task.Urn, "claim")).ErrorCode.Should().Be("tcp.spi.notAuthorized");
        (await (await Host.UserAsync(recipient)).Act(SpiHost.TaskUrn("nope"), "claim")).ErrorCode.Should().Be("tcp.spi.taskNotFound");
    }

    [Fact] // group members can act
    public async Task Group_members_can_claim()
    {
        var member = await Host.AddUserAsync(groups: "ACT_GROUP");
        var task = await Host.AddTaskAsync(new TaskSpec { Groups = ["ACT_GROUP"] });

        var response = await (await Host.UserAsync(member)).Act(task.Urn, "claim");

        response.Status.Should().Be(HttpStatusCode.OK, response.RawText);
        response.Body!["recipientUsers"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain(member); // processor is a recipient
    }
}
