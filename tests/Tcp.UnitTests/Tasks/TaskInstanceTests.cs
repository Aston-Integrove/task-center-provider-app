using Tcp.Domain.Tasks;

namespace Tcp.UnitTests.Tasks;

public class TaskInstanceTests
{
    private static readonly DateTime T0 = new(2026, 10, 7, 12, 0, 0, 123, DateTimeKind.Utc);

    private static OperationDefinition Op(string code, string comment = "UNSUPPORTED", string reason = "UNSUPPORTED", params string[] reasons) =>
        new(code, [new("en-US", code)], "NEUTRAL", comment, reason, reasons.Select(r => new ReasonDefinition(r, [new("en-US", r)])).ToList(), null);

    private static readonly TaskDefinition Definition = new()
    {
        Urn = "urn:def", LocalId = "DEF", Name = [new("en-US", "Def")],
        Responses = [Op("approve", "OPTIONAL"), Op("reject", "REQUIRED", "OPTIONAL", "budget")],
        Actions = [Op("claim"), Op("release"), Op("increasePriority"), Op("escalate")],
    };

    private static TaskInstance Task(string status = TaskStatuses.Ready, string? processor = null, string priority = TaskPriorities.Medium) => new()
    {
        Urn = "urn:t", Status = status, Processor = processor, Priority = priority,
        CreatedAt = T0.AddMinutes(-10), ModifiedAt = T0.AddMinutes(-5),
    };

    private static TaskRuleViolation Violation(Action act) =>
        act.Should().Throw<TaskRuleViolation>().Which;

    [Fact] // T004-06 respond
    public void Respond_completes_the_task_as_the_user()
    {
        var t = Task();

        t.Respond(Definition, "approve", "u1", "ok", null, T0);

        t.Status.Should().Be(TaskStatuses.Completed);
        (t.Processor, t.CompletedBy, t.ModifiedBy).Should().Be(("u1", "u1", "u1"));
        t.CompletedAt.Should().Be(T0);
        t.ModifiedAt.Should().Be(T0);
        t.IsFinal.Should().BeTrue();
    }

    [Fact] // T004-06
    public void Respond_enforces_final_state_known_codes_and_comment_rules_without_mutating()
    {
        var done = Task(TaskStatuses.Completed);
        Violation(() => done.Respond(Definition, "approve", "u1", null, null, T0)).Code.Should().Be(SpiCodes.TaskFinal);

        var open = Task();
        var before = open.ModifiedAt;
        Violation(() => open.Respond(Definition, "nope", "u1", null, null, T0)).Code.Should().Be(SpiCodes.InvalidOperation);
        Violation(() => open.Respond(Definition, "reject", "u1", null, null, T0)).Code.Should().Be(SpiCodes.CommentRequired);
        Violation(() => open.Respond(Definition, "reject", "u1", "x", "wrong", T0)).Code.Should().Be(SpiCodes.InvalidReason);

        (open.Status, open.Processor, open.CompletedAt, open.ModifiedAt).Should().Be((TaskStatuses.Ready, null, null, before));
    }

    [Fact] // T004-06 claim / release
    public void Claim_reserves_and_release_returns_the_task()
    {
        var t = Task();

        t.ExecuteAction(Definition, "claim", "u1", null, null, T0);
        (t.Status, t.Processor).Should().Be((TaskStatuses.Reserved, "u1"));

        t.ExecuteAction(Definition, "release", "u1", null, null, T0.AddSeconds(1));
        (t.Status, t.Processor).Should().Be((TaskStatuses.Ready, null));
        t.ModifiedBy.Should().Be("u1");
    }

    [Fact] // T004-06
    public void Claim_and_release_guard_ownership()
    {
        var reserved = Task(TaskStatuses.Reserved, "owner");
        Violation(() => reserved.ExecuteAction(Definition, "claim", "intruder", null, null, T0)).Code.Should().Be(SpiCodes.ReservedByOther);
        Violation(() => reserved.ExecuteAction(Definition, "claim", "owner", null, null, T0)).Code.Should().Be(SpiCodes.ActionNotValid);
        Violation(() => reserved.ExecuteAction(Definition, "release", "intruder", null, null, T0)).Code.Should().Be(SpiCodes.ReservedByOther);

        var free = Task();
        Violation(() => free.ExecuteAction(Definition, "release", "u1", null, null, T0)).Code.Should().Be(SpiCodes.ActionNotValid);
    }

    [Fact] // T004-06 increasePriority ladder
    public void Increase_priority_climbs_the_ladder_and_stops_at_very_high()
    {
        var t = Task(priority: TaskPriorities.Low);
        var seen = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            t.ExecuteAction(Definition, "increasePriority", "u1", null, null, T0.AddSeconds(i));
            seen.Add(t.Priority);
        }
        seen.Should().Equal(TaskPriorities.Medium, TaskPriorities.High, TaskPriorities.VeryHigh);

