namespace Tcp.Domain.Tasks;

public class TaskRecipientUser
{
    public string TaskUrn { get; set; } = "";
    public string GlobalUserId { get; set; } = "";
}

public class TaskRecipientGroup
{
    public string TaskUrn { get; set; } = "";
    public string GroupName { get; set; } = "";
}

public class TaskCustomAttribute
{
    public string TaskUrn { get; set; } = "";
    public string Code { get; set; } = "";
    public string Value { get; set; } = "";
}

public class TaskOperationError
{
    public long Id { get; set; }
    public string TaskUrn { get; set; } = "";
    public DateTime ExecutedAt { get; set; }
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string ExecutedBy { get; set; } = "";
}

public class OperationLogEntry
{
    public long Id { get; set; }
    public string TaskUrn { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Code { get; set; } = "";
    public string? Comment { get; set; }
    public string? ReasonCode { get; set; }
    public string UserId { get; set; } = "";
    public DateTime At { get; set; }
    public string Outcome { get; set; } = "";
    public string? ErrorCode { get; set; }
}

public class LocalIdSequence
{
    public string DefinitionLocalId { get; set; } = "";
    public DateTime Day { get; set; }
    public int Next { get; set; }
}

/// <summary>
/// A task (table tc.TaskInstance). Behaviour methods enforce the SPI rules; the entitlement check
/// (<see cref="Entitlement"/>) is done by the caller because it needs the user directory.
/// Every mutation goes through <see cref="Touch"/> so <c>ModifiedAt</c> is monotonic per task (FR-SPI-03).
/// </summary>
public class TaskInstance
{
    public string Urn { get; set; } = "";
    public string LocalId { get; set; } = "";
    public string DefinitionUrn { get; set; } = "";
    public string Status { get; set; } = TaskStatuses.Ready;
    public string Priority { get; set; } = TaskPriorities.Medium;

    /// <summary>LocalizedText list: <c>[{"languageCode","text"}]</c>.</summary>
    public string SubjectJson { get; set; } = "[]";

    /// <summary>Descriptions: <c>[{"languageCode","contentType","body"}]</c>.</summary>
    public string? DescriptionJson { get; set; }

    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime ModifiedAt { get; set; }
    public string? ModifiedBy { get; set; }
    public string? Processor { get; set; }
    public DateTime? DueAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? CompletedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<TaskRecipientUser> RecipientUsers { get; set; } = [];
    public ICollection<TaskRecipientGroup> RecipientGroups { get; set; } = [];
    public ICollection<TaskCustomAttribute> CustomAttributes { get; set; } = [];
    public ICollection<TaskOperationError> OperationErrors { get; set; } = [];

    public bool IsFinal => TaskStatuses.IsFinal(Status);

    /// <summary>UTC, truncated to milliseconds so equality in the pull query is exact (constitution VII).</summary>
    public static DateTime TruncateToMs(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    /// <summary>Sets <c>ModifiedAt</c>: now, or previous + 1 ms if the clock did not advance.</summary>
    public void Touch(DateTime nowUtc, string? modifiedBy)
    {
        var now = TruncateToMs(nowUtc);
        ModifiedAt = now > ModifiedAt ? now : DateTime.SpecifyKind(ModifiedAt, DateTimeKind.Utc).AddMilliseconds(1);
        ModifiedBy = modifiedBy;
    }

    /// <summary>Executes a response (approve/reject/...): finalises the task as COMPLETED.</summary>
    public void Respond(TaskDefinition definition, string code, string userId, string? comment, string? reasonCode, DateTime nowUtc)
    {
        if (IsFinal) throw new TaskRuleViolation(SpiCodes.TaskFinal);

        var response = definition.FindResponse(code)
            ?? throw new TaskRuleViolation(SpiCodes.InvalidOperation, "code");
        OperationRules.CheckCommentAndReason(response, comment, reasonCode);

        var now = TruncateToMs(nowUtc);
        Status = TaskStatuses.Completed;
        Processor = userId;
        CompletedBy = userId;
        CompletedAt = now;
        Touch(now, userId);
    }

    /// <summary>Executes a non-finalising action: claim, release or increasePriority.</summary>
    public void ExecuteAction(TaskDefinition definition, string code, string userId, string? comment, string? reasonCode, DateTime nowUtc)
    {
        if (IsFinal) throw new TaskRuleViolation(SpiCodes.TaskFinal);

        var action = definition.FindAction(code)
            ?? throw new TaskRuleViolation(SpiCodes.InvalidOperation, "code");
        OperationRules.CheckCommentAndReason(action, comment, reasonCode);

        switch (code)
        {
            case ActionCodes.Claim:
                if (Processor is not null && Processor != userId) throw new TaskRuleViolation(SpiCodes.ReservedByOther);
                if (Processor is not null || Status != TaskStatuses.Ready) throw new TaskRuleViolation(SpiCodes.ActionNotValid);
                Processor = userId;
                Status = TaskStatuses.Reserved;
                break;

            case ActionCodes.Release:
                if (Processor is null) throw new TaskRuleViolation(SpiCodes.ActionNotValid);
                if (Processor != userId) throw new TaskRuleViolation(SpiCodes.ReservedByOther);
                Processor = null;
                Status = TaskStatuses.Ready;
                break;

            case ActionCodes.IncreasePriority:
                Priority = TaskPriorities.Raise(Priority) ?? throw new TaskRuleViolation(SpiCodes.ActionNotValid);
                break;

            default:
                // Defined by the task definition but not something this provider knows how to execute.
                throw new TaskRuleViolation(SpiCodes.InvalidOperation, "code");
        }

        Touch(nowUtc, userId);
    }

    /// <summary>Tombstone: GDPR forbids hard deletes while Task Center may hold the task (constitution VII).</summary>
    public void Cancel(DateTime nowUtc, string? modifiedBy = null)
    {
        if (IsFinal) throw new TaskRuleViolation(SpiCodes.TaskFinal);
        Status = TaskStatuses.Canceled;
        Processor = null;
        Touch(nowUtc, modifiedBy);
    }
}

public static class ActionCodes
{
    public const string Claim = "claim";
    public const string Release = "release";
    public const string IncreasePriority = "increasePriority";
}
