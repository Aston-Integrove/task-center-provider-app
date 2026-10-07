using System.Globalization;
using System.Resources;
using System.Text.Json.Serialization;
using Microsoft.Net.Http.Headers;
using Tcp.Domain.Tasks;

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

/// <summary>Localised (en-US, de-DE) SPI messages from <c>Resources/SpiMessages*.resx</c> (FR-SPI-04).</summary>
public static class SpiMessages
{
    private static readonly ResourceManager Resources = new("Tcp.Api.Resources.SpiMessages", typeof(SpiMessages).Assembly);
    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    public static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static string Get(string code, CultureInfo culture, params object[] args)
    {
        var template = Resources.GetString(code, culture) ?? Resources.GetString(code, English) ?? code;
        return args.Length == 0 ? template : string.Format(CultureInfo.InvariantCulture, template, args);
    }

    /// <summary>First supported language of <c>Accept-Language</c> by quality; German or English (fallback).</summary>
    public static CultureInfo CultureFor(HttpRequest request)
    {
        IList<StringWithQualityHeaderValue> accepted;
        try { accepted = request.GetTypedHeaders().AcceptLanguage; }
        catch (FormatException) { return English; }

        foreach (var entry in accepted.OrderByDescending(a => a.Quality ?? 1.0))
        {
            var value = entry.Value.Value ?? string.Empty;
            if (value.StartsWith("de", StringComparison.OrdinalIgnoreCase)) return German;
            if (value.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return English;
        }
        return English;
    }
}

public static class SpiErrors
{
    public static int StatusFor(string code) => code switch
    {
        SpiCodes.InvalidParameter or SpiCodes.CommentRequired or SpiCodes.ReasonRequired
            or SpiCodes.InvalidReason or SpiCodes.InvalidOperation => StatusCodes.Status400BadRequest,
        SpiCodes.TaskNotFound or SpiCodes.TaskDefinitionNotFound or SpiCodes.DescriptionNotFound => StatusCodes.Status404NotFound,
        SpiCodes.TaskFinal or SpiCodes.ActionNotValid or SpiCodes.ConcurrentUpdate => StatusCodes.Status409Conflict,
        SpiCodes.ReservedByOther or SpiCodes.NotAuthorized or SpiCodes.UserContextRequired or SpiCodes.Forbidden => StatusCodes.Status403Forbidden,
        SpiCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        SpiCodes.NotImplemented => StatusCodes.Status501NotImplemented,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static SpiErrorBody Body(string code, string message, string? target = null) =>
        new(new SpiErrorDetail(code, message, target, []));

    /// <summary>Writes the SAP error for <paramref name="code"/> with its natural status and a message in the caller's language.</summary>
    public static Task WriteAsync(HttpContext context, string code, string? target = null) =>
        WriteAsync(context, StatusFor(code), code, target);

    public static Task WriteAsync(HttpContext context, int status, string code, string? target = null)
    {
        var message = SpiMessages.Get(code, SpiMessages.CultureFor(context.Request), target ?? string.Empty);
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(Body(code, message, target));
    }

    public static IResult Result(HttpContext context, string code, string? target = null)
    {
        var message = SpiMessages.Get(code, SpiMessages.CultureFor(context.Request), target ?? string.Empty);
        return Results.Json(Body(code, message, target), statusCode: StatusFor(code));
    }
}
