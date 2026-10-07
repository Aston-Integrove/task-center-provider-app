namespace Tcp.Domain.Tasks;

public static class TaskStatuses
{
    public const string Ready = "READY";
    public const string Reserved = "RESERVED";
    public const string InProgress = "IN_PROGRESS";
    public const string ForResubmission = "FOR_RESUBMISSION";
    public const string Inactive = "INACTIVE";
    public const string Completed = "COMPLETED";
    public const string Canceled = "CANCELED";

    public static readonly IReadOnlyList<string> All =
        [Ready, Reserved, InProgress, ForResubmission, Inactive, Completed, Canceled];

    public static bool IsFinal(string status) => status is Completed or Canceled;
}

public static class TaskPriorities
{
    public const string VeryHigh = "VERY_HIGH";
    public const string High = "HIGH";
    public const string Medium = "MEDIUM";
    public const string Low = "LOW";

    public static readonly IReadOnlyList<string> All = [VeryHigh, High, Medium, Low];

    /// <summary>LOW -> MEDIUM -> HIGH -> VERY_HIGH; null at the top.</summary>
    public static string? Raise(string priority) => priority switch
    {
        Low => Medium,
        Medium => High,
        High => VeryHigh,
        _ => null,
    };
}

/// <summary>SPI error codes (<c>tcp.&lt;area&gt;.&lt;reason&gt;</c>, FR-SPI-04).</summary>
public static class SpiCodes
{
    public const string InvalidParameter = "tcp.spi.invalidParameter";
    public const string TaskDefinitionNotFound = "tcp.spi.taskDefinitionNotFound";
    public const string TaskNotFound = "tcp.spi.taskNotFound";
    public const string DescriptionNotFound = "tcp.spi.descriptionNotFound";
    public const string CommentRequired = "tcp.spi.commentRequired";
    public const string ReasonRequired = "tcp.spi.reasonRequired";
    public const string InvalidReason = "tcp.spi.invalidReason";
    public const string InvalidOperation = "tcp.spi.invalidOperation";
    public const string TaskFinal = "tcp.spi.taskFinal";
    public const string ActionNotValid = "tcp.spi.actionNotValid";
    public const string ReservedByOther = "tcp.spi.reservedByOther";
    public const string NotAuthorized = "tcp.spi.notAuthorized";
    public const string ConcurrentUpdate = "tcp.spi.concurrentUpdate";
    public const string NotImplemented = "tcp.spi.notImplemented";
    public const string InternalError = "tcp.spi.internalError";
    public const string UserContextRequired = "tcp.auth.userContextRequired";
    public const string Forbidden = "tcp.auth.forbidden";
    public const string Unauthorized = "tcp.auth.unauthorized";
}

/// <summary>A business-rule rejection carrying the SPI error code to return.</summary>
public sealed class TaskRuleViolation(string code, string? target = null, string? detail = null) : Exception(detail ?? code)
{
    public string Code { get; } = code;

    /// <summary>The offending parameter/property for <see cref="SpiCodes.InvalidParameter"/> style errors.</summary>
    public string? Target { get; } = target;
}

public enum EntitlementResult
{
    Entitled,
    NotAuthorized,
    ReservedByOther,
}

/// <summary>
/// FR-SPI-AUTH: a user may read the description or act on a task if they are a recipient (directly or through a
/// group) and the task is not reserved by somebody else. Inactive or deleted users are never entitled.
/// </summary>
public static class Entitlement
{
    public static EntitlementResult Evaluate(string? processor, string userId, bool isRecipient, bool isGroupMember, bool userActive)
    {
        if (!userActive) return EntitlementResult.NotAuthorized;

        var isProcessor = processor is not null && string.Equals(processor, userId, StringComparison.Ordinal);
        if (!isRecipient && !isGroupMember && !isProcessor) return EntitlementResult.NotAuthorized;

        return processor is not null && !isProcessor ? EntitlementResult.ReservedByOther : EntitlementResult.Entitled;
    }
}
