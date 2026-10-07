using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Tcp.Domain.Tasks;
using Tcp.TestSupport;

namespace Tcp.IntegrationTests;

[Collection(SqlCollection.Name)]
public class SpiConcurrencyTests(SqlServerFixture sql)
{
    private SpiHost Host => SpiHost.Get(sql, "concurrency");

    [Fact] // T004-18: two parallel responses to the same task -> exactly one wins
    public async Task Parallel_responses_to_one_task_have_exactly_one_winner()
    {
        var user = await Host.AddUserAsync();
        var client = await Host.UserAsync(user);
        var tasks = new List<TaskInstance>();
        for (var i = 0; i < 12; i++) tasks.Add(await Host.AddTaskAsync(new TaskSpec { Users = [user] }));

        var results = await Task.WhenAll(tasks.SelectMany(t => new[] { t, t }).Select(async t =>
            (t.Urn, Response: await client.Respond(t.Urn, "approve", "go"))));

        foreach (var group in results.GroupBy(r => r.Urn))
        {
            var statuses = group.Select(r => r.Response.Status).OrderBy(s => (int)s).ToList();
            statuses.Should().Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, group.Key);
            group.Single(r => r.Response.Status == HttpStatusCode.Conflict).Response.ErrorCode
                .Should().BeOneOf("tcp.spi.taskFinal", "tcp.spi.concurrentUpdate");
        }

        var log = await Host.DbAsync(db => db.OperationLog.AsNoTracking().Where(o => o.UserId == user).ToListAsync());
        log.Count(l => l.Outcome == "OK").Should().Be(12);
        log.Count(l => l.Outcome == "REJECTED").Should().Be(12);
    }

    [Fact] // two users racing for the same claim -> one reserves, the other is told so
    public async Task Parallel_claims_have_exactly_one_winner()
    {
        var a = await Host.AddUserAsync();
        var b = await Host.AddUserAsync();
        var ca = await Host.UserAsync(a);
        var cb = await Host.UserAsync(b);
        var tasks = new List<TaskInstance>();
        for (var i = 0; i < 12; i++) tasks.Add(await Host.AddTaskAsync(new TaskSpec { Users = [a, b] }));

        var results = await Task.WhenAll(tasks.SelectMany(t => new[] { (t, ca, a), (t, cb, b) }).Select(async x =>
            (x.t.Urn, x.Item3, Response: await x.Item2.Act(x.t.Urn, "claim"))));

        foreach (var group in results.GroupBy(r => r.Urn))
        {
            var winners = group.Where(r => r.Response.Status == HttpStatusCode.OK).ToList();
            winners.Should().ContainSingle(group.Key);
            group.Single(r => r.Response.Status != HttpStatusCode.OK).Response.Status.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Conflict);

            var stored = await Host.LoadTaskAsync(group.Key);
            stored.Processor.Should().Be(winners[0].Item2);
            stored.Status.Should().Be(TaskStatuses.Reserved);
        }
    }

    [Fact] // modifiedAt stays strictly monotonic per task even under rapid sequential changes
    public async Task Rapid_changes_keep_modified_at_strictly_increasing()
    {
        var user = await Host.AddUserAsync();
        var client = await Host.UserAsync(user);
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [user], Priority = TaskPriorities.Low });

        var stamps = new List<DateTime> { task.ModifiedAt };
        foreach (var code in new[] { "increasePriority", "increasePriority", "claim", "release", "increasePriority" })
        {
            var response = await client.Act(task.Urn, code);
            response.Status.Should().Be(HttpStatusCode.OK, response.RawText);
            stamps.Add(DateTime.Parse(response.Body!["modifiedAt"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal));
        }

        stamps.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }
}

[Collection(SqlCollection.Name)]
public class SpiAsyncResponseTests(SqlServerFixture sql)
{
    private SpiHost Host => SpiHost.Get(sql, "async", new()
    {
        ["Spi:AsyncResponses"] = "true",
        ["Spi:AsyncDelaySeconds"] = "0",
        ["Spi:SimulateFailureCodes:0"] = "reject",
    });

    private async Task<TaskInstance> WaitForAsync(string urn, Func<TaskInstance, bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            var task = await Host.DbAsync(db => db.Tasks.AsNoTracking().Include(t => t.OperationErrors).FirstAsync(t => t.Urn == urn));
            if (condition(task)) return task;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Task {urn} never reached the expected state");
    }

