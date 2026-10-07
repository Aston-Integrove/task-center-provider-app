using Microsoft.EntityFrameworkCore;
using Tcp.Domain.Identity;
using Tcp.Domain.Tasks;
using Tcp.Infrastructure.Persistence;
using Tcp.Infrastructure.Tasks;

namespace Tcp.Api.Endpoints.Spi;

public enum OperationKind
{
    Response,
    Action,
}

public sealed record OperationRequest(string Code, string? Comment, string? ReasonCode);

/// <summary>
/// Executes a response or action on behalf of a user: load, entitlement, rules, mutation, audit log. Optimistic
/// concurrency (rowversion) is retried once against the fresh state; a second conflict is
/// <c>409 tcp.spi.concurrentUpdate</c>. Every attempt, successful or not, is recorded in <c>tc.OperationLog</c>.
/// </summary>
public sealed class OperationService(
    TcpDbContext db,
    TaskRepository tasks,
    DefinitionRepository definitions,
    IUserDirectory users,
    TimeProvider time,
    Microsoft.Extensions.Options.IOptions<Configuration.SpiOptions> spiOptions,
    IPendingResponseQueue pending,
    ILogger<OperationService> logger)
{
    /// <returns>The updated task, or <c>null</c> when the response was accepted for asynchronous completion (202).</returns>
    public async Task<TaskInstance?> ExecuteAsync(
        OperationKind kind, string taskUrn, string userId, OperationRequest request, CancellationToken ct, bool allowAsync = true)
    {
        for (var attempt = 0; ; attempt++)
        {
            db.ChangeTracker.Clear();
            var task = await tasks.FindAsync(taskUrn, ct, track: true);
            if (task is null)
            {
                await LogAsync(kind, taskUrn, userId, request, "REJECTED", SpiCodes.TaskNotFound, ct);
                throw new TaskRuleViolation(SpiCodes.TaskNotFound);
            }

            try
            {
                var definition = await definitions.FindAsync(task.DefinitionUrn, ct)
                    ?? throw new InvalidOperationException($"Task {task.Urn} references unknown definition {task.DefinitionUrn}");

                await CheckEntitlementAsync(task, userId, ct);

                var now = time.GetUtcNow().UtcDateTime;
                if (kind == OperationKind.Response)
                    task.Respond(definition, request.Code, userId, request.Comment, request.ReasonCode, now);
                else
                    task.ExecuteAction(definition, request.Code, userId, request.Comment, request.ReasonCode, now);

                if (kind == OperationKind.Response && allowAsync && spiOptions.Value.AsyncResponses)
                {
                    // Validated against the real state; completion happens later (US-004-7.9). Discard the in-memory change.
                    db.ChangeTracker.Clear();
                    await LogAsync(kind, taskUrn, userId, request, "ACCEPTED", null, ct);
                    pending.Enqueue(new PendingResponse(taskUrn, userId, request));
                    return null;
                }

                db.OperationLog.Add(NewLog(kind, taskUrn, userId, request, "OK", null, now));
                await db.SaveChangesAsync(ct);
                return task;
            }
            catch (TaskRuleViolation violation)
            {
                await LogAsync(kind, taskUrn, userId, request, "REJECTED", violation.Code, ct);
                throw;
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                logger.LogInformation("Concurrent update on {TaskUrn}; re-evaluating against the fresh state", taskUrn);
            }
            catch (DbUpdateConcurrencyException)
            {
                await LogAsync(kind, taskUrn, userId, request, "REJECTED", SpiCodes.ConcurrentUpdate, ct);
                throw new TaskRuleViolation(SpiCodes.ConcurrentUpdate);
            }
        }
    }

    /// <summary>FR-SPI-AUTH. Throws notAuthorized / reservedByOther.</summary>
    public async Task CheckEntitlementAsync(TaskInstance task, string userId, CancellationToken ct)
    {
        var user = await users.FindByGlobalUserIdAsync(userId, ct);
        var groups = task.RecipientGroups.Select(g => g.GroupName).ToList();
        var isRecipient = task.RecipientUsers.Any(r => r.GlobalUserId == userId);
        var isGroupMember = user is not null && groups.Count > 0 && await users.IsMemberOfAnyGroupAsync(userId, groups, ct);

        var result = Entitlement.Evaluate(task.Processor, userId, isRecipient, isGroupMember, user is { Active: true });
        if (result == EntitlementResult.NotAuthorized) throw new TaskRuleViolation(SpiCodes.NotAuthorized);
        if (result == EntitlementResult.ReservedByOther) throw new TaskRuleViolation(SpiCodes.ReservedByOther);
    }

    private async Task LogAsync(OperationKind kind, string taskUrn, string userId, OperationRequest request, string outcome, string? errorCode, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        db.OperationLog.Add(NewLog(kind, taskUrn, userId, request, outcome, errorCode, time.GetUtcNow().UtcDateTime));
        await db.SaveChangesAsync(ct);
    }

    private static OperationLogEntry NewLog(OperationKind kind, string taskUrn, string userId, OperationRequest request, string outcome, string? errorCode, DateTime now) => new()
    {
        TaskUrn = taskUrn.Length > 300 ? taskUrn[..300] : taskUrn,
        Kind = kind == OperationKind.Response ? "RESPONSE" : "ACTION",
        Code = request.Code.Length > 64 ? request.Code[..64] : request.Code,
        Comment = request.Comment is { Length: > 2000 } c ? c[..2000] : request.Comment,
        ReasonCode = request.ReasonCode is { Length: > 64 } r ? r[..64] : request.ReasonCode,
        UserId = userId.Length > 64 ? userId[..64] : userId,
        At = TaskInstance.TruncateToMs(now),
        Outcome = outcome,
        ErrorCode = errorCode,
    };
}
