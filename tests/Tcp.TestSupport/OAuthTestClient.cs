using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Tcp.Api.Auth;

namespace Tcp.TestSupport;

public static class OAuthTestClient
{
    public static AuthenticationHeaderValue Basic(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

    public static Task<HttpResponseMessage> PostTokenAsync(
        HttpClient client, Dictionary<string, string> form, string? basicUser = null, string? basicPassword = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token") { Content = new FormUrlEncodedContent(form) };
        if (basicUser is not null) request.Headers.Authorization = Basic(basicUser, basicPassword ?? string.Empty);
        return client.SendAsync(request);
    }

    public static async Task<string> GetTokenAsync(HttpClient client, string clientId, string secret, string? scope = null)
    {
        var form = new Dictionary<string, string> { ["grant_type"] = GrantTypes.ClientCredentials };
        if (scope is not null) form["scope"] = scope;
        var response = await PostTokenAsync(client, form, clientId, secret);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["access_token"]!.GetValue<string>();
    }

    public static Task<string> GetTechTokenAsync(HttpClient client) =>
        GetTokenAsync(client, "tc-tech", TcpFactory.TechSecret);

    public static Task<string> GetScimTokenAsync(HttpClient client) =>
        GetTokenAsync(client, "ips-scim", TcpFactory.ScimSecret);

    public static async Task<string> GetUserTokenAsync(HttpClient client, string assertion)
    {
        var response = await PostTokenAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.JwtBearer, ["assertion"] = assertion,
        }, "tc-pp", TcpFactory.PpSecret);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["access_token"]!.GetValue<string>();
    }

    public static HttpRequestMessage Get(string url, string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }
}
