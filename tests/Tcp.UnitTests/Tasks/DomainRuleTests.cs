using Tcp.Domain.Tasks;

namespace Tcp.UnitTests.Tasks;

public class UrnTests
{
    [Fact] // T004-01
    public void Builds_and_parses_task_and_definition_urns()
    {
        var task = Urn.Build(UrnKind.Task, "integrove", "tcproto", "valterra-dev", "PR_APPROVAL-20261007-000042");
        task.Value.Should().Be("urn:sap.odm.bpm.task:integrove:tcproto:valterra-dev:PR_APPROVAL-20261007-000042");

        var def = Urn.Build(UrnKind.TaskDefinition, "integrove", "tcproto", "valterra-dev", "PR_APPROVAL");
        def.Value.Should().Be("urn:sap.odm.bpm.taskdefinition:integrove:tcproto:valterra-dev:PR_APPROVAL");

        Urn.TryParse(task.Value, out var parsedTask).Should().BeTrue();
        parsedTask.Should().Be(task);
        Urn.TryParse(def.Value, out var parsedDef).Should().BeTrue();
        parsedDef!.Kind.Should().Be(UrnKind.TaskDefinition);
        (parsedDef.ApplicationId, parsedDef.ApplicationInstanceId, parsedDef.TenantId, parsedDef.LocalId)
            .Should().Be(("integrove", "tcproto", "valterra-dev", "PR_APPROVAL"));
    }

    [Theory] // T004-01
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a urn")]
    [InlineData("urn:sap.odm.bpm.task:a:b:c")]               // too few parts
    [InlineData("urn:sap.odm.bpm.task:a:b:c:d:e")]           // too many parts
    [InlineData("urn:sap.odm.bpm.other:a:b:c:d")]            // unknown kind
    [InlineData("URN:sap.odm.bpm.task:a:b:c:d")]             // prefix is case-sensitive
    [InlineData("urn:sap.odm.bpm.task:a:b:c:")]              // empty local id
    [InlineData("urn:sap.odm.bpm.task:a:b:c:has space")]
    [InlineData("urn:sap.odm.bpm.task:a:b:c:has/slash")]
    [InlineData("urn:sap.odm.bpm.task:a:b:c:\u00fcml")]
    [InlineData("urn:sap.odm.bpm.task:a:b:c:x%3Ay")]
    public void Rejects_malformed_urns(string? value) => Urn.TryParse(value, out _).Should().BeFalse();

    [Fact] // T004-01: max lengths
    public void Enforces_part_and_total_length_limits()
    {
        var max = new string('a', 64);
        Urn.TryParse($"urn:sap.odm.bpm.task:{max}:{max}:{max}:{max}", out var ok).Should().BeTrue();
        ok!.Value.Length.Should().BeLessThanOrEqualTo(Urn.MaxLength);

        var tooLong = new string('a', 65);
        Urn.TryParse($"urn:sap.odm.bpm.task:a:b:c:{tooLong}", out _).Should().BeFalse();

        var build = () => Urn.Build(UrnKind.Task, "a", "b", "c", tooLong);
        build.Should().Throw<ArgumentException>();

        Urn.TryParse("urn:sap.odm.bpm.task:a:b:c:" + new string('x', 400), out _).Should().BeFalse();
    }

    [Fact]
    public void Allowed_characters_are_letters_digits_underscore_dot_dash()
    {
        Urn.TryParse("urn:sap.odm.bpm.task:a-b:c_d:e.f:G-1_2.3", out _).Should().BeTrue();
        Urn.Build(UrnKind.Task, "a", "b", "c", "x").ToString().Should().Be(Urn.TaskPrefix + "a:b:c:x");
    }
}

public class LocalizedTextSelectorTests
{
    private static readonly IReadOnlyList<LocalizedText> Texts =
    [
        new("en-US", "Approve"), new("de-DE", "Genehmigen"), new("fr-FR", "Approuver"),
    ];

    private static string Dump(IReadOnlyList<SelectedText> r) =>
        string.Join("|", r.Select(t => $"{t.LanguageCode}:{t.Text}{(t.IsDefault ? "*" : "")}"));

