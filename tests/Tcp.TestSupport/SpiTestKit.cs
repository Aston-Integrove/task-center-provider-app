using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tcp.Domain.Identity;
using Tcp.Domain.Tasks;
using Tcp.Infrastructure.Persistence;

namespace Tcp.TestSupport;

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _messages = [];
    public IReadOnlyList<string> Messages { get { lock (_messages) return [.. _messages]; } }
    public void Clear() { lock (_messages) _messages.Clear(); }

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

/// <summary>Everything needed to insert a task directly into the database for a test.</summary>
public sealed record TaskSpec
{
    public string Definition { get; init; } = "PR_APPROVAL";
    public string? LocalId { get; init; }
    public DateTime? ModifiedAt { get; init; }
    public DateTime? CreatedAt { get; init; }
    public string Status { get; init; } = TaskStatuses.Ready;
    public string Priority { get; init; } = TaskPriorities.Medium;
    public string? Subject { get; init; }
    public string? SubjectDe { get; init; }
    public IReadOnlyList<string> Users { get; init; } = [];
    public IReadOnlyList<string> Groups { get; init; } = [];
    public string? Processor { get; init; }
    public string? CreatedBy { get; init; }
    public string? CompletedBy { get; init; }
    public DateTime? DueAt { get; init; }
    public IReadOnlyDictionary<string, string>? Attributes { get; init; }
    public IReadOnlyList<TaskDescription>? Descriptions { get; init; }
}

public sealed record SpiResponse(HttpStatusCode Status, JsonNode? Body, HttpResponseMessage Raw, string RawText)
{
    public string? ErrorCode => Body?["error"]?["code"]?.GetValue<string>();
    public string? ErrorTarget => Body?["error"]?["target"]?.GetValue<string>();
    public string? ErrorMessage => Body?["error"]?["message"]?.GetValue<string>();
    public JsonArray Value => Body!["value"]!.AsArray();
}

public sealed class SpiClient(HttpClient http, string basePath = "/task-provider/v2")
{
    public HttpClient Http { get; } = http;
    public string BasePath { get; } = basePath;

    public SpiClient WithBasePath(string path) => new(Http, path);

    public async Task<SpiResponse> SendAsync(HttpMethod method, string path, JsonNode? body = null, string? acceptLanguage = null)
    {
        var request = new HttpRequestMessage(method, BasePath + path);
        if (acceptLanguage is not null) request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonNode? json = null;
        if (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('['))
        {
            try { json = JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { }
        }
        return new SpiResponse(response.StatusCode, json, response, text);
    }

    public Task<SpiResponse> Get(string path, string? acceptLanguage = null) => SendAsync(HttpMethod.Get, path, null, acceptLanguage);
    public Task<SpiResponse> Post(string path, JsonNode body, string? acceptLanguage = null) => SendAsync(HttpMethod.Post, path, body, acceptLanguage);

    public Task<SpiResponse> Respond(string taskUrn, string code, string? comment = null, string? reasonCode = null, string languages = "en-US", string? acceptLanguage = null) =>
        Post($"/tasks/{Uri.EscapeDataString(taskUrn)}/response?languages={languages}", Op(code, comment, reasonCode), acceptLanguage);

    public Task<SpiResponse> Act(string taskUrn, string code, string? comment = null, string? reasonCode = null, string languages = "en-US", string? acceptLanguage = null) =>
        Post($"/tasks/{Uri.EscapeDataString(taskUrn)}/action?languages={languages}", Op(code, comment, reasonCode), acceptLanguage);

    public static JsonObject Op(string code, string? comment, string? reasonCode) =>
        new() { ["code"] = code, ["comment"] = comment, ["reasonCode"] = reasonCode };
}

/// <summary>
/// An API host with a database of its own (one per key) and the real SPI wiring. Users and tasks are inserted
/// directly so the tests control timestamps, ids and states exactly.
/// </summary>
public sealed class SpiHost
{
    public const string AppId = "integrove";
    public const string InstanceId = "tcproto";
    public const string Tenant = "dev";

    private static readonly ConcurrentDictionary<string, Lazy<SpiHost>> Hosts = new();
    private readonly ConcurrentDictionary<string, string> _userTokens = new();
    private string? _techToken;

    public TcpFactory Factory { get; }
    public CapturingLoggerProvider Logs { get; } = new();
    public static TestIssuer Oidc { get; } = new();

    private SpiHost(string connectionString, Dictionary<string, string?> settings)
    {
        var logs = Logs;
        Factory = new TcpFactory(connectionString, settings,
            services => services.AddLogging(b => b.AddProvider(logs)),
            issuers: [Oidc]);
    }

    public static SpiHost Get(SqlServerFixture sql, string key, Dictionary<string, string?>? settings = null) =>
        Hosts.GetOrAdd(key, _ => new Lazy<SpiHost>(() =>
            new SpiHost(sql.ConnectionString("spi_" + key.Replace('-', '_')), settings ?? []))).Value;

    /// <summary>A brand-new database, for tests that look at "all tasks" and must not see anyone else's.</summary>
    public static SpiHost Isolated(SqlServerFixture sql, Dictionary<string, string?>? settings = null) =>
        Get(sql, "iso_" + Guid.NewGuid().ToString("N")[..12], settings);

    public static string TaskUrn(string localId) => Urn.Build(UrnKind.Task, AppId, InstanceId, Tenant, localId).Value;
    public static string DefinitionUrn(string localId) => Urn.Build(UrnKind.TaskDefinition, AppId, InstanceId, Tenant, localId).Value;

