using Tcp.Api.Diagnostics;

namespace Tcp.UnitTests;

public class RequestLogBufferTests
{
    private static RequestLogEntry Entry(int n, string path = "/task-provider/v2/tasks", int status = 200) =>
        new(DateTimeOffset.UnixEpoch.AddSeconds(n), $"c{n}", "GET", path, "", status, 1, null,
            new Dictionary<string, string>(), "", new Dictionary<string, string>(), "");

    [Fact]
    public void Buffer_is_capped_and_returns_newest_first()
    {
        var buffer = new RequestLogBuffer(3);
        for (var i = 1; i <= 5; i++) buffer.Add(Entry(i));

        buffer.Snapshot().Select(e => e.CorrelationId).Should().Equal("c5", "c4", "c3");
    }

    [Fact]
    public void Filters_by_prefix_status_and_limit()
    {
        var buffer = new RequestLogBuffer(10);
        buffer.Add(Entry(1, "/scim/v2/Users"));
        buffer.Add(Entry(2, "/task-provider/v2/tasks", 401));
        buffer.Add(Entry(3, "/task-provider/v2/tasks"));
        buffer.Add(Entry(4, "/task-provider/v2/capabilities"));

        buffer.Snapshot("/scim").Should().HaveCount(1);
        buffer.Snapshot("/task-provider", status: 401).Select(e => e.CorrelationId).Should().Equal("c2");
        buffer.Snapshot("/task-provider", limit: 2).Select(e => e.CorrelationId).Should().Equal("c4", "c3");
    }

    [Fact]
    public void Empty_buffer_returns_empty() => new RequestLogBuffer(5).Snapshot().Should().BeEmpty();
}