    [Theory] // T004-03
    [InlineData("en-US", "en-US:Approve*")]
    [InlineData("de-DE", "de-DE:Genehmigen*")]                                  // default language not requested -> first returned is default
    [InlineData("en-US,de-DE", "en-US:Approve*|de-DE:Genehmigen")]
    [InlineData("de-DE,en-US", "de-DE:Genehmigen|en-US:Approve*")]               // default language wins wherever it is
    [InlineData("de-DE,fr-FR", "de-DE:Genehmigen*|fr-FR:Approuver")]             // no default present -> first
    [InlineData("en-US,en-US,de-DE", "en-US:Approve*|de-DE:Genehmigen")]         // duplicates collapse
    [InlineData("EN-us", "en-US:Approve*")]                                       // case-insensitive match
    [InlineData("it-IT", "en-US:Approve*")]                                       // nothing requested exists -> default language text
    [InlineData("it-IT,es-ES", "en-US:Approve*")]
    [InlineData("it-IT,de-DE", "de-DE:Genehmigen*")]                              // existing ones only
    public void Selects_requested_languages_with_exactly_one_default(string languages, string expected)
    {
        var result = LocalizedTextSelector.Select(Texts, languages.Split(','), "en-US");

        Dump(result).Should().Be(expected);
        result.Count(t => t.IsDefault).Should().Be(1);
    }

    [Fact] // T004-03
    public void Falls_back_to_the_first_text_when_the_default_language_is_missing()
    {
        var onlyGerman = new List<LocalizedText> { new("de-DE", "Genehmigen"), new("fr-FR", "Approuver") };

        Dump(LocalizedTextSelector.Select(onlyGerman, ["it-IT"], "en-US")).Should().Be("de-DE:Genehmigen*");
    }

    [Fact]
    public void Empty_input_yields_nothing_and_first_duplicate_language_wins()
    {
        LocalizedTextSelector.Select([], ["en-US"], "en-US").Should().BeEmpty();
        var dup = new List<LocalizedText> { new("en-US", "first"), new("en-US", "second") };
        Dump(LocalizedTextSelector.Select(dup, ["en-US"], "en-US")).Should().Be("en-US:first*");
    }
}

public class CustomAttributeValidatorTests
{
    [Theory] // T004-04: valid values (and their normalised form)
    [InlineData("STRING", "hello", "hello")]
    [InlineData("STRING", "", "")]
    [InlineData("BOOLEAN", "true", "true")]
    [InlineData("BOOLEAN", "FALSE", "false")]
    [InlineData("INTEGER", "42", "42")]
    [InlineData("INTEGER", "-7", "-7")]
    [InlineData("INTEGER", "2147483647", "2147483647")]
    [InlineData("FLOAT", "1234.5", "1234.5")]
    [InlineData("FLOAT", "-0.25", "-0.25")]
    [InlineData("FLOAT", "1e3", "1000")]
    [InlineData("DATE", "2026-10-07", "2026-10-07")]
    [InlineData("DATE", "2024-02-29", "2024-02-29")]
    [InlineData("DATETIME", "2026-10-07T13:14:15.123Z", "2026-10-07T13:14:15.123Z")]
    [InlineData("TIME", "23:59:59", "23:59:59")]
    [InlineData("TIME", "00:00:00", "00:00:00")]
    public void Accepts_valid_values(string type, string value, string normalized)
    {
        CustomAttributeValidator.TryNormalize(type, value, out var result).Should().BeTrue();
        result.Should().Be(normalized);
    }

    [Theory] // T004-04: invalid values
    [InlineData("BOOLEAN", "yes")]
    [InlineData("BOOLEAN", "1")]
    [InlineData("INTEGER", "2147483648")]
    [InlineData("INTEGER", "1.5")]
    [InlineData("INTEGER", "007")]
    [InlineData("INTEGER", "")]
    [InlineData("INTEGER", "12a")]
    [InlineData("FLOAT", "abc")]
    [InlineData("FLOAT", "NaN")]
    [InlineData("FLOAT", "Infinity")]
    [InlineData("FLOAT", "1,5")]
    [InlineData("DATE", "2026-13-01")]
    [InlineData("DATE", "2026-02-30")]
    [InlineData("DATE", "2023-02-29")]
    [InlineData("DATE", "07.10.2026")]
    [InlineData("DATE", "2026-10-07T00:00:00.000Z")]
    [InlineData("DATETIME", "2026-10-07T13:14:15Z")]       // no milliseconds
    [InlineData("DATETIME", "2026-10-07T13:14:15.123+02:00")]
    [InlineData("DATETIME", "2026-10-07 13:14:15.123Z")]
    [InlineData("TIME", "24:00:00")]
    [InlineData("TIME", "12:60:00")]
    [InlineData("TIME", "12:00")]
    [InlineData("UNKNOWNTYPE", "x")]
    public void Rejects_invalid_values(string type, string value) =>
        CustomAttributeValidator.TryNormalize(type, value, out _).Should().BeFalse();

