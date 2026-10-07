using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Tcp.Domain.Identity;

namespace Tcp.TestSupport;

/// <summary>Records the calls SCIM makes towards the (not yet implemented) task side.</summary>
public sealed class RecordingTaskReferences : ITaskIdentityReferences
{
    public HashSet<string> ReferencedUsers { get; } = [];
    public List<(string GlobalUserId, DateTime At)> Anonymised { get; } = [];
    public List<(string Group, DateTime At)> RemovedGroups { get; } = [];

    public Task<bool> IsUserReferencedAsync(string globalUserId, CancellationToken ct) =>
        Task.FromResult(ReferencedUsers.Contains(globalUserId));

    public Task AnonymiseUserAsync(string globalUserId, DateTime nowUtc, CancellationToken ct)
    {
        Anonymised.Add((globalUserId, nowUtc));
        return Task.CompletedTask;
    }

    public Task RemoveGroupAsync(string groupName, DateTime nowUtc, CancellationToken ct)
    {
        RemovedGroups.Add((groupName, nowUtc));
        return Task.CompletedTask;
    }
}

/// <summary>
/// One API host (and database) per configuration key, shared by all tests of that configuration so migrations
/// run once. Tests isolate themselves with unique user names / ids instead of cleaning up.
/// </summary>
public sealed class ScimHost
{
    private static readonly ConcurrentDictionary<string, Lazy<ScimHost>> Hosts = new();

    public TcpFactory Factory { get; }
    public RecordingTaskReferences TaskReferences { get; } = new();
    private string? _scimToken;

    private ScimHost(string connectionString, Dictionary<string, string?> settings)
    {
        var refs = TaskReferences;
        Factory = new TcpFactory(connectionString, settings, services =>
        {
            services.AddSingleton<ITaskIdentityReferences>(refs);
        }, issuers: [Oidc]);
    }

    public static TestIssuer Oidc { get; } = new();

    public static ScimHost Get(SqlServerFixture sql, string key, Dictionary<string, string?>? settings = null) =>
        Hosts.GetOrAdd(key, _ => new Lazy<ScimHost>(() =>
            new ScimHost(sql.ConnectionString("scim_" + key.Replace('-', '_')), settings ?? []))).Value;

    public async Task<ScimClient> ClientAsync()
    {
        var http = Factory.CreateClient();
        _scimToken ??= await OAuthTestClient.GetScimTokenAsync(http);
        return new ScimClient(http, _scimToken);
    }

    public T Resolve<T>(Func<IServiceProvider, T> f)
    {
        using var scope = Factory.Services.CreateScope();
        return f(scope.ServiceProvider);
    }
}

public sealed record ScimResponse(HttpStatusCode Status, JsonObject? Body, HttpResponseMessage Raw)
{
    public string? ScimType => Body?["scimType"]?.GetValue<string>();
    public string? Id => Body?["id"]?.GetValue<string>();
}

public sealed class ScimClient(HttpClient http, string? token)
{
    public HttpClient Http { get; } = http;

    public async Task<ScimResponse> SendAsync(HttpMethod method, string path, JsonNode? body = null,
        string contentType = "application/scim+json", AuthenticationHeaderValue? auth = null, bool noAuth = false)
    {
        var request = new HttpRequestMessage(method, "/scim/v2" + path);
        if (!noAuth)
            request.Headers.Authorization = auth ?? new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, contentType);

        var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonObject? json = null;
        if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith('{'))
            json = JsonNode.Parse(text) as JsonObject;
        return new ScimResponse(response.StatusCode, json, response);
    }

    public async Task<ScimResponse> SendRawAsync(HttpMethod method, string path, string rawBody, string contentType = "application/scim+json")
    {
        var request = new HttpRequestMessage(method, "/scim/v2" + path) { Content = new StringContent(rawBody, Encoding.UTF8, contentType) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return new ScimResponse(response.StatusCode, text.TrimStart().StartsWith('{') ? JsonNode.Parse(text) as JsonObject : null, response);
    }

    public Task<ScimResponse> Get(string path) => SendAsync(HttpMethod.Get, path);
    public Task<ScimResponse> Post(string path, JsonNode body) => SendAsync(HttpMethod.Post, path, body);
    public Task<ScimResponse> Put(string path, JsonNode body) => SendAsync(HttpMethod.Put, path, body);
    public Task<ScimResponse> Patch(string path, JsonNode body) => SendAsync(HttpMethod.Patch, path, body);
    public Task<ScimResponse> Delete(string path) => SendAsync(HttpMethod.Delete, path);

    public async Task<ScimResponse> CreateUser(string? userName = null, string? userUuid = null, Action<JsonObject>? customise = null)
    {
        var user = Scim.UserPayload(userName ?? Scim.Name("u"), userUuid ?? Guid.NewGuid().ToString());
        customise?.Invoke(user);
        var response = await Post("/Users", user);
        if (response.Status != HttpStatusCode.Created)
            throw new Xunit.Sdk.XunitException($"Expected 201 but got {(int)response.Status}: {response.Body?.ToJsonString()}");
        return response;
    }

    public async Task<ScimResponse> CreateGroup(string? displayName = null, params string[] memberIds)
    {
        var response = await Post("/Groups", Scim.GroupPayload(displayName ?? Scim.Name("g"), memberIds));
        if (response.Status != HttpStatusCode.Created)
            throw new Xunit.Sdk.XunitException($"Expected 201 but got {(int)response.Status}: {response.Body?.ToJsonString()}");
        return response;
    }
}

public static class Scim
{
    public const string SapExt = "urn:ietf:params:scim:schemas:extension:sap:2.0:User";
    public const string PatchOp = "urn:ietf:params:scim:api:messages:2.0:PatchOp";

    public static string Name(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    public static JsonObject UserPayload(string userName, string userUuid) => new()
    {
        ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:User", SapExt),
        ["userName"] = userName,
        ["displayName"] = userName + " Display",
        ["active"] = true,
        ["name"] = new JsonObject { ["givenName"] = "Given", ["familyName"] = "Family" },
        ["emails"] = new JsonArray(new JsonObject { ["value"] = userName + "@corp.example", ["type"] = "work", ["primary"] = true }),
        [SapExt] = new JsonObject { ["userUuid"] = userUuid },
    };

    public static JsonObject GroupPayload(string displayName, params string[] memberIds) => new()
    {
        ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:Group"),
        ["displayName"] = displayName,
        ["members"] = new JsonArray(memberIds.Select(id => (JsonNode)new JsonObject { ["value"] = id }).ToArray()),
    };

    public static JsonObject PatchBody(params JsonObject[] operations) => new()
    {
        ["schemas"] = new JsonArray(PatchOp),
        ["Operations"] = new JsonArray(operations.Cast<JsonNode>().ToArray()),
    };

    public static JsonObject Op(string op, string? path = null, JsonNode? value = null)
    {
        var o = new JsonObject { ["op"] = op };
        if (path is not null) o["path"] = path;
        if (value is not null) o["value"] = value;
        return o;
    }

    public static string[] Ids(JsonObject? list) =>
        list!["Resources"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()).ToArray();
}
