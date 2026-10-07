using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using Tcp.Api.Auth;
using Tcp.Api.Configuration;
using Tcp.Api.Endpoints.Admin;
using Tcp.Api.Endpoints.Spi;
using Tcp.Api.Security;
using Tcp.Domain.Tasks;
using Tcp.Infrastructure.Html;
using Tcp.Infrastructure.Tasks;

namespace Tcp.Api.Endpoints.App;

/// <summary>
/// <c>/app/tasks/{urn}</c> - the page <c>uiLink</c> opens (ADR-009). Basic mode shows it read-only to administrators;
/// IAS mode shows it to entitled users, who can also respond and act there. Those actions reuse the SPI domain logic
/// (<see cref="OperationService"/>), so provider-side completion flows back to Task Center through the next delta pull.
/// </summary>
public static class AppEndpoints
{
    public static IEndpointRouteBuilder MapApp(this IEndpointRouteBuilder app)
    {
        app.MapGet("/app/app.css", (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.Content(AppPageRenderer.Stylesheet, "text/css; charset=utf-8");
        }).AllowAnonymous();

        var group = app.MapGroup("/app/tasks").RequireAuthorization(Policies.App);
        group.MapGet("/{urn}", ShowAsync);
        group.MapPost("/{urn}/respond", (HttpContext h, string urn, OperationService o) => ExecuteAsync(h, urn, OperationKind.Response, o));
        group.MapPost("/{urn}/action", (HttpContext h, string urn, OperationService o) => ExecuteAsync(h, urn, OperationKind.Action, o));
        return app;
    }

