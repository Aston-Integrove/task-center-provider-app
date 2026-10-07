using Tcp.Api.Diagnostics;
using Tcp.Api.Security;

namespace Tcp.Api.Endpoints.Admin;

public static class RequestLogEndpoints
{
    public static RouteGroupBuilder MapAdminApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/api")
            .RequireAuthorization(Policies.Admin);

        // GET /admin/api/requests?prefix=/task-provider&status=200&limit=50
        group.MapGet("/requests", (IRequestLog log, string? prefix, int? status, int? limit) =>
            Results.Ok(log.Snapshot(prefix, status, limit)));

        return group;
    }
}
