using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Tcp.Domain.Tasks;
using Tcp.TestSupport;

namespace Tcp.IntegrationTests;

[Collection(SqlCollection.Name)]
public partial class SpiTaskPayloadTests(SqlServerFixture sql)
{
    private SpiHost Host => SpiHost.Get(sql, "payload");

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$")]
    private static partial Regex Millis();

    private async Task<JsonObject> FetchAsync(TaskInstance task, string languages = "en-US")
    {
        var tech = await Host.TechAsync();
        var response = await tech.Get($"/tasks/{Uri.EscapeDataString(task.Urn)}?languages={languages}");
        response.Status.Should().Be(HttpStatusCode.OK, response.RawText);
        return response.Body!.AsObject();
    }

    // ---- T004-14 single task -------------------------------------------------------------------

    [Fact] // US-004-5
    public async Task Single_task_accepts_raw_and_encoded_urns_and_any_status()
    {
        var tech = await Host.TechAsync();
        var open = await Host.AddTaskAsync(new TaskSpec());
        var done = await Host.AddTaskAsync(new TaskSpec { Status = TaskStatuses.Completed, CompletedBy = "someone" });
        var tombstone = await Host.AddTaskAsync(new TaskSpec { Status = TaskStatuses.Canceled });

        (await tech.Get($"/tasks/{open.Urn}?languages=en-US")).Body!["urn"]!.GetValue<string>().Should().Be(open.Urn);
        (await tech.Get($"/tasks/{Uri.EscapeDataString(open.Urn)}?languages=en-US")).Status.Should().Be(HttpStatusCode.OK);
        (await tech.Get($"/tasks/{done.Urn}?languages=en-US")).Body!["status"]!.GetValue<string>().Should().Be("COMPLETED");
        (await tech.Get($"/tasks/{tombstone.Urn}?languages=en-US")).Body!["status"]!.GetValue<string>().Should().Be("CANCELED");

        var user = await Host.UserAsync(await Host.AddUserAsync());
        (await user.Get($"/tasks/{open.Urn}?languages=en-US")).Status.Should().Be(HttpStatusCode.OK); // tech or user
    }

    [Theory]
    [InlineData("urn:sap.odm.bpm.task:integrove:tcproto:dev:does-not-exist")]
    [InlineData("urn:sap.odm.bpm.taskdefinition:integrove:tcproto:dev:PR_APPROVAL")]
    [InlineData("urn:sap.odm.bpm.task:other:tcproto:dev:x")]
    [InlineData("garbage")]
    public async Task Unknown_tasks_are_404_with_sap_error(string urn)
    {
        var tech = await Host.TechAsync();

        var response = await tech.Get($"/tasks/{Uri.EscapeDataString(urn)}?languages=en-US");

        response.Status.Should().Be(HttpStatusCode.NotFound);
        response.ErrorCode.Should().Be("tcp.spi.taskNotFound");
    }

    [Fact]
    public async Task Single_task_requires_languages()
    {
        var task = await Host.AddTaskAsync(new TaskSpec());
        var response = await (await Host.TechAsync()).Get($"/tasks/{Uri.EscapeDataString(task.Urn)}");
        (response.Status, response.ErrorTarget).Should().Be((HttpStatusCode.BadRequest, "languages"));
    }

    // ---- US-004-4 payload rules ----------------------------------------------------------------

    [Fact] // 4.1
    public async Task User_fields_are_global_user_ids_and_recipients_include_the_processor()
    {
        var creator = await Host.AddUserAsync();
        var recipient = await Host.AddUserAsync();
        var processor = await Host.AddUserAsync();
        var task = await Host.AddTaskAsync(new TaskSpec
        {
            Status = TaskStatuses.Reserved, CreatedBy = creator, Users = [recipient], Processor = processor,
        });

        var json = await FetchAsync(task);

        json["createdBy"]!.GetValue<string>().Should().Be(creator);
        json["processor"]!.GetValue<string>().Should().Be(processor);
        json["recipientUsers"]!.AsArray().Select(n => n!.GetValue<string>()).Should().BeEquivalentTo(recipient, processor);
        Guid.TryParse(json["createdBy"]!.GetValue<string>(), out _).Should().BeTrue();
    }

