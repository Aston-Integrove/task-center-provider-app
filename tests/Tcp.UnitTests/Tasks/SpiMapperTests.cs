using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Tcp.Api.Endpoints.Spi;
using Tcp.Domain.Tasks;

namespace Tcp.UnitTests.Tasks;

public class SpiMapperTests
{
    private static readonly DateTime Created = new(2026, 10, 7, 8, 0, 0, 5, DateTimeKind.Utc);
    private static readonly DateTime Modified = new(2026, 10, 7, 9, 30, 15, 250, DateTimeKind.Utc);
    private const string Alice = "0b3c7d1e-4f5a-4b6c-8d9e-0a1b2c3d4e5f";
    private const string Bob = "11111111-2222-3333-4444-555555555555";
    private const string Gone = "99999999-8888-7777-6666-555555555555";
    private const string TaskUrn = "urn:sap.odm.bpm.task:integrove:tcproto:dev:PR_APPROVAL-20261007-000042";
    private const string DefUrn = "urn:sap.odm.bpm.taskdefinition:integrove:tcproto:dev:PR_APPROVAL";

    private static SpiContext Ctx(params string[] languages) =>
        new("integrove", "tcproto", "dev", "https://tc.test", "en-US", languages.Length == 0 ? ["en-US"] : languages);

    private static OperationDefinition Op(string code, string comment = "UNSUPPORTED") =>
        new(code, [new("en-US", code.ToUpperInvariant()), new("de-DE", code + "-de")], "NEUTRAL", comment, "UNSUPPORTED", [], null);

    private static readonly TaskDefinition Definition = new()
    {
        Urn = DefUrn, LocalId = "PR_APPROVAL",
        Name = [new("en-US", "Approve PR"), new("de-DE", "BANF genehmigen")],
        Responses = [Op("approve", "OPTIONAL")],
        Actions = [Op("claim"), Op("release"), Op("increasePriority")],
        CustomAttributes =
        [
            new("amount", "FLOAT", null, [new("en-US", "Amount")], 100),
            new("currency", "STRING", null, [new("en-US", "Currency")], 90),
            new("neededBy", "DATE", null, [new("en-US", "Needed by")], 60),
        ],
        Capabilities = new JsonArray(new JsonObject { ["name"] = "tasks.description", ["value"] = true }),
        TaskDetailsSettings = new JsonObject { ["webUISettings"] = new JsonObject { ["uiType"] = "Default" } },
    };

    private static TaskInstance OpenTask() => new()
    {
        Urn = TaskUrn, LocalId = "PR_APPROVAL-20261007-000042", DefinitionUrn = DefUrn,
        Status = TaskStatuses.Ready, Priority = TaskPriorities.High,
        SubjectJson = """[{"languageCode":"en-US","text":"Approve PR 4711 - Laptop"},{"languageCode":"de-DE","text":"BANF 4711 genehmigen - Laptop"}]""",
        CreatedAt = Created, CreatedBy = Alice, ModifiedAt = Modified,
        RecipientUsers = [new() { TaskUrn = TaskUrn, GlobalUserId = Bob }, new() { TaskUrn = TaskUrn, GlobalUserId = Alice }],
        RecipientGroups = [new() { TaskUrn = TaskUrn, GroupName = "TC_PROTO_USERS" }],
        CustomAttributes =
        [
            new() { TaskUrn = TaskUrn, Code = "amount", Value = "1234.50" },
            new() { TaskUrn = TaskUrn, Code = "currency", Value = "ZAR" },
        ],
    };

    private static IReadOnlySet<string> Active(params string[] ids) => ids.ToHashSet(StringComparer.Ordinal);

