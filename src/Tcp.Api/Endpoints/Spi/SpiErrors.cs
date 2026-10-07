using System.Text.Json.Serialization;

namespace Tcp.Api.Endpoints.Spi;

public static class SpiPaths
{
    public static readonly string[] Prefixes = ["/task-provider/v2", "/api/task-provider/v2"];

    public static bool IsSpi(PathString path) =>
        Prefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));
}

/// <summary>SAP <c>Error</c> body (TaskProviderV2.json): <c>{"error":{"code","message","target","details"}}</c>.</summary>
public sealed record SpiErrorDetail(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("target")] string? Target,
    [property: JsonPropertyName("details")] IReadOnlyList<SpiErrorDetail> Details);

public sealed record SpiErrorBody([property: JsonPropertyName("error")] SpiErrorDetail Error);

public static class SpiErrors
{
    public static SpiErrorBody Body(string code, string message, string? target = null) =>
        new(new SpiErrorDetail(code, message, target, []));

    public static Task WriteAsync(HttpContext context, int status, string code, string message, string? target = null)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(Body(code, message, target));
    }
}
