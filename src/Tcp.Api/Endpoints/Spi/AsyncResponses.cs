using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tcp.Api.Configuration;
using Tcp.Domain.Tasks;
using Tcp.Infrastructure.Persistence;

namespace Tcp.Api.Endpoints.Spi;

public sealed record PendingResponse(string TaskUrn, string UserId, OperationRequest Request);

public interface IPendingResponseQueue
{
    void Enqueue(PendingResponse response);
    IAsyncEnumerable<PendingResponse> ReadAllAsync(CancellationToken ct);
}

public sealed class PendingResponseQueue : IPendingResponseQueue
{
    private readonly Channel<PendingResponse> _channel = Channel.CreateUnbounded<PendingResponse>();

    public void Enqueue(PendingResponse response) => _channel.Writer.TryWrite(response);
    public IAsyncEnumerable<PendingResponse> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// <c>Spi:AsyncResponses</c> (P3, US-004-7.9): responses are accepted with 202 and completed after
/// <c>Spi:AsyncDelaySeconds</c>. Codes in <c>Spi:SimulateFailureCodes</c> fail instead and surface as
/// <c>operationErrors</c> on the task, which lets Task Center's "Failed Tasks" handling be exercised.
/// </summary>
public sealed class AsyncResponseProcessor(
    IPendingResponseQueue queue,
    IServiceScopeFactory scopes,
    IOptions<SpiOptions> options,
    TimeProvider time,
    ILogger<AsyncResponseProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var pending in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, options.Value.AsyncDelaySeconds)), time, stoppingToken);
                await ProcessAsync(pending, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Async response for {TaskUrn} failed", pending.TaskUrn);
            }
        }
    }

    internal async Task ProcessAsync(PendingResponse pending, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TcpDbContext>();

        if (options.Value.SimulateFailureCodes.Contains(pending.Request.Code, StringComparer.Ordinal))
        {
            var task = await db.Tasks.FirstOrDefaultAsync(t => t.Urn == pending.TaskUrn, ct);
            if (task is null) return;

            var now = time.GetUtcNow().UtcDateTime;
            db.TaskOperationErrors.Add(new TaskOperationError
            {
                TaskUrn = task.Urn,
                ExecutedAt = TaskInstance.TruncateToMs(now),
                Code = pending.Request.Code,
                Message = $"Simulated failure of '{pending.Request.Code}'",
                ExecutedBy = pending.UserId,
            });
            task.Touch(now, pending.UserId); // re-publish so the next delta pull carries operationErrors
            await db.SaveChangesAsync(ct);
            return;
        }

        var operations = scope.ServiceProvider.GetRequiredService<OperationService>();
        try
        {
            await operations.ExecuteAsync(OperationKind.Response, pending.TaskUrn, pending.UserId, pending.Request, ct, allowAsync: false);
        }
        catch (TaskRuleViolation violation)
        {
            // State changed between 202 and now (e.g. somebody else completed it); the rejection is in the operation log.
            logger.LogWarning("Async response for {TaskUrn} rejected: {Code}", pending.TaskUrn, violation.Code);
        }
    }
}
