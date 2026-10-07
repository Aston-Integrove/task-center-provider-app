using Tcp.Api.Auth;

namespace Tcp.Api.Endpoints.Admin;

public sealed record DecodeAssertionRequest(string? Assertion);

public static class DiagnosticsEndpoints
{
    public static RouteGroupBuilder MapAdminDiagnostics(this RouteGroupBuilder admin)
    {
        // POST /admin/api/diagnostics/decode-assertion  {"assertion":"<JWT>"}
        // Shows header + claims (never the signature) and how we would resolve the Global User ID.
        admin.MapPost("/diagnostics/decode-assertion", async (
            DecodeAssertionRequest request,
            IAssertionIssuerRegistry issuers,
            IGlobalUserResolver resolver,
            CancellationToken ct) =>
        {
            var decoded = AssertionDecoder.TryDecode(request.Assertion);
            if (decoded is null)
                return Results.BadRequest(new { error = "not_a_jwt", detail = "Expected header.payload.signature" });

            var issuer = decoded.Claims["iss"]?.GetValue<string>();
            var trusted = issuer is null ? null : issuers.Find(issuer);
            var resolved = await resolver.ResolveAsync(decoded.Claims, ct);

            return Results.Ok(new
            {
                header = decoded.Header,
                claims = decoded.Claims,
                claimNames = decoded.Claims.Select(c => c.Key).ToArray(),
                issuer,
                trustedIssuer = trusted is not null,
                userResolution = new
                {
                    resolved = resolved is not null,
                    globalUserId = resolved?.User.GlobalUserId,
                    via = resolved?.Via,
                },
            });
        });

        return admin;
    }
}