    [Fact] // T004-13 golden: open task, explicit nulls, camelCase, ms timestamps
    public void Open_task_matches_the_golden_payload()
    {
        var json = SpiMapper.Task(OpenTask(), Definition, Ctx("en-US", "de-DE"), Active(Alice, Bob));

        var expected = JsonNode.Parse("""
        {
          "urn": "urn:sap.odm.bpm.task:integrove:tcproto:dev:PR_APPROVAL-20261007-000042",
          "applicationId": "integrove", "applicationInstanceId": "tcproto", "tenantId": "dev",
          "localId": "PR_APPROVAL-20261007-000042",
          "definitionId": "urn:sap.odm.bpm.taskdefinition:integrove:tcproto:dev:PR_APPROVAL",
          "status": "READY", "priority": "HIGH",
          "subject": [
            {"languageCode":"en-US","text":"Approve PR 4711 - Laptop","isDefault":true},
            {"languageCode":"de-DE","text":"BANF 4711 genehmigen - Laptop","isDefault":false}
          ],
          "createdAt": "2026-10-07T08:00:00.005Z",
          "createdBy": "0b3c7d1e-4f5a-4b6c-8d9e-0a1b2c3d4e5f",
          "modifiedAt": "2026-10-07T09:30:15.250Z",
          "processor": null, "dueAt": null, "completedAt": null,
          "uiLink": "https://tc.test/app/tasks/urn%3Asap.odm.bpm.task%3Aintegrove%3Atcproto%3Adev%3APR_APPROVAL-20261007-000042",
          "customAttributes": [ {"code":"amount","value":"1234.5"}, {"code":"currency","value":"ZAR"} ],
          "validResponseCodes": null,
          "validActionCodes": ["claim","increasePriority"],
          "recipientUsers": ["0b3c7d1e-4f5a-4b6c-8d9e-0a1b2c3d4e5f","11111111-2222-3333-4444-555555555555"],
          "recipientGroups": ["TC_PROTO_USERS"]
        }
        """)!;

        JsonNode.DeepEquals(json, expected).Should().BeTrue($"actual: {json.ToJsonString()}");
        json.AsObject().ContainsKey("processor").Should().BeTrue(); // explicit null, not omitted
        json.AsObject().ContainsKey("operationErrors").Should().BeFalse();
    }

    [Fact] // US-004-4.6 / 4.7 final task
    public void Completed_task_has_empty_valid_codes_and_completion_fields()
    {
        var t = OpenTask();
        t.Status = TaskStatuses.Completed;
        t.Processor = Bob;
        t.CompletedBy = Bob;
        t.CompletedAt = Modified;
        t.ModifiedBy = Bob;

        var json = SpiMapper.Task(t, Definition, Ctx(), Active(Alice, Bob));

        json["validResponseCodes"]!.AsArray().Should().BeEmpty();
        json["validActionCodes"]!.AsArray().Should().BeEmpty();
        json["completedAt"]!.GetValue<string>().Should().Be("2026-10-07T09:30:15.250Z");
        json["completedBy"]!.GetValue<string>().Should().Be(Bob);
        json["modifiedBy"]!.GetValue<string>().Should().Be(Bob);
        json["processor"]!.GetValue<string>().Should().Be(Bob);
    }

