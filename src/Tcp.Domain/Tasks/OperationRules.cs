namespace Tcp.Domain.Tasks;

/// <summary>Computes <c>validResponseCodes</c>/<c>validActionCodes</c> and checks comment/reason requirements.</summary>
public static class OperationRules
{
    /// <summary>
    /// <c>null</c> for open tasks (every response of the definition is valid), <c>[]</c> once the task is final.
    /// </summary>
    public static IReadOnlyList<string>? ValidResponseCodes(TaskInstance task, TaskDefinition definition) =>
        task.IsFinal ? [] : null;

    /// <summary>
    /// US-004-4.7: <c>claim</c> iff unassigned and READY; <c>release</c> iff assigned; <c>increasePriority</c> iff not
    /// VERY_HIGH; nothing once final. Only actions that the definition declares are returned.
    /// </summary>
    public static IReadOnlyList<string> ValidActionCodes(TaskInstance task, TaskDefinition definition)
    {
        if (task.IsFinal) return [];

        var valid = new List<string>();
        foreach (var action in definition.Actions)
        {
            var ok = action.Code switch
            {
                ActionCodes.Claim => task.Processor is null && task.Status == TaskStatuses.Ready,
                ActionCodes.Release => task.Processor is not null,
                ActionCodes.IncreasePriority => task.Priority != TaskPriorities.VeryHigh,
                _ => false,
            };
            if (ok) valid.Add(action.Code);
        }
        return valid;
    }

    public static void CheckCommentAndReason(OperationDefinition operation, string? comment, string? reasonCode)
    {
        if (operation.CommentRequired == "REQUIRED" && string.IsNullOrWhiteSpace(comment))
            throw new TaskRuleViolation(SpiCodes.CommentRequired, "comment");

        if (operation.ReasonRequired == "UNSUPPORTED") return;

        if (string.IsNullOrWhiteSpace(reasonCode))
        {
            if (operation.ReasonRequired == "REQUIRED") throw new TaskRuleViolation(SpiCodes.ReasonRequired, "reasonCode");
            return;
        }

        if (operation.PossibleReasons.All(r => r.Code != reasonCode))
            throw new TaskRuleViolation(SpiCodes.InvalidReason, "reasonCode");
    }
}