    private static async Task<IResult> ShowAsync(
        HttpContext http, string urn, TaskRepository tasks, DefinitionRepository definitions, Infrastructure.Persistence.TcpDbContext db,
        OperationService operations, IGlobalUserResolver resolver, HtmlDescriptionSanitizer sanitizer,
        IOptions<AppOptions> appOptions, IOptions<ProviderOptions> provider, IAntiforgery antiforgery)
    {
        var language = ChooseLanguage(http);
        var parsed = SpiEndpoints.ParseUrn(urn, UrnKind.Task);
        var task = parsed is null ? null : await tasks.FindAsync(parsed, http.RequestAborted);
        if (task is null) return Message(http, StatusCodes.Status404NotFound, language, "The task was not found.", "Die Aufgabe wurde nicht gefunden.");

        string? userLabel = null;
        var canAct = false;
        if (appOptions.Value.IsOidc)
        {
            var user = await ResolveUserAsync(http, resolver);
            if (user is null)
                return Message(http, StatusCodes.Status403Forbidden, language, "Your account is not known to this application.", "Ihr Konto ist dieser Anwendung nicht bekannt.");
            try
            {
                await operations.CheckEntitlementAsync(task, user.User.GlobalUserId, http.RequestAborted);
            }
            catch (TaskRuleViolation)
            {
                return Message(http, StatusCodes.Status403Forbidden, language, "You are not entitled to see this task.", "Sie sind nicht berechtigt, diese Aufgabe zu sehen.");
            }
            userLabel = user.User.DisplayName ?? user.User.UserName;
            canAct = true;
        }

        var detail = await TaskDetail.LoadAsync(task, db, http.RequestAborted);
        var definition = await definitions.FindAsync(task.DefinitionUrn, http.RequestAborted);

        string? field = null, token = null;
        if (canAct)
        {
            var tokens = antiforgery.GetAndStoreTokens(http);
            (field, token) = (tokens.FormFieldName, tokens.RequestToken);
        }

        string? notice = http.Request.Query.ContainsKey("done") ? Label(language, "Done.", "Erledigt.") : null;
        string? error = null;
        if (http.Request.Query["error"].FirstOrDefault() is { Length: > 0 } code && code.StartsWith("tcp.", StringComparison.Ordinal))
            error = SpiMessages.Get(code, language == "de" ? SpiMessages.German : SpiMessages.English, http.Request.Query["target"].FirstOrDefault() ?? string.Empty);

        var html = AppPageRenderer.Render(new AppPageModel(detail, definition, language == "de" ? "de-DE" : "en-US", provider.Value.DefaultLanguage,
            userLabel, field, token, notice, error, canAct), sanitizer);
        Headers(http);
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private static async Task<IResult> ExecuteAsync(HttpContext http, string urn, OperationKind kind, OperationService operations)
    {
        var sp = http.RequestServices;
        if (!sp.GetRequiredService<IOptions<AppOptions>>().Value.IsOidc)
            return Message(http, StatusCodes.Status403Forbidden, ChooseLanguage(http), "Actions are only available when signed in with SAP IAS.", "Aktionen sind nur mit SAP-IAS-Anmeldung verfügbar.");

        // Cookie session => CSRF protection is mandatory.
        if (!await sp.GetRequiredService<IAntiforgery>().IsRequestValidAsync(http))
            return Results.StatusCode(StatusCodes.Status400BadRequest);

        var language = ChooseLanguage(http);
        var user = await ResolveUserAsync(http, sp.GetRequiredService<IGlobalUserResolver>());
        var normalized = SpiEndpoints.ParseUrn(urn, UrnKind.Task);
        if (user is null || normalized is null)
            return Message(http, StatusCodes.Status403Forbidden, language, "Not allowed.", "Nicht erlaubt.");

        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var code = form["code"].FirstOrDefault() ?? string.Empty;
        var comment = form["comment"].FirstOrDefault() is { Length: > 0 } c ? c : null;
        var reason = form["reasonCode"].FirstOrDefault() is { Length: > 0 } r ? r : null;
        var page = $"/app/tasks/{Uri.EscapeDataString(normalized)}";

        try
        {
            await operations.ExecuteAsync(kind, normalized, user.User.GlobalUserId, new OperationRequest(code, comment, reason), http.RequestAborted, allowAsync: false);
            return Results.Redirect($"{page}?done=1&lang={language}");
        }
        catch (TaskRuleViolation violation)
        {
            var target = violation.Target is null ? string.Empty : $"&target={Uri.EscapeDataString(violation.Target)}";
            return Results.Redirect($"{page}?error={Uri.EscapeDataString(violation.Code)}{target}&lang={language}");
        }
    }

    /// <summary>Who is looking: Global User ID from the IAS claims, via the same resolver as the token service.</summary>
    private static async Task<ResolvedUser?> ResolveUserAsync(HttpContext http, IGlobalUserResolver resolver)
    {
        var claims = new JsonObject();
        var options = http.RequestServices.GetRequiredService<IOptions<AppOptions>>().Value.Oidc;
        foreach (var claim in http.User.Claims.Where(c => c.Type is "email" or "sub" or "name")) claims[claim.Type] = claim.Value;
        var id = http.User.FindFirst(options.UserIdClaim)?.Value;
        if (id is not null) claims["user_uuid"] = id;
        return await resolver.ResolveAsync(claims, http.RequestAborted);
    }

    private static string ChooseLanguage(HttpContext http)
    {
        if (http.Request.Query["lang"].FirstOrDefault() is { Length: > 0 } explicitLanguage) return AppPageRenderer.Culture(explicitLanguage);
        return SpiMessages.CultureFor(http.Request).Name.StartsWith("de", StringComparison.Ordinal) ? "de" : "en";
    }

    private static string Label(string language, string en, string de) => language == "de" ? de : en;

    /// <summary>A tiny error page; the text is fixed (nothing from the request is echoed).</summary>
    private static IResult Message(HttpContext http, int status, string language, string en, string de)
    {
        Headers(http);
        var text = System.Net.WebUtility.HtmlEncode(Label(language, en, de));
        return Results.Content(
            $"<!doctype html><html lang=\"{language}\"><head><meta charset=\"utf-8\"><title>{text}</title><link rel=\"stylesheet\" href=\"/app/app.css\"></head><body><main><p class=\"error\" role=\"alert\">{text}</p></main></body></html>",
            "text/html; charset=utf-8", statusCode: status);
    }

    private static void Headers(HttpContext http)
    {
        var h = http.Response.Headers;
        h["Content-Security-Policy"] = "default-src 'none'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        h.CacheControl = "no-store";
    }
}