    [Fact]
    public void Strings_longer_than_255_are_truncated_and_null_is_invalid()
    {
        CustomAttributeValidator.TryNormalize("STRING", new string('x', 300), out var result).Should().BeTrue();
        result.Should().HaveLength(255);
        CustomAttributeValidator.TryNormalize("STRING", null, out _).Should().BeFalse();
        CustomAttributeValidator.Types.Should().HaveCount(7);
    }
}

public class OperationRulesAndEntitlementTests
{
    private static OperationDefinition Op(string code, string comment = "UNSUPPORTED", string reason = "UNSUPPORTED", params string[] reasons) =>
        new(code, [new("en-US", code)], "NEUTRAL", comment, reason, reasons.Select(r => new ReasonDefinition(r, [new("en-US", r)])).ToList(), null);

    private static TaskDefinition Definition(params string[] actions) => new()
    {
        Urn = "urn:def", LocalId = "DEF", Name = [new("en-US", "Def")],
        Responses = [Op("approve", "OPTIONAL"), Op("reject", "REQUIRED", "OPTIONAL", "budget", "other"), Op("return", "UNSUPPORTED", "REQUIRED", "price", "qty")],
        Actions = actions.Select(a => Op(a)).ToList(),
    };

    private static TaskInstance Task(string status = TaskStatuses.Ready, string? processor = null, string priority = TaskPriorities.Medium) =>
        new() { Urn = "urn:t", Status = status, Processor = processor, Priority = priority };

    [Theory] // T004-05 validActionCodes
    [InlineData(TaskStatuses.Ready, null, "MEDIUM", "claim,increasePriority")]
    [InlineData(TaskStatuses.Ready, null, "VERY_HIGH", "claim")]
    [InlineData(TaskStatuses.Reserved, "u1", "MEDIUM", "release,increasePriority")]
    [InlineData(TaskStatuses.Reserved, "u1", "VERY_HIGH", "release")]
    [InlineData(TaskStatuses.Ready, "u1", "LOW", "release,increasePriority")]
    [InlineData(TaskStatuses.Completed, "u1", "LOW", "")]
    [InlineData(TaskStatuses.Canceled, null, "LOW", "")]
    [InlineData(TaskStatuses.InProgress, null, "LOW", "increasePriority")]   // claim only from READY
    public void Valid_action_codes_follow_the_task_state(string status, string? processor, string priority, string expected)
    {
        var codes = OperationRules.ValidActionCodes(Task(status, processor, priority), Definition("claim", "release", "increasePriority"));
        string.Join(",", codes).Should().Be(expected);
    }

    [Fact] // only actions the definition declares are offered
    public void Valid_action_codes_never_exceed_the_definition()
    {
        OperationRules.ValidActionCodes(Task(), Definition("claim", "release")).Should().Equal("claim");
        OperationRules.ValidActionCodes(Task(), Definition()).Should().BeEmpty();
    }

    [Fact] // T004-05 validResponseCodes
    public void Valid_response_codes_are_null_while_open_and_empty_when_final()
    {
        var def = Definition();
        OperationRules.ValidResponseCodes(Task(), def).Should().BeNull();
        OperationRules.ValidResponseCodes(Task(TaskStatuses.Reserved, "u"), def).Should().BeNull();
        OperationRules.ValidResponseCodes(Task(TaskStatuses.Completed), def).Should().BeEmpty();
        OperationRules.ValidResponseCodes(Task(TaskStatuses.Canceled), def).Should().BeEmpty();
    }

