using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tcp.Domain.Tasks;
using Tcp.Infrastructure.Persistence;

namespace Tcp.Infrastructure.Tasks;

/// <summary>The URN triple that identifies this provider instance (configuration <c>Provider:*</c>).</summary>
public sealed record ProviderIdentity(string ApplicationId, string ApplicationInstanceId, string TenantId)
{
    public Urn TaskDefinitionUrn(string localId) => Urn.Build(UrnKind.TaskDefinition, ApplicationId, ApplicationInstanceId, TenantId, localId);
    public Urn TaskUrn(string localId) => Urn.Build(UrnKind.Task, ApplicationId, ApplicationInstanceId, TenantId, localId);
}

public sealed record SeedResult(int Created, int Updated, int Unchanged);

/// <summary>
/// FR-SPI-07: upserts the seed task definitions by URN. Idempotent — an unchanged definition is not touched, and a
/// changed one only updates its own row (tasks are never bumped by a definition change).
/// </summary>
public sealed class DefinitionSeeder(TcpDbContext db, TimeProvider time, ILogger<DefinitionSeeder> logger)
{
    public async Task<SeedResult> SeedAsync(string json, ProviderIdentity identity, CancellationToken ct)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException("Seed file must be a JSON object");
        var items = root["value"] as JsonArray ?? throw new InvalidOperationException("Seed file needs a 'value' array");

        int created = 0, updated = 0, unchanged = 0;
        var now = TaskInstance.TruncateToMs(time.GetUtcNow().UtcDateTime);

        foreach (var node in items)
        {
            var item = node as JsonObject ?? throw new InvalidOperationException("Each seed definition must be an object");
            var localId = item["localId"]?.GetValue<string>() ?? throw new InvalidOperationException("Seed definition has no localId");
            if (item["name"] is not JsonArray { Count: > 0 })
                throw new InvalidOperationException($"Seed definition {localId} needs a non-empty name[]");

            var urn = identity.TaskDefinitionUrn(localId).Value;
            var row = new TaskDefinitionEntity
            {
                Urn = urn,
                LocalId = localId,
                NameJson = item["name"]!.ToJsonString(),
                ResponsesJson = (item["possibleResponses"] ?? new JsonArray()).ToJsonString(),
                ActionsJson = (item["possibleActions"] ?? new JsonArray()).ToJsonString(),
                CustomAttributesJson = (item["customAttributes"] ?? new JsonArray()).ToJsonString(),
                CapabilitiesJson = (item["capabilities"] ?? new JsonArray()).ToJsonString(),
                TaskDetailsSettingsJson = item["taskDetailsSettings"]?.ToJsonString(),
            };

            var existing = await db.TaskDefinitions.FirstOrDefaultAsync(d => d.Urn == urn, ct);
            if (existing is null)
            {
                row.ModifiedAt = now;
                db.TaskDefinitions.Add(row);
                created++;
            }
            else if (Differs(existing, row))
            {
                existing.LocalId = row.LocalId;
                existing.NameJson = row.NameJson;
                existing.ResponsesJson = row.ResponsesJson;
                existing.ActionsJson = row.ActionsJson;
                existing.CustomAttributesJson = row.CustomAttributesJson;
                existing.CapabilitiesJson = row.CapabilitiesJson;
                existing.TaskDetailsSettingsJson = row.TaskDetailsSettingsJson;
                existing.ModifiedAt = now;
                updated++;
            }
            else
            {
                unchanged++;
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Task definitions seeded: {Created} created, {Updated} updated, {Unchanged} unchanged", created, updated, unchanged);
        return new SeedResult(created, updated, unchanged);
    }

    private static bool Differs(TaskDefinitionEntity a, TaskDefinitionEntity b) =>
        a.LocalId != b.LocalId || a.NameJson != b.NameJson || a.ResponsesJson != b.ResponsesJson ||
        a.ActionsJson != b.ActionsJson || a.CustomAttributesJson != b.CustomAttributesJson ||
        a.CapabilitiesJson != b.CapabilitiesJson || a.TaskDetailsSettingsJson != b.TaskDetailsSettingsJson;
}
