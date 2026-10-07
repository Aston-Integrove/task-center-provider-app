using System.Net;
using System.Text.Json.Nodes;
using Tcp.TestSupport;

namespace Tcp.ContractTests;

[Collection(SqlCollection.Name)]
public class ScimContractTests(SqlServerFixture sql)
{
    private static readonly OpenApiContract Contract =
        OpenApiContract.Load("specs/003-scim-provisioning/contracts/scim.openapi.yaml");

    private ScimHost Host => ScimHost.Get(sql, "contract");

    private static void Conforms(ScimResponse response, string path, string method, int status)
    {
        ((int)response.Status).Should().Be(status, response.Body?.ToJsonString());
        OpenApiContract.AssertValid(
            Contract.ValidateResponse(path, method, status, response.Body?.ToJsonString() ?? "{}"),
            $"{method.ToUpperInvariant()} {path} -> {status}");
    }

    private static void RequestConforms(string path, string method, JsonNode body) =>
        OpenApiContract.AssertValid(Contract.ValidateRequest(path, method, body.ToJsonString()), $"request {method.ToUpperInvariant()} {path}");

    [Fact] // T003-03
    public async Task Discovery_endpoints_conform()
    {
        var scim = await Host.ClientAsync();
        Conforms(await scim.Get("/ServiceProviderConfig"), "/ServiceProviderConfig", "get", 200);
        Conforms(await scim.Get("/ResourceTypes"), "/ResourceTypes", "get", 200);
        Conforms(await scim.Get("/Schemas"), "/Schemas", "get", 200);
    }

    [Fact]
    public async Task User_lifecycle_conforms_to_the_contract()
    {
        var scim = await Host.ClientAsync();
        var payload = Scim.UserPayload(Scim.Name("ct"), Guid.NewGuid().ToString());
        RequestConforms("/Users", "post", payload);

        var created = await scim.Post("/Users", payload);
        Conforms(created, "/Users", "post", 201);

        Conforms(await scim.Get($"/Users/{created.Id}"), "/Users/{id}", "get", 200);
        Conforms(await scim.Get("/Users?count=5"), "/Users", "get", 200);
        Conforms(await scim.Get("/Users?filter=" + Uri.EscapeDataString($"userName eq \"{payload["userName"]}\"")), "/Users", "get", 200);

        var put = Scim.UserPayload(payload["userName"]!.GetValue<string>(), created.Body![Scim.SapExt]!["userUuid"]!.GetValue<string>());
        RequestConforms("/Users/{id}", "put", put);
        Conforms(await scim.Put($"/Users/{created.Id}", put), "/Users/{id}", "put", 200);

        var patch = Scim.PatchBody(Scim.Op("replace", "active", false));
        RequestConforms("/Users/{id}", "patch", patch);
        Conforms(await scim.Patch($"/Users/{created.Id}", patch), "/Users/{id}", "patch", 200);

        var deleted = await scim.Delete($"/Users/{created.Id}");
        deleted.Status.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task User_error_responses_conform()
    {
        var scim = await Host.ClientAsync();
        var name = Scim.Name("cterr");
        await scim.CreateUser(name);

        Conforms(await scim.Post("/Users", Scim.UserPayload(name, Guid.NewGuid().ToString())), "/Users", "post", 409);
        Conforms(await scim.Post("/Users", new JsonObject { ["schemas"] = new JsonArray(), ["userName"] = "" }), "/Users", "post", 400);
        Conforms(await scim.Get("/Users?filter=" + Uri.EscapeDataString("bogus")), "/Users", "get", 400);
        Conforms(await scim.Get($"/Users/{Guid.NewGuid()}"), "/Users/{id}", "get", 404);
        Conforms(await scim.Put($"/Users/{Guid.NewGuid()}", Scim.UserPayload("x", Guid.NewGuid().ToString())), "/Users/{id}", "put", 404);
        Conforms(await scim.Patch($"/Users/{Guid.NewGuid()}", Scim.PatchBody(Scim.Op("replace", "active", false))), "/Users/{id}", "patch", 404);
        Conforms(await scim.Delete($"/Users/{Guid.NewGuid()}"), "/Users/{id}", "delete", 404);
    }

    [Fact]
    public async Task Group_lifecycle_conforms_to_the_contract()
    {
        var scim = await Host.ClientAsync();
        var user = await scim.CreateUser();
        var payload = Scim.GroupPayload(Scim.Name("ctg"), user.Id!);
        RequestConforms("/Groups", "post", payload);

        var created = await scim.Post("/Groups", payload);
        Conforms(created, "/Groups", "post", 201);

        Conforms(await scim.Get($"/Groups/{created.Id}"), "/Groups/{id}", "get", 200);
        Conforms(await scim.Get($"/Groups/{created.Id}?excludedAttributes=members"), "/Groups/{id}", "get", 200);
        Conforms(await scim.Get("/Groups?count=5"), "/Groups", "get", 200);
        Conforms(await scim.Put($"/Groups/{created.Id}", Scim.GroupPayload(Scim.Name("ctg2"), user.Id!)), "/Groups/{id}", "put", 200);

        var patch = Scim.PatchBody(Scim.Op("remove", "members"));
        RequestConforms("/Groups/{id}", "patch", patch);
        Conforms(await scim.Patch($"/Groups/{created.Id}", patch), "/Groups/{id}", "patch", 200);

        (await scim.Delete($"/Groups/{created.Id}")).Status.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Group_error_responses_conform()
    {
        var scim = await Host.ClientAsync();
        var name = Scim.Name("cgerr");
        await scim.CreateGroup(name);

        Conforms(await scim.Post("/Groups", Scim.GroupPayload(Scim.Name("x"), Guid.NewGuid().ToString())), "/Groups", "post", 400);
        Conforms(await scim.Post("/Groups", Scim.GroupPayload(name)), "/Groups", "post", 409);
        Conforms(await scim.Get($"/Groups/{Guid.NewGuid()}"), "/Groups/{id}", "get", 404);
        Conforms(await scim.Put($"/Groups/{Guid.NewGuid()}", Scim.GroupPayload("x")), "/Groups/{id}", "put", 404);
        Conforms(await scim.Patch($"/Groups/{Guid.NewGuid()}", Scim.PatchBody(Scim.Op("remove", "members"))), "/Groups/{id}", "patch", 404);
        Conforms(await scim.Delete($"/Groups/{Guid.NewGuid()}"), "/Groups/{id}", "delete", 404);
    }
}
