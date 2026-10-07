using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;
using Tcp.Api.Configuration;
using Tcp.Domain.Tasks;
using Tcp.Infrastructure.Tasks;

namespace Tcp.Api.Endpoints.Spi;

/// <summary>Loads what the mapper needs (definitions, active users) with set-based queries and maps tasks to SAP JSON.</summary>
public sealed class SpiTaskService(
    TaskRepository tasks,
    DefinitionRepository definitions,
    IOptions<ProviderOptions> provider,
    ILogger<SpiTaskService> logger)
{
    public SpiContext Context(HttpContext http, IReadOnlyList<string> languages)
    {
        var p = provider.Value;
        var baseUrl = string.IsNullOrWhiteSpace(p.PublicBaseUrl)
            ? $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}"
            : p.PublicBaseUrl.TrimEnd('/');
        return new SpiContext(p.ApplicationId, p.ApplicationInstanceId, p.TenantId, baseUrl, p.DefaultLanguage, languages);
    }

    public async Task<JsonArray> MapAsync(IReadOnlyList<TaskInstance> page, SpiContext ctx, CancellationToken ct)
    {
        if (page.Count == 0) return [];

        var defs = await definitions.FindManyAsync(page.Select(t => t.DefinitionUrn), ct);
        var userIds = page.SelectMany(t => t.RecipientUsers.Select(r => r.GlobalUserId));
        var active = await tasks.ActiveUserIdsAsync(userIds, ct);

        return new JsonArray(page.Select(t => (JsonNode)SpiMapper.Task(
            t, defs.GetValueOrDefault(t.DefinitionUrn), ctx, active,
            (code, type) => logger.LogWarning("Dropped invalid custom attribute {Code} ({Type}) of task {TaskUrn}", code, type, t.Urn))).ToArray());
    }

    public async Task<JsonObject> MapOneAsync(TaskInstance task, SpiContext ctx, CancellationToken ct) =>
        (JsonObject)(await MapAsync([task], ctx, ct))[0]!.DeepClone();
}
