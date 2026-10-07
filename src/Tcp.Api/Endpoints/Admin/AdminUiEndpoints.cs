using Microsoft.Extensions.FileProviders;
using Tcp.Api.Security;

namespace Tcp.Api.Endpoints.Admin;

/// <summary>
/// Serves the admin single-page UI from <c>wwwroot/admin</c> behind the admin Basic auth (US-005-5). No build step and no
/// external resources: the CSP only allows this origin, so there is no inline script or style.
/// </summary>
public static class AdminUiEndpoints
{
    private static readonly Dictionary<string, string> Files = new(StringComparer.Ordinal)
    {
        ["index.html"] = "text/html; charset=utf-8",
        ["admin.js"] = "text/javascript; charset=utf-8",
        ["admin.css"] = "text/css; charset=utf-8",
    };

    public static IEndpointRouteBuilder MapAdminUi(this IEndpointRouteBuilder app)
    {
        var ui = app.MapGroup("/admin").RequireAuthorization(Policies.Admin);

        ui.MapGet("", (HttpContext http) => Serve(http, "index.html"));
        ui.MapGet("/{file}", (HttpContext http, string file) => Files.ContainsKey(file) ? Serve(http, file) : Results.NotFound());
        return app;
    }

    private static IResult Serve(HttpContext http, string file)
    {
        var provider = http.RequestServices.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider;
        var info = provider.GetFileInfo("admin/" + file);
        if (!info.Exists) return Results.NotFound();

        var h = http.Response.Headers;
        h["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
            "frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        h.CacheControl = "no-cache";
        return Results.File(info.CreateReadStream(), Files[file]);
    }
}
