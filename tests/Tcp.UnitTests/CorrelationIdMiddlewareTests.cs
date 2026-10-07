using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Tcp.Api.Diagnostics;

namespace Tcp.UnitTests;

public class CorrelationIdMiddlewareTests
{
    private static async Task<DefaultHttpContext> RunAsync(string? inbound)
    {
        var ctx = new DefaultHttpContext();
        if (inbound is not null) ctx.Request.Headers[CorrelationIdMiddleware.HeaderName] = inbound;
        var mw = new CorrelationIdMiddleware(async c => await c.Response.StartAsync(), NullLogger<CorrelationIdMiddleware>.Instance);
        await mw.InvokeAsync(ctx);
        return ctx;
    }

    [Fact]
    public async Task Generates_id_when_missing()
    {
        var ctx = await RunAsync(null);
        ctx.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString().Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public async Task Echoes_valid_inbound_id()
    {
        var ctx = await RunAsync("abc-123");
        ctx.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString().Should().Be("abc-123");
        CorrelationIdMiddleware.Get(ctx).Should().Be("abc-123");
    }

    [Fact]
    public async Task Replaces_unsafe_inbound_id()
    {
        var ctx = await RunAsync("bad id\r\nX-Evil: 1");
        ctx.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString().Should().MatchRegex("^[0-9a-f]{32}$");
    }
}
