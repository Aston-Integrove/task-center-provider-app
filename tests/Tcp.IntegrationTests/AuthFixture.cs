using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tcp.Domain.Identity;
using Tcp.TestSupport;

namespace Tcp.IntegrationTests;

/// <summary>Shared host (no database) with fake OIDC + XSUAA issuers and an in-memory user directory.</summary>
public sealed class AuthFixture : IDisposable
{
    public const string AliceId = "0b3c7d1e-4f5a-4b6c-8d9e-0a1b2c3d4e5f";
    public const string InactiveId = "99999999-8888-7777-6666-555555555555";
    public const string UnknownId = "ffffffff-ffff-ffff-ffff-ffffffffffff";

    public TestIssuer Oidc { get; } = new();
    public TestIssuer Xsuaa { get; } = new(TestIssuer.XsuaaIssuer);
    public InMemoryUserDirectory Directory { get; } = new();
    public TcpFactory Factory { get; }
    public CapturingLoggerProvider Logs { get; } = new();

    public AuthFixture()
    {
        Directory
            .Add(new DirectoryUser(AliceId, "alice", "alice@example.com", "Alice Example", true))
            .Add(new DirectoryUser(InactiveId, "gone", "gone@example.com", "Gone", false));

        Factory = new TcpFactory(
            issuers: [Oidc, Xsuaa],
            settings: new() { ["Diagnostics:LogAssertionClaims"] = "true" },
            configureServices: s =>
            {
                s.AddSingleton<IUserDirectory>(Directory);
                s.AddLogging(b => b.AddProvider(Logs));
            });
    }

    public HttpClient CreateClient() => Factory.CreateClient();

    public static JsonObject AliceClaims(string idClaim = "user_uuid") => new() { [idClaim] = AliceId, ["email"] = "alice@example.com" };

    public void Dispose()
    {
        Factory.Dispose();
        Logs.Dispose();
    }
}

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _messages = [];
    public IReadOnlyList<string> Messages { get { lock (_messages) return [.. _messages]; } }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
    public void Dispose() { }

    private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._messages) owner._messages.Add(formatter(state, exception));
        }
    }
}
