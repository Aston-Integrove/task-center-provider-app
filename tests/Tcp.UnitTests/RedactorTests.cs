using System.Text.Json.Nodes;
using Tcp.Api.Diagnostics;

namespace Tcp.UnitTests;

public class RedactorTests
{
    [Theory]
    [InlineData("Authorization", "Bearer abc")]
    [InlineData("authorization", "Basic Zm9vOmJhcg==")]
    [InlineData("Cookie", "a=b")]
    public void Sensitive_headers_are_masked(string name, string value) =>
        Redactor.RedactHeader(name, value).Should().Be("***");

    [Fact]
    public void Other_headers_pass_through() =>
        Redactor.RedactHeader("Accept-Language", "en-US").Should().Be("en-US");

    [Fact]
    public void Form_secret_and_assertion_are_masked()
    {
        var body = "grant_type=jwt-bearer&client_id=tc-pp&client_secret=s3cr3t&assertion=eyJhbGciOi.payload.sig&scope=spi.user";
        var result = Redactor.RedactBody(body, "application/x-www-form-urlencoded");

        result.Should().NotContain("s3cr3t").And.NotContain("eyJhbGciOi");
        result.Should().Contain("client_secret=***").And.Contain("assertion=***");
        result.Should().Contain("client_id=tc-pp").And.Contain("scope=spi.user");
    }

    [Fact]
    public void Json_access_token_is_masked_including_nested_and_arrays()
    {
        var body = """{"access_token":"tok123","token_type":"Bearer","nested":{"client_secret":"x"},"items":[{"refresh_token":"r"}]}""";
        var result = Redactor.RedactBody(body, "application/json");

        result.Should().NotContain("tok123").And.NotContain("\"x\"").And.NotContain("\"r\"");
        var node = JsonNode.Parse(result)!;
        node["access_token"]!.GetValue<string>().Should().Be("***");
        node["token_type"]!.GetValue<string>().Should().Be("Bearer");
        node["nested"]!["client_secret"]!.GetValue<string>().Should().Be("***");
        node["items"]![0]!["refresh_token"]!.GetValue<string>().Should().Be("***");
    }

    [Fact]
    public void Truncated_json_still_masks_secrets()
    {
        var body = """{"keep":"a","access_token":"supersecret","more":"unterminated""";
        var result = Redactor.RedactBody(body, "application/json");
        result.Should().NotContain("supersecret").And.Contain("\"keep\":\"a\"");
    }

    [Fact]
    public void Query_secrets_are_masked()
    {
        var result = Redactor.RedactQuery("?languages=en-US&access_token=abc&$top=10");
        result.Should().Contain("access_token=***").And.Contain("languages=en-US").And.NotContain("abc");
    }

    [Fact]
    public void Empty_inputs_are_safe()
    {
        Redactor.RedactBody(null, null).Should().BeEmpty();
        Redactor.RedactQuery(null).Should().BeEmpty();
    }
}