    [Fact] // 4.2
    public async Task Recipient_users_exclude_inactive_and_deleted_users_and_groups_are_scim_group_names()
    {
        var active = await Host.AddUserAsync();
        var inactive = await Host.AddUserAsync(active: false);
        var deleted = await Host.AddUserAsync();
        var unknown = Guid.NewGuid().ToString();
        await Host.DbAsync(db => db.ScimUsers.Where(u => u.GlobalUserId == deleted)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDeleted, true)));
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [active, inactive, deleted, unknown], Groups = ["TC_PROTO_USERS", "TC_PROTO_APPROVERS"] });

        var json = await FetchAsync(task);

        json["recipientUsers"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal(active);
        json["recipientGroups"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("TC_PROTO_APPROVERS", "TC_PROTO_USERS");
    }

    [Fact] // 4.3
    public async Task Subject_honours_languages_with_exactly_one_default()
    {
        var task = await Host.AddTaskAsync(new TaskSpec { Subject = "Approve PR 4711", SubjectDe = "BANF 4711 genehmigen" });

        var both = (await FetchAsync(task, "en-US,de-DE"))["subject"]!.AsArray();
        both.Select(s => (s!["languageCode"]!.GetValue<string>(), s["isDefault"]!.GetValue<bool>())).Should().Equal(("en-US", true), ("de-DE", false));

        var german = (await FetchAsync(task, "de-DE"))["subject"]!.AsArray();
        german.Should().ContainSingle();
        german[0]!["text"]!.GetValue<string>().Should().Be("BANF 4711 genehmigen");
        german[0]!["isDefault"]!.GetValue<bool>().Should().BeTrue();

        var missing = (await FetchAsync(task, "fr-FR"))["subject"]!.AsArray();
        missing.Should().ContainSingle().Which!["text"]!.GetValue<string>().Should().Be("Approve PR 4711");
        missing[0]!["isDefault"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact] // 4.4
    public async Task Timestamps_use_millisecond_utc_format_and_absent_ones_are_explicit_null()
    {
        var modified = new DateTime(2026, 10, 7, 9, 30, 15, 250, DateTimeKind.Utc);
        var task = await Host.AddTaskAsync(new TaskSpec { ModifiedAt = modified, CreatedAt = modified.AddHours(-1), DueAt = modified.AddDays(2) });

        var json = await FetchAsync(task);

        json["modifiedAt"]!.GetValue<string>().Should().Be("2026-10-07T09:30:15.250Z");
        json["createdAt"]!.GetValue<string>().Should().Be("2026-10-07T08:30:15.250Z");
        json["dueAt"]!.GetValue<string>().Should().Be("2026-10-09T09:30:15.250Z");
        Millis().IsMatch(json["createdAt"]!.GetValue<string>()).Should().BeTrue();
        foreach (var property in new[] { "processor", "completedAt" })
        {
            json.ContainsKey(property).Should().BeTrue($"{property} must be present");
            json[property].Should().BeNull();
        }
        var noDue = await FetchAsync(await Host.AddTaskAsync(new TaskSpec()));
        noDue.ContainsKey("dueAt").Should().BeTrue();
        noDue["dueAt"].Should().BeNull();
    }

    [Fact] // 4.5
    public async Task Ui_link_points_at_the_open_in_app_page()
    {
        var task = await Host.AddTaskAsync(new TaskSpec());

        var json = await FetchAsync(task);

        json["uiLink"]!.GetValue<string>().Should().Be($"https://tc.test/app/tasks/{Uri.EscapeDataString(task.Urn)}");
    }

    [Theory] // 4.6 + 4.7
    [InlineData(TaskStatuses.Ready, null, "MEDIUM", false, "claim,increasePriority")]
    [InlineData(TaskStatuses.Ready, null, "VERY_HIGH", false, "claim")]
    [InlineData(TaskStatuses.Reserved, "me", "LOW", false, "release,increasePriority")]
    [InlineData(TaskStatuses.Completed, "me", "LOW", true, "")]
    [InlineData(TaskStatuses.Canceled, null, "LOW", true, "")]
    public async Task Valid_response_and_action_codes_are_computed(string status, string? processorMarker, string priority, bool isFinal, string actions)
    {
        var processor = processorMarker is null ? null : await Host.AddUserAsync();
        var task = await Host.AddTaskAsync(new TaskSpec { Status = status, Processor = processor, Priority = priority });

        var json = await FetchAsync(task);

        if (isFinal) json["validResponseCodes"]!.AsArray().Should().BeEmpty();
        else json["validResponseCodes"].Should().BeNull();
        string.Join(",", json["validActionCodes"]!.AsArray().Select(c => c!.GetValue<string>())).Should().Be(actions);
    }

    [Fact] // 4.8
    public async Task Custom_attributes_are_typed_ordered_by_rank_and_invalid_values_are_dropped_and_logged()
    {
        var task = await Host.AddTaskAsync(new TaskSpec
        {
            Attributes = new Dictionary<string, string>
            {
                ["amount"] = "not-a-number", ["currency"] = "ZAR", ["neededBy"] = "2026-10-31", ["requester"] = "L. Robbins", ["bogus"] = "unknown code",
            },
        });
        Host.Logs.Clear();

        var json = await FetchAsync(task);

        json["customAttributes"]!.AsArray().Select(a => $"{a!["code"]}={a["value"]}").Should()
            .Equal("currency=ZAR", "requester=L. Robbins", "neededBy=2026-10-31");
        Host.Logs.Messages.Should().Contain(m => m.Contains("Dropped invalid custom attribute amount") && m.Contains(task.Urn));

        var valid = await FetchAsync(await Host.AddTaskAsync(new TaskSpec { Attributes = new Dictionary<string, string> { ["amount"] = "1234.50" } }));
        valid["customAttributes"]![0]!["value"]!.GetValue<string>().Should().Be("1234.5"); // normalised FLOAT string
    }

    [Fact] // 4.9
    public async Task Operation_errors_are_omitted_unless_present()
    {
        var user = await Host.AddUserAsync();
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [user] });
        (await FetchAsync(task)).ContainsKey("operationErrors").Should().BeFalse();

        await Host.DbAsync(async db =>
        {
            db.TaskOperationErrors.Add(new TaskOperationError { TaskUrn = task.Urn, ExecutedAt = DateTime.UtcNow, Code = "approve", Message = "first failure", ExecutedBy = user });
            await db.SaveChangesAsync();
            db.TaskOperationErrors.Add(new TaskOperationError { TaskUrn = task.Urn, ExecutedAt = DateTime.UtcNow, Code = "approve", Message = "second failure", ExecutedBy = user });
            await db.SaveChangesAsync();
        });

        var errors = (await FetchAsync(task))["operationErrors"]!.AsArray();

        errors.Should().ContainSingle(); // latest per user
        errors[0]!["message"]!.GetValue<string>().Should().Be("second failure");
        errors[0]!["executedBy"]!.GetValue<string>().Should().Be(user);
        Millis().IsMatch(errors[0]!["executedAt"]!.GetValue<string>()).Should().BeTrue();
    }

    [Fact]
    public async Task Task_identity_fields_come_from_configuration()
    {
        var json = await FetchAsync(await Host.AddTaskAsync(new TaskSpec { LocalId = "IDENT-1" }));

        (json["applicationId"]!.GetValue<string>(), json["applicationInstanceId"]!.GetValue<string>(), json["tenantId"]!.GetValue<string>(),
                json["localId"]!.GetValue<string>(), json["definitionId"]!.GetValue<string>())
            .Should().Be(("integrove", "tcproto", "dev", "IDENT-1", SpiHost.DefinitionUrn("PR_APPROVAL")));
        json["urn"]!.GetValue<string>().Should().Be("urn:sap.odm.bpm.task:integrove:tcproto:dev:IDENT-1");
        json["priority"]!.GetValue<string>().Should().Be("MEDIUM");
    }
}
