using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Tcp.Api.Auth;
using Tcp.Api.Endpoints;
using Tcp.Api.Security;

namespace Tcp.TestSupport;

/// <summary>Test-only protected endpoints used to exercise authentication/authorization policies.</summary>
public sealed class ProbeEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        static IResult Who(ICurrentUser u) =>
            Results.Ok(new { u.GlobalUserId, u.IsTechnical, u.ClientId });

        app.MapGet("/__probe/tech", Who).RequireAuthorization(Policies.SpiTech);
        app.MapGet("/__probe/user", Who).RequireAuthorization(Policies.SpiUser);
        app.MapGet("/__probe/any", Who).RequireAuthorization(Policies.SpiAny);
        app.MapGet("/__probe/scim", Who).RequireAuthorization(Policies.Scim);
        app.MapGet("/__probe/open", () => Results.Ok()).AllowAnonymous();
        // Same policy on a real SPI path to check the SAP Error body of 403 responses.
        app.MapGet("/task-provider/v2/__probe/user", Who).RequireAuthorization(Policies.SpiUser);
    }
}