    [Theory] // T004-05 comment / reason table
    [InlineData("approve", null, null, null)]
    [InlineData("approve", "fine", null, null)]
    [InlineData("reject", null, null, SpiCodes.CommentRequired)]
    [InlineData("reject", "", null, SpiCodes.CommentRequired)]
    [InlineData("reject", "   ", null, SpiCodes.CommentRequired)]
    [InlineData("reject", "no budget", null, null)]                         // reason optional
    [InlineData("reject", "no budget", "budget", null)]
    [InlineData("reject", "no budget", "nonsense", SpiCodes.InvalidReason)]
    [InlineData("return", null, null, SpiCodes.ReasonRequired)]             // reason required
    [InlineData("return", null, "  ", SpiCodes.ReasonRequired)]
    [InlineData("return", null, "nonsense", SpiCodes.InvalidReason)]
    [InlineData("return", null, "price", null)]
    [InlineData("approve", null, "whatever", null)]                          // reasons unsupported -> ignored
    public void Comment_and_reason_requirements(string code, string? comment, string? reason, string? expectedError)
    {
        var op = Definition().FindResponse(code)!;
        var act = () => OperationRules.CheckCommentAndReason(op, comment, reason);

        if (expectedError is null) act.Should().NotThrow();
        else act.Should().Throw<TaskRuleViolation>().Which.Code.Should().Be(expectedError);
    }

    [Theory] // T004-05 entitlement incl. group membership + processor rules
    [InlineData(null, true, false, true, EntitlementResult.Entitled)]       // direct recipient
    [InlineData(null, false, true, true, EntitlementResult.Entitled)]       // via group
    [InlineData(null, false, false, true, EntitlementResult.NotAuthorized)] // stranger
    [InlineData(null, true, true, false, EntitlementResult.NotAuthorized)]  // inactive user is never entitled
    [InlineData("me", false, false, true, EntitlementResult.Entitled)]      // the processor is always entitled
    [InlineData("me", true, false, true, EntitlementResult.Entitled)]
    [InlineData("other", true, false, true, EntitlementResult.ReservedByOther)]
    [InlineData("other", false, true, true, EntitlementResult.ReservedByOther)]
    [InlineData("other", false, false, true, EntitlementResult.NotAuthorized)] // not entitled at all beats reserved
    [InlineData("me", true, true, false, EntitlementResult.NotAuthorized)]
    public void Entitlement_table(string? processor, bool recipient, bool groupMember, bool active, EntitlementResult expected) =>
        Entitlement.Evaluate(processor, "me", recipient, groupMember, active).Should().Be(expected);
}

public class TaskDescriptionSelectorTests
{
    private static readonly IReadOnlyList<TaskDescription> Descriptions =
    [
        new("en-US", "text/html", "<p>Hello</p>"), new("de-DE", "text/html", "<p>Hallo</p>"),
    ];

    [Theory]
    [InlineData("de-DE", "de-DE")]
    [InlineData("de", "de-DE")]                // primary language
    [InlineData("de-AT", "de-DE")]             // same primary language, different region
    [InlineData("fr-FR,de", "de-DE")]          // first supported preference
    [InlineData("fr-FR", "en-US")]             // nothing matches -> provider default
    [InlineData("*", "en-US")]
    [InlineData("", "en-US")]
    public void Chooses_the_best_language(string accept, string expected) =>
        TaskDescriptionSelector.Select(Descriptions, accept.Split(',', StringSplitOptions.RemoveEmptyEntries), "en-US")!
            .LanguageCode.Should().Be(expected);

    [Fact]
    public void Falls_back_to_the_first_and_handles_empty()
    {
        TaskDescriptionSelector.Select([new("fr-FR", "text/plain", "Bonjour")], ["de"], "en-US")!.LanguageCode.Should().Be("fr-FR");
        TaskDescriptionSelector.Select([], ["de"], "en-US").Should().BeNull();
    }

    [Fact]
    public void Parses_stored_json_and_tolerates_missing_data()
    {
        TaskDescriptionSelector.Parse(null).Should().BeEmpty();
        TaskDescriptionSelector.Parse("[]").Should().BeEmpty();
        var parsed = TaskDescriptionSelector.Parse("""[{"languageCode":"en-US","contentType":"text/plain","body":"x"},{"body":"no language"}]""");
        parsed.Should().ContainSingle().Which.ContentType.Should().Be("text/plain");
    }
}