    // ---- clients ------------------------------------------------------------------------------

    public async Task<SpiClient> TechAsync(string basePath = "/task-provider/v2")
    {
        var http = Factory.CreateClient();
        _techToken ??= await OAuthTestClient.GetTechTokenAsync(http);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _techToken);
        return new SpiClient(http, basePath);
    }

    public async Task<SpiClient> UserAsync(string globalUserId, string basePath = "/task-provider/v2")
    {
        var http = Factory.CreateClient();
        if (!_userTokens.TryGetValue(globalUserId, out var token))
        {
            token = await OAuthTestClient.GetUserTokenAsync(http, Oidc.Mint(new JsonObject { ["user_uuid"] = globalUserId }));
            _userTokens[globalUserId] = token;
        }
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new SpiClient(http, basePath);
    }

    public SpiClient Anonymous(string basePath = "/task-provider/v2") => new(Factory.CreateClient(), basePath);

    // ---- database -----------------------------------------------------------------------------

    public async Task<T> DbAsync<T>(Func<TcpDbContext, Task<T>> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        _ = Factory.Server; // make sure the host (and its migrations) is running
        return await action(scope.ServiceProvider.GetRequiredService<TcpDbContext>());
    }

    public Task DbAsync(Func<TcpDbContext, Task> action) => DbAsync<int>(async db => { await action(db); return 0; });

    public async Task<string> AddUserAsync(bool active = true, string? globalUserId = null, string? email = null, string? name = null, params string[] groups)
    {
        var gid = globalUserId ?? Guid.NewGuid().ToString("D");
        name ??= "user-" + Guid.NewGuid().ToString("N")[..8];
        await DbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var user = new ScimUser
            {
                Id = Guid.NewGuid(), UserName = name, GlobalUserId = gid, DisplayName = name, PrimaryEmail = email,
                Active = active, Created = now, LastModified = now,
            };
            db.ScimUsers.Add(user);
            foreach (var group in groups)
            {
                var existing = db.ScimGroups.FirstOrDefault(g => g.DisplayName == group);
                if (existing is null)
                {
                    existing = new ScimGroup { Id = Guid.NewGuid(), DisplayName = group, Created = now, LastModified = now };
                    db.ScimGroups.Add(existing);
                    await db.SaveChangesAsync();
                }
                db.ScimGroupMembers.Add(new ScimGroupMember { GroupId = existing.Id, UserId = user.Id });
            }
            await db.SaveChangesAsync();
        });
        return gid;
    }

    public static TaskInstance BuildTask(TaskSpec spec)
    {
        var localId = spec.LocalId ?? "T-" + Guid.NewGuid().ToString("N")[..10];
        var urn = TaskUrn(localId);
        var now = TaskInstance.TruncateToMs(DateTime.UtcNow);
        var subject = new JsonArray(new JsonObject { ["languageCode"] = "en-US", ["text"] = spec.Subject ?? $"Approve purchase requisition {localId}" });
        if (spec.SubjectDe is not null) subject.Add(new JsonObject { ["languageCode"] = "de-DE", ["text"] = spec.SubjectDe });

        var task = new TaskInstance
        {
            Urn = urn, LocalId = localId, DefinitionUrn = DefinitionUrn(spec.Definition),
            Status = spec.Status, Priority = spec.Priority, SubjectJson = subject.ToJsonString(),
            DescriptionJson = spec.Descriptions is null ? null : new JsonArray(spec.Descriptions.Select(d => (JsonNode)new JsonObject
            {
                ["languageCode"] = d.LanguageCode, ["contentType"] = d.ContentType, ["body"] = d.Body,
            }).ToArray()).ToJsonString(),
            CreatedAt = TaskInstance.TruncateToMs(spec.CreatedAt ?? now.AddMinutes(-5)),
            CreatedBy = spec.CreatedBy,
            ModifiedAt = TaskInstance.TruncateToMs(spec.ModifiedAt ?? now),
            Processor = spec.Processor, DueAt = spec.DueAt, CompletedBy = spec.CompletedBy,
            CompletedAt = TaskStatuses.IsFinal(spec.Status) ? TaskInstance.TruncateToMs(spec.ModifiedAt ?? now) : null,
        };
        foreach (var user in spec.Users) task.RecipientUsers.Add(new TaskRecipientUser { TaskUrn = urn, GlobalUserId = user });
        foreach (var group in spec.Groups) task.RecipientGroups.Add(new TaskRecipientGroup { TaskUrn = urn, GroupName = group });
        foreach (var (code, value) in spec.Attributes ?? new Dictionary<string, string>())
            task.CustomAttributes.Add(new TaskCustomAttribute { TaskUrn = urn, Code = code, Value = value });
        return task;
    }

    public async Task<TaskInstance> AddTaskAsync(TaskSpec spec)
    {
        var task = BuildTask(spec);
        await AddTasksAsync([task]);
        return task;
    }

    /// <summary>Inserts many tasks with one SaveChanges (EF batches the INSERTs).</summary>
    public Task AddTasksAsync(IEnumerable<TaskInstance> tasks) => DbAsync(async db =>
    {
        foreach (var chunk in tasks.Chunk(500))
        {
            db.Tasks.AddRange(chunk);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
    });

    public Task<TaskInstance> LoadTaskAsync(string urn) =>
        DbAsync(db => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(
            Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.Include(
                Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.Include(
                    Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(db.Tasks), t => t.RecipientUsers),
                t => t.RecipientGroups),
            t => t.Urn == urn));

    public static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