    [Fact] // US-004-7.9
    public async Task Response_is_accepted_with_202_and_completes_in_the_background()
    {
        var user = await Host.AddUserAsync();
        var client = await Host.UserAsync(user);
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [user] });

        var response = await client.Respond(task.Urn, "approve", "ok");

        response.Status.Should().Be(HttpStatusCode.Accepted);
        response.RawText.Should().BeEmpty();
        var done = await WaitForAsync(task.Urn, t => t.Status == TaskStatuses.Completed);
        (done.CompletedBy, done.Processor).Should().Be((user, user));

        var log = await Host.DbAsync(db => db.OperationLog.AsNoTracking().Where(o => o.TaskUrn == task.Urn).OrderBy(o => o.Id).ToListAsync());
        log.Select(l => l.Outcome).Should().Equal("ACCEPTED", "OK");
    }

    [Fact] // simulated failure surfaces as operationErrors on the next pull
    public async Task Simulated_failures_become_operation_errors_and_leave_the_task_open()
    {
        var user = await Host.AddUserAsync();
        var client = await Host.UserAsync(user);
        var tech = await Host.TechAsync();
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [user], ModifiedAt = DateTime.UtcNow.AddMinutes(-5) });

        var response = await client.Respond(task.Urn, "reject", "no thanks");
        response.Status.Should().Be(HttpStatusCode.Accepted);

        var failed = await WaitForAsync(task.Urn, t => t.OperationErrors.Count > 0);
        failed.Status.Should().Be(TaskStatuses.Ready);
        failed.ModifiedAt.Should().BeAfter(task.ModifiedAt);

        var pulled = (await tech.Get($"/tasks?languages=en-US&modifiedAfter={SpiHost.Iso(task.ModifiedAt)}&$top=1000"))
            .Value.Single(t => t!["urn"]!.GetValue<string>() == task.Urn)!;
        var error = pulled["operationErrors"]![0]!;
        (error["code"]!.GetValue<string>(), error["executedBy"]!.GetValue<string>()).Should().Be(("reject", user));
        error["message"]!.GetValue<string>().Should().Contain("reject");
        pulled["status"]!.GetValue<string>().Should().Be("READY");
    }

    [Fact] // validation is still synchronous
    public async Task Invalid_requests_are_rejected_immediately_even_in_async_mode()
    {
        var user = await Host.AddUserAsync();
        var client = await Host.UserAsync(user);
        var stranger = await Host.UserAsync(await Host.AddUserAsync());
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [user] });

        (await stranger.Respond(task.Urn, "approve")).Status.Should().Be(HttpStatusCode.Forbidden);
        (await client.Respond(task.Urn, "reject")).ErrorCode.Should().Be("tcp.spi.commentRequired");
        (await client.Respond(task.Urn, "nonsense")).ErrorCode.Should().Be("tcp.spi.invalidOperation");

        (await Host.LoadTaskAsync(task.Urn)).Status.Should().Be(TaskStatuses.Ready);
    }

    [Fact] // actions are always synchronous
    public async Task Actions_stay_synchronous()
    {
        var user = await Host.AddUserAsync();
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [user] });

        var response = await (await Host.UserAsync(user)).Act(task.Urn, "claim");

        response.Status.Should().Be(HttpStatusCode.OK);
    }
}

[Collection(SqlCollection.Name)]
public class GdprAndGroupDeletionTests(SqlServerFixture sql)
{
    private SpiHost Host => SpiHost.Isolated(sql);

