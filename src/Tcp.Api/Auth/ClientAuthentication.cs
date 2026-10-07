using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using Tcp.Api.Security;

namespace Tcp.Api.Auth;

public sealed record ClientAuthResult(OAuthClient? Client, string? Error, string? Description, int Status)
{
    public bool Succeeded => Client is not null;
    public static ClientAuthResult Ok(OAuthClient c) => new(c, null, null, 200);
    public static ClientAuthResult InvalidClient(string desc = "Client authentication failed") => new(null, "invalid_client", desc, 401);
    public static ClientAuthResult InvalidRequest(string desc) => new(null, "invalid_request", desc, 400);
}

public interface IClientStore
{
    OAuthClient? Find(string clientId);
    string? GetSecret(OAuthClient client);
}

/// <summary>Client registry from <c>OAuth:Clients</c>; secrets from <c>Secrets:{SecretName}</c> (Key Vault).</summary>
public sealed class ConfigClientStore(IOptions<OAuthOptions> options, IConfiguration configuration) : IClientStore
{
    public OAuthClient? Find(string clientId) =>
        options.Value.Clients.FirstOrDefault(c => string.Equals(c.ClientId, clientId, StringComparison.Ordinal));

    public string? GetSecret(OAuthClient client)
    {
        if (!string.IsNullOrEmpty(client.SecretName))
        {
            var fromVault = configuration[$"Secrets:{client.SecretName}"];
            if (!string.IsNullOrEmpty(fromVault)) return fromVault;
        }
        return string.IsNullOrEmpty(client.Secret) ? null : client.Secret;
    }
}

/// <summary>
/// <c>client_secret_basic</c> and <c>client_secret_post</c> (FR-TOK-02). Using both methods at once is
/// rejected (RFC 6749 section 2.3), but a <c>client_id</c> repeated in the body is tolerated if it matches.
/// </summary>
public sealed class ClientAuthenticator(IClientStore store)
{
    private const string DummySecret = "dummy-secret-for-constant-time-path";

    public static (string? Id, string? Secret)? ReadBasic(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Authorization", out var header)) return null;
        if (!AuthenticationHeaderValue.TryParse(header, out var parsed) ||
            !string.Equals(parsed.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(parsed.Parameter)) return null;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parsed.Parameter));
            var idx = decoded.IndexOf(':');
            return idx < 0 ? (decoded, string.Empty) : (decoded[..idx], decoded[(idx + 1)..]);
        }
        catch (FormatException)
        {
            return (null, null);
        }
    }

    public ClientAuthResult Authenticate(HttpRequest request, IFormCollection form)
    {
        var basic = ReadBasic(request);
        string? formId = form["client_id"].FirstOrDefault();
        string? formSecret = form["client_secret"].FirstOrDefault();

        string? id, secret;
        if (basic is not null)
        {
            if (!string.IsNullOrEmpty(formSecret))
                return ClientAuthResult.InvalidRequest("Multiple client authentication methods used");
            (id, secret) = basic.Value;
            if (!string.IsNullOrEmpty(formId) && !string.Equals(formId, id, StringComparison.Ordinal))
                return ClientAuthResult.InvalidRequest("client_id does not match the Authorization header");
        }
        else
        {
            id = formId;
            secret = formSecret;
        }

        if (string.IsNullOrEmpty(id)) return ClientAuthResult.InvalidClient();

        var client = store.Find(id);
        var expected = client is null ? null : store.GetSecret(client);

        // Always run one constant-time comparison so unknown clients are indistinguishable from bad secrets.
        var secretOk = ConstantTime.Equals(secret, expected ?? DummySecret);
        return client is not null && expected is not null && secretOk
            ? ClientAuthResult.Ok(client)
            : ClientAuthResult.InvalidClient();
    }
}
