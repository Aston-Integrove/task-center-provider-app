using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tcp.Api.Configuration;
using Tcp.Api.Endpoints.Spi;
using Tcp.Domain.Identity;
using Tcp.Domain.Tasks;
using Tcp.Infrastructure.Html;
using Tcp.Infrastructure.Persistence;
using Tcp.Infrastructure.Tasks;

namespace Tcp.Api.Endpoints.Admin;

/// <summary>
/// Provider-side task management for testers (spec 005). All mutations go through the <see cref="TaskInstance"/>
/// aggregate so <c>modifiedAt</c> is bumped exactly like for SPI operations, and nothing is ever hard-deleted.
/// </summary>
public sealed partial class AdminTaskService(
    TcpDbContext db,
    TaskRepository tasks,
    DefinitionRepository definitions,
    LocalIdGenerator localIds,
    IUserDirectory users,
    HtmlDescriptionSanitizer sanitizer,
    IOptions<ProviderOptions> provider,
    TimeProvider time)
{
    public const int MaxSubjectLength = 255;
    public const int MaxDescriptionLength = 50_000;
    public const int MaxGeneratedTasks = 10_000;

    [GeneratedRegex("^[a-z]{2}-[A-Z]{2}$")]
    private static partial Regex LanguageTag();

    private ProviderIdentity Identity => new(provider.Value.ApplicationId, provider.Value.ApplicationInstanceId, provider.Value.TenantId);

    // ---- create -------------------------------------------------------------------------------

    public async Task<TaskInstance> CreateAsync(CreateTaskRequest request, CancellationToken ct)
    {
        var errors = new List<AdminError>();

        if (string.IsNullOrWhiteSpace(request.DefinitionLocalId)) errors.Add(new("definitionLocalId", "is required"));
        var definition = string.IsNullOrWhiteSpace(request.DefinitionLocalId) ? null : await definitions.FindByLocalIdAsync(request.DefinitionLocalId, ct);
        if (definition is null && !string.IsNullOrWhiteSpace(request.DefinitionLocalId))
            errors.Add(new("definitionLocalId", $"unknown task definition '{request.DefinitionLocalId}'"));

        var subject = ValidateSubject(request.Subject, errors);
        var recipients = await ResolveRecipientsAsync(request.Recipients, errors, required: true, ct);
        var priority = ValidatePriority(request.Priority, errors) ?? TaskPriorities.Medium;
        var attributes = definition is null ? [] : ValidateAttributes(definition, request.CustomAttributes, errors);
        var descriptions = ValidateDescriptions(request.Description, errors);

        string? createdBy = null;
        if (!string.IsNullOrWhiteSpace(request.CreatedBy))
        {
            var creator = GlobalUserId.Normalize(request.CreatedBy) is { } gid ? await users.FindByGlobalUserIdAsync(gid, ct) : null;
            if (creator is null) errors.Add(new("createdBy", $"unknown user '{request.CreatedBy}'"));
            else createdBy = creator.GlobalUserId;
        }

        if (errors.Count > 0) throw AdminException.Invalid(errors);

        var localId = await localIds.NextAsync(definition!.LocalId, ct);
        var urn = Identity.TaskUrn(localId).Value;
        var now = TaskInstance.TruncateToMs(time.GetUtcNow().UtcDateTime);

        subject = await EnsureUniqueSubjectAsync(subject!, localId, excludeUrn: null, ct);

        var task = new TaskInstance
        {
            Urn = urn, LocalId = localId, DefinitionUrn = definition.Urn, Status = TaskStatuses.Ready, Priority = priority,
            SubjectJson = SubjectToJson(subject), DescriptionJson = descriptions is null ? null : DescriptionsToJson(descriptions),
            CreatedAt = now, CreatedBy = createdBy, ModifiedAt = now, DueAt = request.DueAt is { } due ? TaskInstance.TruncateToMs(due.ToUniversalTime()) : null,
        };
        foreach (var id in recipients!.UserIds) task.RecipientUsers.Add(new TaskRecipientUser { TaskUrn = urn, GlobalUserId = id });
        foreach (var group in recipients.Groups) task.RecipientGroups.Add(new TaskRecipientGroup { TaskUrn = urn, GroupName = group });
        foreach (var (code, value) in attributes) task.CustomAttributes.Add(new TaskCustomAttribute { TaskUrn = urn, Code = code, Value = value });

        db.Tasks.Add(task);
        await db.SaveChangesAsync(ct);
        return task;
    }

    // ---- update -------------------------------------------------------------------------------

    public async Task<TaskInstance> UpdateAsync(string urn, JsonObject patch, CancellationToken ct)
    {
        var task = await tasks.FindAsync(urn, ct, track: true) ?? throw AdminException.NotFound("The task");
        if (task.IsFinal) throw AdminException.FromRule(new TaskRuleViolation(SpiCodes.TaskFinal));

        var definition = await definitions.FindAsync(task.DefinitionUrn, ct)
            ?? throw new InvalidOperationException($"Task {urn} references an unknown definition");
        var errors = new List<AdminError>();

        if (patch.ContainsKey("subject"))
        {
            var map = ReadStringMap(patch["subject"], "subject", errors);
            var subject = ValidateSubject(map, errors);
            if (subject is not null)
                task.SubjectJson = SubjectToJson(await EnsureUniqueSubjectAsync(subject, task.LocalId, task.Urn, ct));
        }

        if (patch.ContainsKey("priority"))
            task.Priority = ValidatePriority(AdminJson.String(patch, "priority"), errors) ?? task.Priority;

        if (patch.ContainsKey("dueAt"))
        {
            if (patch["dueAt"] is null) task.DueAt = null;
            else if (patch["dueAt"] is JsonValue v && v.TryGetValue<DateTime>(out var due)) task.DueAt = TaskInstance.TruncateToMs(due.ToUniversalTime());
            else errors.Add(new("dueAt", "must be an ISO 8601 date-time or null"));
        }

        if (patch.ContainsKey("recipients"))
        {
            var dto = patch["recipients"] is JsonObject r
                ? new RecipientsDto(ReadStringList(r["users"]), ReadStringList(r["groups"]))
                : null;
            var recipients = await ResolveRecipientsAsync(dto, errors, required: true, ct);
            if (recipients is not null && errors.Count == 0) ApplyRecipients(task, recipients);
        }

        if (patch["customAttributes"] is JsonObject attrs)
        {
            var values = attrs.ToDictionary(p => p.Key, p => (string?)(p.Value is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : p.Value?.ToJsonString()));
            ApplyAttributes(task, definition, values, errors);
        }

        if (patch.ContainsKey("description"))
        {
            var map = ReadDescriptionMap(patch["description"], errors);
            var descriptions = ValidateDescriptions(map, errors);
            if (errors.Count == 0) task.DescriptionJson = descriptions is null ? null : DescriptionsToJson(descriptions);
        }

        if (errors.Count > 0) throw AdminException.Invalid(errors);

        task.Touch(time.GetUtcNow().UtcDateTime, modifiedBy: null);
        await SaveAsync(ct);
        return task;
    }

    // ---- lifecycle ----------------------------------------------------------------------------

    public async Task<TaskInstance> CompleteAsync(string urn, CompleteTaskRequest request, CancellationToken ct)
    {
        var task = await tasks.FindAsync(urn, ct, track: true) ?? throw AdminException.NotFound("The task");
        var definition = await definitions.FindAsync(task.DefinitionUrn, ct)
            ?? throw new InvalidOperationException($"Task {urn} references an unknown definition");

        var user = GlobalUserId.Normalize(request.UserId) is { } gid ? await users.FindByGlobalUserIdAsync(gid, ct) : null;
        if (user is null || !user.Active)
            throw AdminException.Invalid(new AdminError("userId", $"unknown or inactive user '{request.UserId}'"));

        var code = request.Code
                   ?? definition.Responses.FirstOrDefault(r => r.Nature == "POSITIVE")?.Code
                   ?? definition.Responses.FirstOrDefault()?.Code
                   ?? throw AdminException.Invalid(new AdminError("code", "the task definition has no responses"));

        var now = time.GetUtcNow().UtcDateTime;
        try
        {
            task.Respond(definition, code, user.GlobalUserId, request.Comment, request.ReasonCode, now);
        }
        catch (TaskRuleViolation violation)
        {
            throw AdminException.FromRule(violation);
        }

        db.OperationLog.Add(new OperationLogEntry
        {
            TaskUrn = task.Urn, Kind = "RESPONSE", Code = code, Comment = request.Comment, ReasonCode = request.ReasonCode,
            UserId = user.GlobalUserId, At = TaskInstance.TruncateToMs(now), Outcome = "OK",
        });
        await SaveAsync(ct);
        return task;
    }

    public Task<TaskInstance> CancelAsync(string urn, CancellationToken ct) => LifecycleAsync(urn, (t, now) => t.Cancel(now), ct);
    public Task<TaskInstance> DeactivateAsync(string urn, CancellationToken ct) => LifecycleAsync(urn, (t, now) => t.Deactivate(now), ct);
    public Task<TaskInstance> ReactivateAsync(string urn, CancellationToken ct) => LifecycleAsync(urn, (t, now) => t.Reactivate(now), ct);

    private async Task<TaskInstance> LifecycleAsync(string urn, Action<TaskInstance, DateTime> change, CancellationToken ct)
    {
        var task = await tasks.FindAsync(urn, ct, track: true) ?? throw AdminException.NotFound("The task");
        try
        {
            change(task, time.GetUtcNow().UtcDateTime);
        }
        catch (TaskRuleViolation violation)
        {
            throw AdminException.FromRule(violation);
        }
        await SaveAsync(ct);
        return task;
    }

    // ---- generator ----------------------------------------------------------------------------

    public async Task<JsonObject> GenerateAsync(GenerateTasksRequest request, CancellationToken ct)
    {
        var errors = new List<AdminError>();
        if (request.Count is < 1 or > MaxGeneratedTasks) errors.Add(new("count", $"must be between 1 and {MaxGeneratedTasks}"));

        var defs = new List<TaskDefinition>();
        if (!string.IsNullOrWhiteSpace(request.DefinitionLocalId))
        {
            var one = await definitions.FindByLocalIdAsync(request.DefinitionLocalId, ct);
            if (one is null) errors.Add(new("definitionLocalId", $"unknown task definition '{request.DefinitionLocalId}'"));
            else defs.Add(one);
        }
        else
        {
            defs.AddRange(await definitions.ListAsync(0, 100, ct));
            if (defs.Count == 0) errors.Add(new("definitionLocalId", "no task definitions exist"));
        }

        var recipients = await ResolveRecipientsAsync(request.Recipients, errors, required: true, ct);
        if (errors.Count > 0) throw AdminException.Invalid(errors);

        var random = request.Seed is { } seed ? new Random(seed) : new Random();
        var now = TaskInstance.TruncateToMs(time.GetUtcNow().UtcDateTime);
        var perDefinition = Enumerable.Range(0, request.Count).GroupBy(_ => random.Next(defs.Count)).ToDictionary(g => g.Key, g => g.Count());

        var urns = new List<string>(request.Count);
        var batch = new List<TaskInstance>(500);
        var sequence = 0;
        foreach (var (index, count) in perDefinition)
        {
            var definition = defs[index];
            var ids = await localIds.NextBlockAsync(definition.LocalId, count, ct);
            foreach (var localId in ids)
            {
                var task = BuildRandomTask(definition, localId, recipients!, random, now, request.SameTimestamp, ++sequence);
                urns.Add(task.Urn);
                batch.Add(task);
                if (batch.Count == 500) await FlushAsync(batch, ct);
            }
        }
        await FlushAsync(batch, ct);

        var result = new JsonObject
        {
            ["created"] = urns.Count,
            ["definitions"] = new JsonArray(perDefinition.Select(p => (JsonNode)new JsonObject
                { ["localId"] = defs[p.Key].LocalId, ["count"] = p.Value }).ToArray()),
            ["firstUrn"] = urns.Order(StringComparer.Ordinal).First(),
            ["lastUrn"] = urns.Order(StringComparer.Ordinal).Last(),
        };
        if (request.SameTimestamp) result["modifiedAt"] = SpiMapper.Format(now);
        return result;
    }

    private async Task FlushAsync(List<TaskInstance> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        db.Tasks.AddRange(batch);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        batch.Clear();
    }

    private TaskInstance BuildRandomTask(TaskDefinition definition, string localId, ResolvedRecipients recipients, Random random,
        DateTime now, bool sameTimestamp, int sequence)
    {
        var urn = Identity.TaskUrn(localId).Value;
        var modified = sameTimestamp ? now : TaskInstance.TruncateToMs(now.AddMilliseconds(-random.Next(0, 3_600_000)));
        var (subjectEn, subjectDe, attributes) = RandomContent(definition.LocalId, localId, random, now);

        var task = new TaskInstance
        {
            Urn = urn, LocalId = localId, DefinitionUrn = definition.Urn, Status = TaskStatuses.Ready,
            Priority = TaskPriorities.All[random.Next(TaskPriorities.All.Count)],
            SubjectJson = SubjectToJson([new LocalizedText("en-US", subjectEn), new LocalizedText("de-DE", subjectDe)]),
            CreatedAt = modified, ModifiedAt = modified,
            DueAt = random.Next(3) == 0 ? null : TaskInstance.TruncateToMs(now.AddDays(random.Next(1, 30))),
        };
        foreach (var id in recipients.UserIds) task.RecipientUsers.Add(new TaskRecipientUser { TaskUrn = urn, GlobalUserId = id });
        foreach (var group in recipients.Groups) task.RecipientGroups.Add(new TaskRecipientGroup { TaskUrn = urn, GroupName = group });
        foreach (var (code, value) in attributes.Where(a => definition.FindAttribute(a.Key) is not null))
            task.CustomAttributes.Add(new TaskCustomAttribute { TaskUrn = urn, Code = code, Value = value });
        _ = sequence;
        return task;
    }

    private static readonly string[] Items = ["Dell Latitude 7450", "Lenovo ThinkPad T14", "Safety boots (pair)", "Hydraulic hose 2in", "Conveyor belt section", "Site laptop dock", "PPE starter kit", "Drill bits set"];
    private static readonly string[] Units = ["Mining Ops", "Smelter Maintenance", "Processing Plant", "Head Office IT", "Logistics", "Exploration"];
    private static readonly string[] Vendors = ["ACME Mining Supplies", "Rustenburg Tools", "Bushveld Hydraulics", "Sandton IT Distribution"];
    private static readonly string[] LeaveTypes = ["Annual", "Sick", "Study", "Family responsibility"];

    private static (string En, string De, Dictionary<string, string> Attributes) RandomContent(string definition, string localId, Random random, DateTime now)
    {
        var n = random.Next(1000, 9999);
        var suffix = $" ({localId})";
        switch (definition)
        {
            case "LEAVE_APPROVAL":
            {
                var type = LeaveTypes[random.Next(LeaveTypes.Length)];
                var days = random.Next(1, 15);
                var from = DateOnly.FromDateTime(now.AddDays(random.Next(5, 60)));
                return ($"Approve {type} leave request {n} - {days} days" + suffix, $"Urlaubsantrag {n} genehmigen - {days} Tage" + suffix,
                    new Dictionary<string, string>
                    {
                        ["leaveType"] = type, ["fromDate"] = from.ToString("yyyy-MM-dd"), ["toDate"] = from.AddDays(days - 1).ToString("yyyy-MM-dd"),
                        ["days"] = days.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    });
            }
            case "INVOICE_EXCEPTION":
            {
                var vendor = Vendors[random.Next(Vendors.Length)];
                var variance = Math.Round((random.NextDouble() - 0.4) * 5000, 2);
                return ($"Resolve invoice exception {n} - {vendor}" + suffix, $"Rechnungsausnahme {n} klaeren - {vendor}" + suffix,
                    new Dictionary<string, string>
                    {
                        ["vendor"] = vendor, ["invoiceNo"] = $"INV-{n}", ["variance"] = variance.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                        ["postingDate"] = DateOnly.FromDateTime(now.AddDays(-random.Next(1, 30))).ToString("yyyy-MM-dd"),
                    });
            }
            default:
            {
                var item = Items[random.Next(Items.Length)];
                var qty = random.Next(1, 6);
                var unit = Units[random.Next(Units.Length)];
                return ($"Approve PR {n} - {qty}x {item} for {unit}" + suffix, $"BANF {n} genehmigen - {qty}x {item} fuer {unit}" + suffix,
                    new Dictionary<string, string>
                    {
                        ["amount"] = Math.Round(random.NextDouble() * 90_000 + 500, 2).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                        ["currency"] = "ZAR", ["requester"] = $"Requester {random.Next(1, 200)}", ["costCenter"] = $"CC-{random.Next(1000, 9999)}",
                        ["neededBy"] = DateOnly.FromDateTime(now.AddDays(random.Next(3, 45))).ToString("yyyy-MM-dd"),
                    });
            }
        }
    }

    // ---- validation and resolution -------------------------------------------------------------

    public async Task<ResolvedRecipients?> ResolveRecipientsAsync(RecipientsDto? dto, List<AdminError> errors, bool required, CancellationToken ct)
    {
        var userIds = new List<string>();
        var groups = new List<string>();
        var before = errors.Count;

        foreach (var entry in (dto?.Users ?? []).Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            DirectoryUser? user = null;
            if (GlobalUserId.Normalize(entry) is { } gid) user = await users.FindByGlobalUserIdAsync(gid, ct);
            else if (entry.Contains('@', StringComparison.Ordinal)) user = await users.FindUniqueByEmailAsync(entry, ct);

            if (user is null) errors.Add(new("recipients.users", $"unknown user (or ambiguous e-mail) '{entry}'"));
            else if (!user.Active) errors.Add(new("recipients.users", $"user '{entry}' is inactive"));
            else if (!userIds.Contains(user.GlobalUserId)) userIds.Add(user.GlobalUserId);
        }

        foreach (var name in (dto?.Groups ?? []).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var canonical = await db.ScimGroups.AsNoTracking().Where(g => g.DisplayName == name).Select(g => g.DisplayName).FirstOrDefaultAsync(ct);
            if (canonical is null) errors.Add(new("recipients.groups", $"unknown group '{name}'"));
            else if (!groups.Contains(canonical, StringComparer.OrdinalIgnoreCase)) groups.Add(canonical);
        }

        if (required && userIds.Count == 0 && groups.Count == 0 && errors.Count == before)
            errors.Add(new("recipients", "at least one user or group is required"));
        return errors.Count == before ? new ResolvedRecipients(userIds, groups) : null;
    }

    private static List<LocalizedText>? ValidateSubject(Dictionary<string, string>? subject, List<AdminError> errors)
    {
        if (subject is null || subject.Count == 0)
        {
            errors.Add(new("subject", "at least one language is required, e.g. {\"en-US\": \"Approve PR 4711\"}"));
            return null;
        }
        var result = new List<LocalizedText>();
        foreach (var (language, text) in subject)
        {
            if (!LanguageTag().IsMatch(language)) { errors.Add(new($"subject.{language}", "language code must look like en-US")); continue; }
            if (string.IsNullOrWhiteSpace(text)) { errors.Add(new($"subject.{language}", "must not be empty")); continue; }
            if (text.Length > MaxSubjectLength) { errors.Add(new($"subject.{language}", $"must be at most {MaxSubjectLength} characters")); continue; }
            result.Add(new LocalizedText(language, text.Trim()));
        }
        return errors.Any(e => e.Field.StartsWith("subject", StringComparison.Ordinal)) ? null : result;
    }

    private static string? ValidatePriority(string? priority, List<AdminError> errors)
    {
        if (priority is null) return null;
        if (TaskPriorities.All.Contains(priority)) return priority;
        errors.Add(new("priority", $"must be one of {string.Join(", ", TaskPriorities.All)}"));
        return null;
    }

    private static Dictionary<string, string> ValidateAttributes(TaskDefinition definition, Dictionary<string, string?>? values, List<AdminError> errors)
    {
        var result = new Dictionary<string, string>();
        foreach (var (code, value) in values ?? [])
        {
            var def = definition.FindAttribute(code);
            if (def is null) { errors.Add(new($"customAttributes.{code}", "is not defined by the task definition")); continue; }
            if (!CustomAttributeValidator.TryNormalize(def.Type, value, out var normalized))
            {
                errors.Add(new($"customAttributes.{code}", $"'{value}' is not a valid {def.Type}"));
                continue;
            }
            result[code] = normalized;
        }
        return result;
    }

    private void ApplyAttributes(TaskInstance task, TaskDefinition definition, Dictionary<string, string?> values, List<AdminError> errors)
    {
        foreach (var (code, value) in values)
        {
            var def = definition.FindAttribute(code);
            if (def is null) { errors.Add(new($"customAttributes.{code}", "is not defined by the task definition")); continue; }

            var existing = task.CustomAttributes.FirstOrDefault(a => a.Code == code);
            if (value is null)
            {
                if (existing is not null) task.CustomAttributes.Remove(existing);
                continue;
            }
            if (!CustomAttributeValidator.TryNormalize(def.Type, value, out var normalized))
            {
                errors.Add(new($"customAttributes.{code}", $"'{value}' is not a valid {def.Type}"));
                continue;
            }
            if (existing is null) task.CustomAttributes.Add(new TaskCustomAttribute { TaskUrn = task.Urn, Code = code, Value = normalized });
            else existing.Value = normalized;
        }
    }

    private static void ApplyRecipients(TaskInstance task, ResolvedRecipients recipients)
    {
        foreach (var gone in task.RecipientUsers.Where(r => !recipients.UserIds.Contains(r.GlobalUserId)).ToList()) task.RecipientUsers.Remove(gone);
        foreach (var id in recipients.UserIds.Where(id => task.RecipientUsers.All(r => r.GlobalUserId != id)))
            task.RecipientUsers.Add(new TaskRecipientUser { TaskUrn = task.Urn, GlobalUserId = id });

        foreach (var gone in task.RecipientGroups.Where(r => !recipients.Groups.Contains(r.GroupName, StringComparer.OrdinalIgnoreCase)).ToList()) task.RecipientGroups.Remove(gone);
        foreach (var name in recipients.Groups.Where(n => task.RecipientGroups.All(r => !string.Equals(r.GroupName, n, StringComparison.OrdinalIgnoreCase))))
            task.RecipientGroups.Add(new TaskRecipientGroup { TaskUrn = task.Urn, GroupName = name });
    }

    private List<TaskDescription>? ValidateDescriptions(Dictionary<string, DescriptionDto>? map, List<AdminError> errors)
    {
        if (map is null || map.Count == 0) return null;
        var result = new List<TaskDescription>();
        foreach (var (language, dto) in map)
        {
            if (!LanguageTag().IsMatch(language)) { errors.Add(new($"description.{language}", "language code must look like en-US")); continue; }
            var body = dto.Body ?? string.Empty;
            if (body.Length > MaxDescriptionLength) { errors.Add(new($"description.{language}", $"must be at most {MaxDescriptionLength} characters")); continue; }

            var contentType = string.IsNullOrWhiteSpace(dto.ContentType) ? "text/html" : dto.ContentType.Trim().ToLowerInvariant();
            if (contentType is not ("text/html" or "text/plain")) { errors.Add(new($"description.{language}.contentType", "must be text/html or text/plain")); continue; }

            result.Add(new TaskDescription(language, contentType, contentType == "text/html" ? sanitizer.Sanitize(body) : body));
        }
        return result.Count == 0 ? null : result;
    }

    private async Task<List<LocalizedText>> EnsureUniqueSubjectAsync(List<LocalizedText> subject, string localId, string? excludeUrn, CancellationToken ct)
    {
        var primary = subject.FirstOrDefault(s => s.LanguageCode == provider.Value.DefaultLanguage) ?? subject[0];
        var fragment = new JsonObject { ["languageCode"] = primary.LanguageCode, ["text"] = primary.Text }.ToJsonString();
        var taken = await db.Tasks.AsNoTracking().AnyAsync(t =>
            (excludeUrn == null || t.Urn != excludeUrn) && EF.Functions.Like(t.SubjectJson, SqlLike.Contains(fragment), SqlLike.EscapeCharacter), ct);
        if (!taken) return subject;

        // Subjects must be unique and meaningful (digest section 6): append the business number.
        var suffix = $" ({localId})";
        return subject.Select(s => new LocalizedText(s.LanguageCode,
            (s.Text.Length + suffix.Length > MaxSubjectLength ? s.Text[..(MaxSubjectLength - suffix.Length)] : s.Text) + suffix)).ToList();
    }

    // ---- JSON helpers ---------------------------------------------------------------------------

    public static string SubjectToJson(IEnumerable<LocalizedText> subject) =>
        new JsonArray(subject.Select(s => (JsonNode)new JsonObject { ["languageCode"] = s.LanguageCode, ["text"] = s.Text }).ToArray()).ToJsonString();

    public static string DescriptionsToJson(IEnumerable<TaskDescription> descriptions) =>
        new JsonArray(descriptions.Select(d => (JsonNode)new JsonObject
        {
            ["languageCode"] = d.LanguageCode, ["contentType"] = d.ContentType, ["body"] = d.Body,
        }).ToArray()).ToJsonString();

    private static Dictionary<string, string>? ReadStringMap(JsonNode? node, string field, List<AdminError> errors)
    {
        if (node is not JsonObject obj) { errors.Add(new(field, "must be an object")); return null; }
        var map = new Dictionary<string, string>();
        foreach (var (key, value) in obj)
        {
            if (value is JsonValue v && v.TryGetValue<string>(out var s)) map[key] = s;
            else errors.Add(new($"{field}.{key}", "must be a string"));
        }
        return map;
    }

    private static Dictionary<string, DescriptionDto>? ReadDescriptionMap(JsonNode? node, List<AdminError> errors)
    {
        if (node is null) return null;
        if (node is not JsonObject obj) { errors.Add(new("description", "must be an object")); return null; }
        var map = new Dictionary<string, DescriptionDto>();
        foreach (var (key, value) in obj)
        {
            if (value is JsonObject d) map[key] = new DescriptionDto(AdminJson.String(d, "contentType"), AdminJson.String(d, "body"));
            else errors.Add(new($"description.{key}", "must be an object with contentType and body"));
        }
        return map;
    }

    private static List<string>? ReadStringList(JsonNode? node) =>
        (node as JsonArray)?.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty).ToList();

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw AdminException.FromRule(new TaskRuleViolation(SpiCodes.ConcurrentUpdate));
        }
    }
}
