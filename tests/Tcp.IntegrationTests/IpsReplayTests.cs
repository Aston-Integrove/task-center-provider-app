using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tcp.TestSupport;

namespace Tcp.IntegrationTests;

/// <summary>
/// Replays recorded (or, until spike S-03 runs, synthetic) IPS request sequences from tests/fixtures/ips against the
/// real API and database. See the README next to the fixtures for the format.
/// </summary>
[Collection(SqlCollection.Name)]
public partial class IpsReplayTests(SqlServerFixture sql)
{
    public static IEnumerable<object[]> Fixtures() =>
        Directory.GetFiles(Path.Combine(OpenApiContract.RepositoryRoot, "tests", "fixtures", "ips"), "*.json")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => new object[] { Path.GetFileName(f) });

    [Fact]
    public void Fixture_set_is_not_empty() => Fixtures().Should().NotBeEmpty();

    [Theory] // T003-16
    [MemberData(nameof(Fixtures))]
    public async Task Replays_cleanly(string fixtureFile)
    {
        var host = ScimHost.Get(sql, "replay");
        var scim = await host.ClientAsync();
        var token = await OAuthTestClient.GetScimTokenAsync(scim.Http);

        var fixture = (JsonObject)JsonNode.Parse(File.ReadAllText(
            Path.Combine(OpenApiContract.RepositoryRoot, "tests", "fixtures", "ips", fixtureFile)))!;

        var vars = new Dictionary<string, string>
        {
            ["rand"] = Guid.NewGuid().ToString("N")[..12],
            ["guid1"] = Guid.NewGuid().ToString(),
            ["guid2"] = Guid.NewGuid().ToString(),
            ["guid3"] = Guid.NewGuid().ToString(),
        };

        foreach (var step in fixture["steps"]!.AsArray().Cast<JsonObject>())
        {
            var name = step["name"]!.GetValue<string>();
            var request = new HttpRequestMessage(new HttpMethod(step["method"]!.GetValue<string>()), Substitute(step["path"]!.GetValue<string>(), vars));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (step["body"] is { } body)
            {
                var text = Substitute(body.ToJsonString(), vars, jsonEscape: true);
                request.Content = new StringContent(text, Encoding.UTF8, step["contentType"]?.GetValue<string>() ?? "application/scim+json");
            }

            var response = await scim.Http.SendAsync(request);
            var raw = await response.Content.ReadAsStringAsync();
            var json = raw.TrimStart().StartsWith('{') ? JsonNode.Parse(raw) : null;

            var expect = (JsonObject)step["expect"]!;
            ((int)response.StatusCode).Should().Be(expect["status"]!.GetValue<int>(), $"[{fixtureFile}] step '{name}': {raw}");

            if (expect["body"] is JsonObject expectedBody)
                foreach (var (path, expected) in expectedBody)
                {
                    var actual = Resolve(json, path);
                    var expectedText = Substitute(expected!.ToJsonString(), vars, jsonEscape: true);
                    (actual?.ToJsonString()).Should().Be(expectedText, $"[{fixtureFile}] step '{name}': {path} in {raw}");
                }

            if (step["capture"] is JsonObject capture)
                foreach (var (variable, source) in capture)
                    vars[variable] = Resolve(json, source!.GetValue<string>())?.GetValue<string>()
                        ?? throw new InvalidOperationException($"[{fixtureFile}] step '{name}': nothing to capture for '{variable}'");
        }
    }

    [Fact] // T003-16: the exporter turns what IPS sent into fixtures
    public async Task Request_log_export_yields_redacted_replayable_steps()
    {
        var host = ScimHost.Get(sql, "export");
        var scim = await host.ClientAsync();
        var name = Scim.Name("exp");
        await scim.CreateUser(name);
        await scim.Get("/Users?filter=" + Uri.EscapeDataString($"userName eq \"{name}\""));

        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/api/requests/export?prefix=/scim");
        request.Headers.Authorization = OAuthTestClient.Basic(TcpFactory.AdminUser, TcpFactory.AdminPassword);
        var response = await scim.Http.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain("Bearer ").And.NotContain(TcpFactory.ScimSecret);
        var steps = JsonNode.Parse(text)!["steps"]!.AsArray();
        steps.Should().HaveCountGreaterOrEqualTo(2);
        steps[0]!["method"]!.GetValue<string>().Should().Be("POST");
        steps[0]!["path"]!.GetValue<string>().Should().Be("/scim/v2/Users");
        steps[0]!["body"]!["userName"]!.GetValue<string>().Should().Be(name);
        steps[0]!["expect"]!["status"]!.GetValue<int>().Should().Be(201);
        steps[1]!["path"]!.GetValue<string>().Should().StartWith("/scim/v2/Users?filter=");

        // admin only
        var anonymous = await scim.Http.GetAsync("/admin/api/requests/export");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex Placeholder();

    private static string Substitute(string text, Dictionary<string, string> vars, bool jsonEscape = false) =>
        Placeholder().Replace(text, m =>
        {
            if (!vars.TryGetValue(m.Groups[1].Value, out var value))
                throw new InvalidOperationException($"Unknown placeholder {m.Value}");
            return jsonEscape ? JsonEncodedText.Encode(value).ToString() : value;
        });

    /// <summary>Dotted path lookup; object keys that themselves contain dots (URNs) are matched greedily.</summary>
    private static JsonNode? Resolve(JsonNode? node, string path)
    {
        if (path.Length == 0) return node;
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).OrderByDescending(k => k.Length))
                {
                    if (path == key) return obj[key];
                    if (path.StartsWith(key + ".", StringComparison.Ordinal)) return Resolve(obj[key], path[(key.Length + 1)..]);
                }
                return null;
            case JsonArray array:
                var dot = path.IndexOf('.');
                var segment = dot < 0 ? path : path[..dot];
                var rest = dot < 0 ? "" : path[(dot + 1)..];
                if (segment == "length" && rest.Length == 0) return JsonValue.Create(array.Count);
                return int.TryParse(segment, out var index) && index < array.Count ? Resolve(array[index], rest) : null;
            default:
                return null;
        }
    }
}