        Violation(() => t.ExecuteAction(Definition, "increasePriority", "u1", null, null, T0.AddSeconds(9))).Code.Should().Be(SpiCodes.ActionNotValid);
        t.Priority.Should().Be(TaskPriorities.VeryHigh);
    }

    [Fact] // T004-06
    public void Unknown_unexecutable_and_final_actions_are_rejected()
    {
        var t = Task();
        Violation(() => t.ExecuteAction(Definition, "nope", "u1", null, null, T0)).Code.Should().Be(SpiCodes.InvalidOperation);
        Violation(() => t.ExecuteAction(Definition, "escalate", "u1", null, null, T0)).Code.Should().Be(SpiCodes.InvalidOperation); // declared, but unknown to the provider
        Violation(() => Task(TaskStatuses.Canceled).ExecuteAction(Definition, "claim", "u1", null, null, T0)).Code.Should().Be(SpiCodes.TaskFinal);
    }

    [Fact] // T004-06 cancel = tombstone
    public void Cancel_tombstones_the_task_once()
    {
        var t = Task(TaskStatuses.Reserved, "u1");

        t.Cancel(T0);

        (t.Status, t.Processor, t.ModifiedBy).Should().Be((TaskStatuses.Canceled, null, null));
        Violation(() => t.Cancel(T0.AddSeconds(1))).Code.Should().Be(SpiCodes.TaskFinal);
    }

    [Fact] // T004-06, FR-SPI-03
    public void Touch_is_monotonic_per_task_and_truncates_to_milliseconds()
    {
        var t = Task();
        t.ModifiedAt = T0;

        t.Touch(T0.AddTicks(7_654), "u"); // same millisecond after truncation -> previous + 1 ms
        t.ModifiedAt.Should().Be(T0.AddMilliseconds(1));

        t.Touch(T0.AddSeconds(-30), "u"); // the clock went backwards
        t.ModifiedAt.Should().Be(T0.AddMilliseconds(2));

        t.Touch(T0.AddSeconds(10).AddTicks(9_999), "u");
        t.ModifiedAt.Should().Be(T0.AddSeconds(10));
        t.ModifiedAt.Ticks.Should().Be(t.ModifiedAt.Ticks - t.ModifiedAt.Ticks % TimeSpan.TicksPerMillisecond);
        t.ModifiedAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact] // many touches in the same instant are strictly increasing
    public void Repeated_touches_in_one_instant_are_strictly_increasing()
    {
        var t = Task();
        t.ModifiedAt = T0.AddMinutes(-1);
        var previous = t.ModifiedAt;
        for (var i = 0; i < 20; i++)
        {
            t.Touch(T0, null);
            t.ModifiedAt.Should().BeAfter(previous);
            previous = t.ModifiedAt;
        }
    }
}

public class TaskDefinitionParsingTests
{
    [Fact]
    public void Parses_the_stored_json_columns()
    {
        var entity = new TaskDefinitionEntity
        {
            Urn = "urn:def", LocalId = "DEF",
            NameJson = """[{"languageCode":"en-US","text":"Def"},{"languageCode":"de-DE","text":"Definition"}]""",
            ResponsesJson = """[{"code":"approve","name":[{"languageCode":"en-US","text":"Approve"}],"nature":"POSITIVE","commentRequired":"OPTIONAL","reasonRequired":"UNSUPPORTED","possibleReasons":[],"capabilities":[{"name":"tasks.bulk.operations","value":false}]}]""",
            ActionsJson = """[{"code":"claim","name":[{"languageCode":"en-US","text":"Claim"}]}]""",
            CustomAttributesJson = """[{"code":"amount","type":"FLOAT","rank":100,"name":[{"languageCode":"en-US","text":"Amount"}]}]""",
            CapabilitiesJson = """[{"name":"tasks.description","value":true}]""",
            TaskDetailsSettingsJson = """{"webUISettings":{"uiType":"Default"}}""",
        };

        var d = TaskDefinition.FromEntity(entity);

        d.Name.Should().HaveCount(2);
        d.FindResponse("approve")!.CommentRequired.Should().Be("OPTIONAL");
        d.FindResponse("approve")!.Capabilities.Should().NotBeNull();
        d.FindAction("claim")!.Nature.Should().Be("NEUTRAL"); // SAP default
        d.FindAction("claim")!.CommentRequired.Should().Be("UNSUPPORTED");
        d.FindAttribute("amount")!.Rank.Should().Be(100);
        d.FindAttribute("missing").Should().BeNull();
        d.Capabilities.Should().ContainSingle();
        d.TaskDetailsSettings!["webUISettings"]!["uiType"]!.GetValue<string>().Should().Be("Default");
    }
}
