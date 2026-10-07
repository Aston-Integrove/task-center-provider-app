using System.Net;
using System.Text.Json.Nodes;
using Tcp.TestSupport;

namespace Tcp.IntegrationTests;

[Collection(SqlCollection.Name)]
public class OpenApiDocumentTests(SqlServerFixture sql)
{
    [Fact] // spec 005 contract: generated from code, admin only
    public async Task Generated_openapi_document_lists_our_endpoints_and_is_admin_only()
    {
        var host = SpiHost.Get(sql, "admin-ui");

        var anonymous = await host.Factory.CreateClient().GetAsync("/admin/openapi/v1.json");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var http = host.Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = OAuthTestClient.Basic(TcpFactory.AdminUser, TcpFactory.AdminPassword);
        var response = await http.GetAsync("/admin/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var paths = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["paths"]!.AsObject().Select(p => p.Key).ToList();
        paths.Should().Contain(["/oauth/token", "/scim/v2/Users", "/task-provider/v2/tasks", "/admin/api/tasks"]);
    }
}
