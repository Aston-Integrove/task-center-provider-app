using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Tcp.Api.Endpoints.Spi;

namespace Tcp.Api.Auth;

/// <summary>Satisfied when the token's space-delimited <c>scope</c> claim contains any of the listed scopes.</summary>
public sealed class ScopeRequirement(params string[] anyOf) : IAuthorizationRequirement
{
    public IReadOnlyList<string> AnyOf { get; } = anyOf;

    public static IReadOnlySet<string> ScopesOf(ClaimsPrincipal user) =>
        (user.FindFirst("scope")?.Value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
}

public sealed class ScopeRequirementHandler : AuthorizationHandler<ScopeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ScopeRequirement requirement)
    {
        var scopes = ScopeRequirement.ScopesOf(context.User);
        if (requirement.AnyOf.Any(scopes.Contains)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>User-context SPI calls need a <c>spi.user</c> token with a subject (the Global User ID).</summary>
public sealed class UserContextRequirement : IAuthorizationRequirement;

public sealed class UserContextRequirementHandler : AuthorizationHandler<UserContextRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, UserContextRequirement requirement)
    {
        if (ScopeRequirement.ScopesOf(context.User).Contains(Scopes.SpiUser) &&
            !string.IsNullOrEmpty(context.User.FindFirst("sub")?.Value))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Renders SPI authorization failures with the SAP <c>Error</c> body (FR-SPI-04): a technical token on a
/// user-context endpoint is <c>403 tcp.auth.userContextRequired</c>. Other paths keep the default behaviour.
/// </summary>
public sealed class SpiAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden && SpiPaths.IsSpi(context.Request.Path))
        {
            var userContext = authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<UserContextRequirement>().Any() == true;
            await SpiErrors.WriteAsync(context, userContext ? Domain.Tasks.SpiCodes.UserContextRequired : Domain.Tasks.SpiCodes.Forbidden);
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}

public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    /// <summary>Global User ID (token <c>sub</c>) for user-context tokens; null for technical tokens.</summary>
    string? GlobalUserId { get; }

    bool IsTechnical { get; }
    string? ClientId { get; }
}

public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? User => accessor.HttpContext?.User;
    private IReadOnlySet<string> Scopes => User is null ? new HashSet<string>() : ScopeRequirement.ScopesOf(User);

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true;
    public bool IsTechnical => Scopes.Contains(Auth.Scopes.SpiTech);
    public string? ClientId => User?.FindFirst("client_id")?.Value;
    public string? GlobalUserId =>
        Scopes.Contains(Auth.Scopes.SpiUser) ? User?.FindFirst("sub")?.Value : null;
}
