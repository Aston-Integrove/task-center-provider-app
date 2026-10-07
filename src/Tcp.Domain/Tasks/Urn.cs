using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Tcp.Domain.Tasks;

public enum UrnKind
{
    Task,
    TaskDefinition,
}

/// <summary>
/// <c>urn:sap.odm.bpm.task:{ApplicationId}:{ApplicationInstanceId}:{TenantId}:{LocalId}</c> and the
/// <c>taskdefinition</c> variant (FR-SPI-01). Parts and the local id are restricted to <c>[A-Za-z0-9_.-]</c>,
/// at most 64 characters each; the whole URN at most 300.
/// </summary>
public sealed partial record Urn(UrnKind Kind, string ApplicationId, string ApplicationInstanceId, string TenantId, string LocalId)
{
    public const int MaxLength = 300;
    public const int MaxPartLength = 64;
    public const string TaskPrefix = "urn:sap.odm.bpm.task:";
    public const string TaskDefinitionPrefix = "urn:sap.odm.bpm.taskdefinition:";

    [GeneratedRegex("^[A-Za-z0-9_.-]+$")]
    private static partial Regex PartPattern();

    public static string PrefixOf(UrnKind kind) => kind == UrnKind.Task ? TaskPrefix : TaskDefinitionPrefix;

    public string Value => $"{PrefixOf(Kind)}{ApplicationId}:{ApplicationInstanceId}:{TenantId}:{LocalId}";

    public override string ToString() => Value;

    public static Urn Build(UrnKind kind, string applicationId, string applicationInstanceId, string tenantId, string localId)
    {
        var urn = new Urn(kind, applicationId, applicationInstanceId, tenantId, localId);
        return urn.IsValid(out var error) ? urn : throw new ArgumentException(error);
    }

    public static bool TryParse(string? value, [NotNullWhen(true)] out Urn? urn)
    {
        urn = null;
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength) return false;

        UrnKind kind;
        string rest;
        if (value.StartsWith(TaskDefinitionPrefix, StringComparison.Ordinal))
        {
            kind = UrnKind.TaskDefinition;
            rest = value[TaskDefinitionPrefix.Length..];
        }
        else if (value.StartsWith(TaskPrefix, StringComparison.Ordinal))
        {
            kind = UrnKind.Task;
            rest = value[TaskPrefix.Length..];
        }
        else
        {
            return false;
        }

        var parts = rest.Split(':');
        if (parts.Length != 4) return false;

        var candidate = new Urn(kind, parts[0], parts[1], parts[2], parts[3]);
        if (!candidate.IsValid(out _)) return false;
        urn = candidate;
        return true;
    }

    public bool IsValid(out string error)
    {
        foreach (var (name, part) in new[]
                 {
                     ("applicationId", ApplicationId), ("applicationInstanceId", ApplicationInstanceId),
                     ("tenantId", TenantId), ("localId", LocalId),
                 })
        {
            if (string.IsNullOrEmpty(part) || part.Length > MaxPartLength || !PartPattern().IsMatch(part))
            {
                error = $"{name} must be 1-{MaxPartLength} characters of [A-Za-z0-9_.-]";
                return false;
            }
        }

        if (Value.Length > MaxLength)
        {
            error = $"URN is longer than {MaxLength} characters";
            return false;
        }
        error = string.Empty;
        return true;
    }
}