    private static async Task<HttpResponseMessage> ScimAsync(SpiHost host, HttpMethod method, string path, JsonNode? body = null)
    {
        var http = host.Factory.CreateClient();
        var token = await OAuthTestClient.GetScimTokenAsync(http);
        var request = new HttpRequestMessage(method, "/scim/v2" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/scim+json");
        return await http.SendAsync(request);
    }

    private static async Task<Guid> ScimIdOf(SpiHost host, string gid) =>
        await host.DbAsync(db => db.ScimUsers.Where(u => u.GlobalUserId == gid).Select(u => u.Id).SingleAsync());

    private static JsonObject PullOne(SpiResponse pull, string urn) =>
        pull.Value.Single(t => t!["urn"]!.GetValue<string>() == urn)!.AsObject();

    [Fact] // T003-11, FR-SCIM-12, US-003-3.4
    public async Task Deleting_a_user_tombstones_and_anonymises_their_tasks_and_re_publishes_them()
    {
        var host = Host;
        var erased = await host.AddUserAsync();
        var other = await host.AddUserAsync();
        var old = DateTime.UtcNow.AddHours(-3);
        var onlyRecipient = await host.AddTaskAsync(new TaskSpec { LocalId = "g1", Users = [erased], ModifiedAt = old });
        var shared = await host.AddTaskAsync(new TaskSpec { LocalId = "g2", Users = [erased, other], ModifiedAt = old });
        var processing = await host.AddTaskAsync(new TaskSpec
        {
            LocalId = "g3", Users = [erased, other], Processor = erased, Status = TaskStatuses.Reserved, CreatedBy = erased, ModifiedAt = old,
        });
        var finished = await host.AddTaskAsync(new TaskSpec
        {
            LocalId = "g4", Users = [erased], Status = TaskStatuses.Completed, Processor = erased, CompletedBy = erased, CreatedBy = erased, ModifiedAt = old,
        });
        var unrelated = await host.AddTaskAsync(new TaskSpec { LocalId = "g5", Users = [other], ModifiedAt = old });
        var user = await host.UserAsync(erased);
        await user.Act(shared.Urn, "increasePriority"); // leaves an audit row for the user
        await host.DbAsync(db => db.TaskOperationErrors.AddRangeAsync(new TaskOperationError
            { TaskUrn = shared.Urn, ExecutedAt = old, Code = "approve", Message = "boom", ExecutedBy = erased }));
        await host.DbAsync(db => db.SaveChangesAsync());
        var before = DateTime.UtcNow.AddSeconds(-1);

        var delete = await ScimAsync(host, HttpMethod.Delete, $"/Users/{await ScimIdOf(host, erased)}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var tech = await host.TechAsync();
        var pull = await tech.Get($"/tasks?languages=en-US&$top=1000&modifiedAfter={SpiHost.Iso(old.AddSeconds(-1))}");
        pull.Value.Should().HaveCount(5); // nothing was deleted, ever

        var g1 = PullOne(pull, onlyRecipient.Urn);
        g1["status"]!.GetValue<string>().Should().Be("CANCELED"); // tombstone
        g1["recipientUsers"]!.AsArray().Should().BeEmpty();
        g1["validActionCodes"]!.AsArray().Should().BeEmpty();

        var g2 = PullOne(pull, shared.Urn);
        g2["status"]!.GetValue<string>().Should().Be("READY");
        g2["recipientUsers"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal(other);
        g2.ContainsKey("modifiedBy").Should().BeFalse();
        g2.ContainsKey("operationErrors").Should().BeFalse();

        var g3 = PullOne(pull, processing.Urn);
        g3["processor"].Should().BeNull();
        g3.ContainsKey("createdBy").Should().BeFalse();
        g3["recipientUsers"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal(other);

        var g4 = PullOne(pull, finished.Urn);
        g4["status"]!.GetValue<string>().Should().Be("COMPLETED");
        g4.ContainsKey("completedBy").Should().BeFalse();
        g4["processor"].Should().BeNull();

        // every touched task has a fresh modifiedAt so Task Center re-reads it; the unrelated one is untouched
        foreach (var urn in new[] { onlyRecipient.Urn, shared.Urn, processing.Urn, finished.Urn })
            DateTime.Parse(PullOne(pull, urn)["modifiedAt"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal)
                .Should().BeAfter(before, urn);
        PullOne(pull, unrelated.Urn)["modifiedAt"]!.GetValue<string>().Should().Be(SpiHost.Iso(unrelated.ModifiedAt));

        // the audit trail no longer identifies the person
        var audit = await host.DbAsync(db => db.OperationLog.AsNoTracking().ToListAsync());
        audit.Should().NotContain(a => a.UserId == erased);
        audit.Should().Contain(a => a.UserId == "erased");
        (await host.DbAsync(db => db.Tasks.CountAsync(t => t.CreatedBy == erased || t.ModifiedBy == erased || t.Processor == erased || t.CompletedBy == erased)))
            .Should().Be(0);
    }

    [Fact] // US-003-4.4: deleting a group removes it from task recipient groups with a modifiedAt bump
    public async Task Deleting_a_group_removes_it_from_tasks_and_re_publishes_them()
    {
        var host = Host;
        var old = DateTime.UtcNow.AddHours(-2);
        var member = await host.AddUserAsync(groups: ["DOOMED", "SURVIVOR"]);
        var onlyDoomed = await host.AddTaskAsync(new TaskSpec { LocalId = "d1", Groups = ["DOOMED"], ModifiedAt = old });
        var both = await host.AddTaskAsync(new TaskSpec { LocalId = "d2", Groups = ["DOOMED", "SURVIVOR"], ModifiedAt = old });
        var unrelated = await host.AddTaskAsync(new TaskSpec { LocalId = "d3", Groups = ["SURVIVOR"], ModifiedAt = old });
        var groupId = await host.DbAsync(db => db.ScimGroups.Where(g => g.DisplayName == "DOOMED").Select(g => g.Id).SingleAsync());
        var before = DateTime.UtcNow.AddSeconds(-1);

        (await ScimAsync(host, HttpMethod.Delete, $"/Groups/{groupId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var tech = await host.TechAsync();
        var pull = await tech.Get($"/tasks?languages=en-US&$top=1000&modifiedAfter={SpiHost.Iso(old.AddSeconds(-1))}");
        PullOne(pull, onlyDoomed.Urn)["recipientGroups"]!.AsArray().Should().BeEmpty();
        PullOne(pull, both.Urn)["recipientGroups"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("SURVIVOR");
        DateTime.Parse(PullOne(pull, onlyDoomed.Urn)["modifiedAt"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal).Should().BeAfter(before);
        DateTime.Parse(PullOne(pull, both.Urn)["modifiedAt"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal).Should().BeAfter(before);
        PullOne(pull, unrelated.Urn)["modifiedAt"]!.GetValue<string>().Should().Be(SpiHost.Iso(unrelated.ModifiedAt));
        member.Should().NotBeNullOrEmpty();
    }

    [Fact] // US-003-3.5 with real task data behind it
    public async Task Global_user_id_cannot_change_once_tasks_reference_the_user()
    {
        var host = Host;
        var gid = await host.AddUserAsync();
        var unreferencedGid = await host.AddUserAsync();
        await host.AddTaskAsync(new TaskSpec { Users = [gid] });
        var userName = await host.DbAsync(db => db.ScimUsers.Where(u => u.GlobalUserId == gid).Select(u => u.UserName).SingleAsync());
        var scimId = await ScimIdOf(host, gid);
        var freeUserName = await host.DbAsync(db => db.ScimUsers.Where(u => u.GlobalUserId == unreferencedGid).Select(u => u.UserName).SingleAsync());
        var freeId = await ScimIdOf(host, unreferencedGid);

        object Payload(string name, string uuid) => new { schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" }, userName = name,
            externalId = uuid, active = true };

        var rejected = await ScimAsync(host, HttpMethod.Put, $"/Users/{scimId}", JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Payload(userName, Guid.NewGuid().ToString()))));
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await rejected.Content.ReadAsStringAsync()).Should().Contain("mutability");

        var allowed = await ScimAsync(host, HttpMethod.Put, $"/Users/{freeId}", JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Payload(freeUserName, Guid.NewGuid().ToString()))));
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

[Collection(SqlCollection.Name)]
[Trait("Category", "Performance")]
public class SpiPerformanceTests(SqlServerFixture sql)
{
    [Fact] // T004-21 (in-process counterpart of tests/perf/k6-pull.js)
    public async Task Pages_of_1000_rich_tasks_are_served_well_within_budget()
    {
        const int total = 5000;
        var host = SpiHost.Isolated(sql);
        var users = new[] { await host.AddUserAsync(), await host.AddUserAsync(), await host.AddUserAsync() };
        var start = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        await host.AddTasksAsync(Enumerable.Range(0, total).Select(i => SpiHost.BuildTask(new TaskSpec
        {
            LocalId = $"perf-{i:D6}", ModifiedAt = start.AddMilliseconds(i / 7), Users = users, Groups = ["PERF_GROUP"],
            Attributes = new Dictionary<string, string>
            {
                ["amount"] = "1234.5", ["currency"] = "ZAR", ["requester"] = "L. Robbins", ["costCenter"] = "CC-1000", ["neededBy"] = "2026-10-31",
            },
        })));
        var tech = await host.TechAsync();

        var seen = new HashSet<string>();
        var timings = new List<double>();
        DateTime? after = start;
        string? lastId = null;
        while (true)
        {
            var path = $"/tasks?languages=en-US,de-DE&$top=1000&modifiedAfter={SpiHost.Iso(after!.Value)}" + (lastId is null ? "" : $"&lastId={Uri.EscapeDataString(lastId)}");
            var sw = Stopwatch.StartNew();
            var page = await tech.Get(path);
            sw.Stop();
            page.Status.Should().Be(HttpStatusCode.OK);
            if (page.Value.Count == 0) break;
            timings.Add(sw.Elapsed.TotalSeconds);
            foreach (var t in page.Value) seen.Add(t!["urn"]!.GetValue<string>()).Should().BeTrue("each task exactly once");
            lastId = page.Value[^1]!["urn"]!.GetValue<string>();
            after = DateTime.Parse(page.Value[^1]!["modifiedAt"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        }

        seen.Should().HaveCount(total);
        timings.Should().HaveCount(5);
        // NFR-004-01: p95 < 2 s per 1000-task page (SAP budget is 30 s). The first page includes JIT warm-up.
        timings.Skip(1).Should().OnlyContain(t => t < 2.0, $"page timings (s): {string.Join(", ", timings.Select(t => t.ToString("F2")))}");
        timings[0].Should().BeLessThan(10);
    }
}