    [Fact] // US-004-4.2: only active users; the processor is always included
    public void Recipient_users_are_filtered_to_active_users_and_include_the_processor()
    {
        var t = OpenTask();
        t.RecipientUsers.Add(new TaskRecipientUser { TaskUrn = TaskUrn, GlobalUserId = Gone });
        t.Processor = "zzz-processor";
        t.Status = TaskStatuses.Reserved;

        var json = SpiMapper.Task(t, Definition, Ctx(), Active(Alice, Bob)); // Gone is not active

        json["recipientUsers"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().Equal(Alice, Bob, "zzz-processor");
    }

    [Fact] // the SAP schema makes recipientGroups a non-nullable array: no groups is [] and never null
    public void Recipient_groups_are_an_empty_array_when_empty_and_sorted_otherwise()
    {
        var t = OpenTask();
        t.RecipientGroups = [];
        var empty = SpiMapper.Task(t, Definition, Ctx(), Active()).AsObject();
        empty.ContainsKey("recipientGroups").Should().BeTrue();
        empty["recipientGroups"]!.AsArray().Should().BeEmpty();

        t.RecipientGroups =
        [
            new() { TaskUrn = TaskUrn, GroupName = "B_GROUP" }, new() { TaskUrn = TaskUrn, GroupName = "A_GROUP" },
        ];
        SpiMapper.Task(t, Definition, Ctx(), Active())["recipientGroups"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().Equal("A_GROUP", "B_GROUP");
    }

    [Fact] // US-004-4.3 via the mapper
    public void Subject_honours_the_languages_parameter_with_one_default()
    {
        var de = SpiMapper.Task(OpenTask(), Definition, Ctx("de-DE"), Active())["subject"]!.AsArray();
        de.Should().ContainSingle();
        de[0]!["languageCode"]!.GetValue<string>().Should().Be("de-DE");
        de[0]!["isDefault"]!.GetValue<bool>().Should().BeTrue();

        var unknown = SpiMapper.Task(OpenTask(), Definition, Ctx("fr-FR"), Active())["subject"]!.AsArray();
        unknown.Should().ContainSingle();
        unknown[0]!["languageCode"]!.GetValue<string>().Should().Be("en-US");
        unknown[0]!["isDefault"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact] // US-004-4.8: invalid stored values are omitted and reported
    public void Invalid_custom_attributes_are_dropped_and_unknown_codes_ignored()
    {
        var t = OpenTask();
        t.CustomAttributes =
        [
            new() { TaskUrn = TaskUrn, Code = "amount", Value = "not-a-number" },
            new() { TaskUrn = TaskUrn, Code = "neededBy", Value = "2026-10-31" },
            new() { TaskUrn = TaskUrn, Code = "undefined", Value = "x" },
        ];
        var dropped = new List<string>();

        var json = SpiMapper.Task(t, Definition, Ctx(), Active(), (code, type) => dropped.Add($"{code}:{type}"));

        json["customAttributes"]!.AsArray().Select(a => $"{a!["code"]}={a["value"]}").Should().Equal("neededBy=2026-10-31");
        dropped.Should().Equal("amount:FLOAT");
    }

    [Fact] // US-004-4.9
    public void Operation_errors_show_the_latest_per_user_and_are_omitted_when_absent()
    {
        var t = OpenTask();
        t.OperationErrors =
        [
            new() { Id = 1, TaskUrn = TaskUrn, ExecutedAt = Modified, Code = "approve", Message = "first", ExecutedBy = Bob },
            new() { Id = 3, TaskUrn = TaskUrn, ExecutedAt = Modified.AddMinutes(1), Code = "approve", Message = "latest", ExecutedBy = Bob },
            new() { Id = 2, TaskUrn = TaskUrn, ExecutedAt = Modified, Code = "reject", Message = "other user", ExecutedBy = Alice },
        ];

        var errors = SpiMapper.Task(t, Definition, Ctx(), Active())["operationErrors"]!.AsArray();

        errors.Select(e => e!["message"]!.GetValue<string>()).Should().BeEquivalentTo("latest", "other user");
        errors.Single(e => e!["executedBy"]!.GetValue<string>() == Bob)!["executedAt"]!.GetValue<string>().Should().Be("2026-10-07T09:31:15.250Z");
    }

    [Fact] // defensive: an unknown definition must not break the pull
    public void Missing_definition_still_maps()
    {
        var json = SpiMapper.Task(OpenTask(), null, Ctx(), Active());

        json["customAttributes"]!.AsArray().Should().BeEmpty();
        json["validActionCodes"]!.AsArray().Should().BeEmpty();
        json["validResponseCodes"].Should().BeNull();
    }

    [Fact] // definitions
    public void Definition_maps_with_language_filtering_and_sorted_attributes()
    {
        var json = SpiMapper.Definition(Definition, Ctx("de-DE"));

        json["urn"]!.GetValue<string>().Should().Be(DefUrn);
        json["name"]!.AsArray().Single()!["text"]!.GetValue<string>().Should().Be("BANF genehmigen");
        json["possibleResponses"]![0]!["name"]![0]!["text"]!.GetValue<string>().Should().Be("approve-de");
        json["possibleResponses"]![0]!["commentRequired"]!.GetValue<string>().Should().Be("OPTIONAL");
        json["customAttributes"]!.AsArray().Select(a => a!["code"]!.GetValue<string>()).Should().Equal("amount", "currency", "neededBy");
        json["capabilities"]![0]!["value"]!.GetValue<bool>().Should().BeTrue();
        json["taskDetailsSettings"]!["webUISettings"]!["uiType"]!.GetValue<string>().Should().Be("Default");
    }

    [Theory]
    [InlineData("2026-10-07T09:30:15.250Z", 2026, 10, 7, 9, 30, 15, 250)]
    [InlineData("2026-01-02T03:04:05.000Z", 2026, 1, 2, 3, 4, 5, 0)]
    public void Timestamps_use_millisecond_utc_format(string expected, int y, int mo, int d, int h, int mi, int s, int ms) =>
        SpiMapper.Format(new DateTime(y, mo, d, h, mi, s, ms, DateTimeKind.Unspecified)).Should().Be(expected);
}

public class SpiMessagesTests
{
    private static readonly string[] AllCodes = typeof(SpiCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToArray();

    [Fact] // FR-SPI-04
    public void Every_error_code_has_an_english_and_a_german_message()
    {
        foreach (var code in AllCodes)
        {
            var en = SpiMessages.Get(code, SpiMessages.English, "x");
            var de = SpiMessages.Get(code, SpiMessages.German, "x");
            en.Should().NotBe(code, $"{code} needs an English message");
            de.Should().NotBe(code, $"{code} needs a German message");
            de.Should().NotBe(en, $"{code} should be translated");
        }
    }

    [Fact]
    public void Placeholders_are_filled_and_unknown_codes_fall_back_to_the_code()
    {
        SpiMessages.Get(SpiCodes.InvalidParameter, SpiMessages.English, "$top").Should().Contain("$top");
        SpiMessages.Get(SpiCodes.InvalidParameter, SpiMessages.German, "$top").Should().Contain("$top").And.Contain("ungültig");
        SpiMessages.Get("tcp.spi.madeUp", SpiMessages.English).Should().Be("tcp.spi.madeUp");
    }

    [Theory]
    [InlineData("de-DE", "de-DE")]
    [InlineData("de", "de-DE")]
    [InlineData("en-GB,de;q=0.5", "en-US")]
    [InlineData("fr-FR,de;q=0.9,en;q=0.5", "de-DE")]       // first supported by quality
    [InlineData("fr-FR", "en-US")]                          // unsupported -> English
    [InlineData("", "en-US")]
    [InlineData("###", "en-US")]                            // malformed header never throws
    public void Culture_is_chosen_from_accept_language(string header, string expected)
    {
        var ctx = new DefaultHttpContext();
        if (header.Length > 0) ctx.Request.Headers.AcceptLanguage = header;
        SpiMessages.CultureFor(ctx.Request).Name.Should().Be(expected);
    }

    [Theory]
    [InlineData(SpiCodes.InvalidParameter, 400)]
    [InlineData(SpiCodes.CommentRequired, 400)]
    [InlineData(SpiCodes.TaskNotFound, 404)]
    [InlineData(SpiCodes.TaskDefinitionNotFound, 404)]
    [InlineData(SpiCodes.TaskFinal, 409)]
    [InlineData(SpiCodes.ActionNotValid, 409)]
    [InlineData(SpiCodes.ConcurrentUpdate, 409)]
    [InlineData(SpiCodes.NotAuthorized, 403)]
    [InlineData(SpiCodes.ReservedByOther, 403)]
    [InlineData(SpiCodes.UserContextRequired, 403)]
    [InlineData(SpiCodes.Unauthorized, 401)]
    [InlineData(SpiCodes.NotImplemented, 501)]
    [InlineData(SpiCodes.InternalError, 500)]
    public void Error_codes_map_to_http_status(string code, int status) =>
        SpiErrors.StatusFor(code).Should().Be(status);
}
