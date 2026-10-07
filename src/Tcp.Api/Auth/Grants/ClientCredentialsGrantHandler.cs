namespace Tcp.Api.Auth.Grants;

/// <summary>Technical tokens (Task Center pull) and the SCIM client token. Subject is the client itself.</summary>
public sealed class ClientCredentialsGrantHandler : IGrantHandler
{
    /// <summary>Constitution VI: technical tokens live at most 15 minutes.</summary>
    public const int MaxLifetimeSeconds = 900;

    public string GrantType => GrantTypes.ClientCredentials;

    public Task<GrantResult> HandleAsync(GrantContext context) =>
        Task.FromResult(GrantResult.Ok(
            context.Client.ClientId,
            Math.Clamp(context.Client.TokenLifetimeSeconds, 1, MaxLifetimeSeconds)));
}
