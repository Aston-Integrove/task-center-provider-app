using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Tcp.Domain.Tasks;
using Tcp.TestSupport;

namespace Tcp.IntegrationTests;

[Collection(SqlCollection.Name)]
public class SpiPullTests(SqlServerFixture sql)
{
    private static readonly DateTime T1 = new(2026, 10, 1, 8, 0, 0, 100, DateTimeKind.Utc);
    private static readonly DateTime T2 = T1.AddHours(1);
    private static readonly DateTime T3 = T1.AddHours(2);

    private static string Q(DateTime? after, string? lastId = null, int? top = null)
    {
        var q = "languages=en-US";
        if (after is not null) q += "&modifiedAfter=" + SpiHost.Iso(after.Value);
        if (lastId is not null) q += "&lastId=" + Uri.EscapeDataString(lastId);
        if (top is not null) q += "&$top=" + top;
        return "/tasks?" + q;
    }

    private static string[] LocalIds(SpiResponse r) => r.Value.Select(t => t!["localId"]!.GetValue<string>()).ToArray();

    private async Task<(SpiHost Host, SpiClient Tech)> SeedFourAsync()
    {
        var host = SpiHost.Isolated(sql);
        await host.AddTaskAsync(new TaskSpec { LocalId = "pa", ModifiedAt = T1 });
        await host.AddTaskAsync(new TaskSpec { LocalId = "pb", ModifiedAt = T2 });
        await host.AddTaskAsync(new TaskSpec { LocalId = "pc", ModifiedAt = T2 });
        await host.AddTaskAsync(new TaskSpec { LocalId = "pd", ModifiedAt = T3 });
        return (host, await host.TechAsync());
    }

    [Fact] // US-004-3.1
    public async Task Keyset_with_last_id_continues_after_the_last_task()
    {
        var (_, tech) = await SeedFourAsync();

        var page = await tech.Get(Q(T2, SpiHost.TaskUrn("pb")));

        page.Status.Should().Be(HttpStatusCode.OK);
        LocalIds(page).Should().Equal("pc", "pd");
    }

    [Fact] // US-004-3.2
    public async Task Without_last_id_the_comparison_is_greater_or_equal()
    {
        var (_, tech) = await SeedFourAsync();

        LocalIds(await tech.Get(Q(T2))).Should().Equal("pb", "pc", "pd");
        LocalIds(await tech.Get(Q(T1))).Should().Equal("pa", "pb", "pc", "pd");
        LocalIds(await tech.Get(Q(T3))).Should().Equal("pd");
    }

    [Fact] // US-004-3.4 + 3.8 + no modifiedAfter
    public async Task Empty_page_means_done_and_age_is_never_filtered()
    {
        var (host, tech) = await SeedFourAsync();
        await host.AddTaskAsync(new TaskSpec { LocalId = "ancient", ModifiedAt = new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc) });

        var done = await tech.Get(Q(T3.AddMilliseconds(1)));
        done.Status.Should().Be(HttpStatusCode.OK);
        done.Value.Should().BeEmpty();

        LocalIds(await tech.Get(Q(new DateTime(2018, 1, 1, 0, 0, 0, DateTimeKind.Utc)))).Should().Equal("ancient", "pa", "pb", "pc", "pd");
        LocalIds(await tech.Get("/tasks?languages=en-US")).Should().Equal("ancient", "pa", "pb", "pc", "pd"); // no modifiedAfter = from the start
    }

    [Fact] // US-004-3.5: tombstones and final states are included
    public async Task All_statuses_are_returned()
    {
        var host = SpiHost.Isolated(sql);
        foreach (var (id, status) in new[]
                 {
                     ("s1", TaskStatuses.Ready), ("s2", TaskStatuses.Reserved), ("s3", TaskStatuses.Completed),
                     ("s4", TaskStatuses.Canceled), ("s5", TaskStatuses.Inactive), ("s6", TaskStatuses.ForResubmission), ("s7", TaskStatuses.InProgress),
                 })
            await host.AddTaskAsync(new TaskSpec { LocalId = id, Status = status, ModifiedAt = T1 });

        var page = await (await host.TechAsync()).Get(Q(T1));

        page.Value.Select(t => t!["status"]!.GetValue<string>()).Should().BeEquivalentTo(
            TaskStatuses.Ready, TaskStatuses.Reserved, TaskStatuses.Completed, TaskStatuses.Canceled,
            TaskStatuses.Inactive, TaskStatuses.ForResubmission, TaskStatuses.InProgress);
        page.Value.Single(t => t!["localId"]!.GetValue<string>() == "s4")!["validActionCodes"]!.AsArray().Should().BeEmpty();
    }

    [Theory] // US-004-3.6
    [InlineData("modifiedAfter=2026-10-01T08:00:00Z", "modifiedAfter")]          // no milliseconds
    [InlineData("modifiedAfter=2026-10-01T08:00:00.1Z", "modifiedAfter")]
    [InlineData("modifiedAfter=2026-10-01T08:00:00.100", "modifiedAfter")]        // no Z
    [InlineData("modifiedAfter=2026-10-01T08:00:00.100%2B02:00", "modifiedAfter")] // offset instead of Z
    [InlineData("modifiedAfter=2026-13-01T08:00:00.100Z", "modifiedAfter")]
    [InlineData("modifiedAfter=2026-02-30T08:00:00.100Z", "modifiedAfter")]
    [InlineData("modifiedAfter=yesterday", "modifiedAfter")]
    [InlineData("modifiedAfter=", "modifiedAfter")]
    [InlineData("lastId=urn:sap.odm.bpm.task:integrove:tcproto:dev:x", "lastId")]   // lastId without modifiedAfter
    [InlineData("modifiedAfter=2026-10-01T08:00:00.100Z&$top=0", "$top")]
    [InlineData("modifiedAfter=2026-10-01T08:00:00.100Z&$top=1001", "$top")]
    [InlineData("modifiedAfter=2026-10-01T08:00:00.100Z&$top=ten", "$top")]
    public async Task Invalid_pull_parameters_are_400(string query, string target)
    {
        var host = SpiHost.Get(sql, "shared");
        var tech = await host.TechAsync();

        var response = await tech.Get("/tasks?languages=en-US&" + query);

        response.Status.Should().Be(HttpStatusCode.BadRequest);
        response.ErrorCode.Should().Be("tcp.spi.invalidParameter");
        response.ErrorTarget.Should().Be(target);
    }

    [Fact]
    public async Task Languages_are_required_for_pulls()
    {
        var tech = await SpiHost.Get(sql, "shared").TechAsync();
        var response = await tech.Get("/tasks?modifiedAfter=" + SpiHost.Iso(T1));
        response.ErrorTarget.Should().Be("languages");
    }

    [Fact] // US-004-3.6: default $top is 100, max 1000
    public async Task Default_page_size_is_100_and_max_is_1000()
    {
        var host = SpiHost.Isolated(sql);
        await host.AddTasksAsync(Enumerable.Range(0, 1010).Select(i =>
            SpiHost.BuildTask(new TaskSpec { LocalId = $"bulk{i:D5}", ModifiedAt = T1 })));
        var tech = await host.TechAsync();

        (await tech.Get(Q(T1))).Value.Should().HaveCount(100);
        (await tech.Get(Q(T1, top: 1000))).Value.Should().HaveCount(1000);
        (await tech.Get(Q(T1, top: 7))).Value.Should().HaveCount(7);
    }

    [Fact] // milliseconds matter: equality in the keyset is exact
    public async Task Millisecond_boundaries_are_exact()
    {
        var host = SpiHost.Isolated(sql);
        await host.AddTaskAsync(new TaskSpec { LocalId = "ms1", ModifiedAt = T1 });
        await host.AddTaskAsync(new TaskSpec { LocalId = "ms2", ModifiedAt = T1.AddMilliseconds(1) });
        var tech = await host.TechAsync();

        LocalIds(await tech.Get(Q(T1))).Should().Equal("ms1", "ms2");
        LocalIds(await tech.Get(Q(T1.AddMilliseconds(1)))).Should().Equal("ms2");
        LocalIds(await tech.Get(Q(T1, SpiHost.TaskUrn("ms1")))).Should().Equal("ms2");
        LocalIds(await tech.Get(Q(T1.AddMilliseconds(1), SpiHost.TaskUrn("ms2")))).Should().BeEmpty();
    }

    [Fact] // T004-02: ordinal ordering through the API
    public async Task Pull_orders_urns_ordinally_within_one_timestamp()
    {
        var host = SpiHost.Isolated(sql);
        foreach (var id in new[] { "a", "Z", "_", "A", "0", "m", "a-b", "a.b", "a_b" })
            await host.AddTaskAsync(new TaskSpec { LocalId = id, ModifiedAt = T1 });
        var tech = await host.TechAsync();

        var ids = LocalIds(await tech.Get(Q(T1)));

        ids.Should().Equal(ids.OrderBy(i => SpiHost.TaskUrn(i), StringComparer.Ordinal));
        ids.Take(3).Should().Equal("0", "A", "Z");
        ids.Should().Equal("0", "A", "Z", "_", "a", "a-b", "a.b", "a_b", "m");
    }

    [Fact] // T004-11: the SQL has the keyset shape and the page costs a constant number of queries
    public async Task Generated_sql_is_a_keyset_range_scan_with_set_based_child_loading()
    {
        var host = SpiHost.Isolated(sql, new() { ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Information" });
        var user = await host.AddUserAsync();
        await host.AddTasksAsync(Enumerable.Range(0, 60).Select(i => SpiHost.BuildTask(new TaskSpec
        {
            LocalId = $"sql{i:D3}", ModifiedAt = T1.AddMilliseconds(i / 10), Users = [user], Groups = ["G1"],
            Attributes = new Dictionary<string, string> { ["amount"] = "1", ["currency"] = "ZAR" },
        })));
        var tech = await host.TechAsync();

        async Task<IReadOnlyList<string>> CommandsFor(int top)
        {
            host.Logs.Clear();
            (await tech.Get(Q(T1, SpiHost.TaskUrn("sql000"), top))).Status.Should().Be(HttpStatusCode.OK);
            return host.Logs.Messages.Where(m => m.Contains("Executed DbCommand")).ToList();
        }

        var small = await CommandsFor(5);
        var large = await CommandsFor(50);

        small.Count.Should().Be(large.Count, "child rows are loaded with set-based queries, not per task");
        small.Count.Should().BeLessThanOrEqualTo(8);

        var main = small.Single(m => m.Contains("FROM [tc].[TaskInstance]") && m.Contains("ORDER BY"));
        main.Should().Contain("TOP(");
        main.Should().Contain("ORDER BY [t].[ModifiedAt], [t].[Urn]");
        main.Should().MatchRegex(@"\[t\]\.\[ModifiedAt\] > @");
        main.Should().MatchRegex(@"\[t\]\.\[ModifiedAt\] = @.*\[t\]\.\[Urn\] > @");
        small.Count(m => m.Contains("FROM [tc].[TaskRecipientUser]")).Should().Be(1);
        small.Count(m => m.Contains("FROM [tc].[TaskRecipientGroup]")).Should().Be(1);
        small.Count(m => m.Contains("FROM [tc].[TaskCustomAttribute]")).Should().Be(1);
        small.Count(m => m.Contains("FROM [tc].[TaskOperationError]")).Should().Be(1);
    }

    [Fact] // user tokens may pull too (E4 is "tech or user")
    public async Task User_tokens_can_pull_as_well()
    {
        var host = SpiHost.Isolated(sql);
        await host.AddTaskAsync(new TaskSpec { LocalId = "u1", ModifiedAt = T1 });
        var user = await host.UserAsync(await host.AddUserAsync());

        LocalIds(await user.Get(Q(T1))).Should().Equal("u1");
    }
}

[Collection(SqlCollection.Name)]
public class SpiPullPropertyTests(SqlServerFixture sql)
{
    private static readonly DateTime Base = new(2026, 10, 1, 8, 0, 0, 0, DateTimeKind.Utc);
    private const string IdChars = "ABCXYZabcxyz0189_.-";

    private static async Task<List<string>> ScrollAsync(SpiClient tech, DateTime? start, Random random, int maxTop)
    {
        var urns = new List<string>();
        DateTime? after = start;
        string? lastId = null;
        for (var guard = 0; guard < 10_000; guard++)
        {
            var top = random.Next(1, maxTop + 1);
            var path = "/tasks?languages=en-US&$top=" + top;
            if (after is not null) path += "&modifiedAfter=" + SpiHost.Iso(after.Value) + (lastId is null ? "" : "&lastId=" + Uri.EscapeDataString(lastId));
            var response = await tech.Get(path);
            response.Status.Should().Be(HttpStatusCode.OK, response.RawText);
            if (response.Value.Count == 0) return urns;

            foreach (var task in response.Value) urns.Add(task!["urn"]!.GetValue<string>());
            var last = response.Value[^1]!;
            after = DateTime.Parse(last["modifiedAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
            lastId = last["urn"]!.GetValue<string>();
        }
        throw new InvalidOperationException("Pull never terminated");
    }

    [Fact] // T004-12: random data, heavy timestamp collisions, random page sizes
    public async Task Random_scrolls_visit_every_task_exactly_once_in_order()
    {
        var host = SpiHost.Isolated(sql);
        var tech = await host.TechAsync();

        for (var seed = 1; seed <= 15; seed++)
        {
            var random = new Random(seed);
            await host.DbAsync(db => db.Tasks.ExecuteDeleteAsync());

            var timestampCount = random.Next(1, 7);
            var stamps = Enumerable.Range(0, timestampCount).Select(_ => Base.AddMilliseconds(random.Next(0, 4) * 1000 + random.Next(0, 3))).ToArray();
            var taskCount = random.Next(15, 90);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            while (ids.Count < taskCount)
                ids.Add(string.Concat(Enumerable.Range(0, random.Next(1, 9)).Select(_ => IdChars[random.Next(IdChars.Length)])));

            var tasks = ids.Select(id => SpiHost.BuildTask(new TaskSpec { LocalId = id, ModifiedAt = stamps[random.Next(stamps.Length)] })).ToList();
            var dupes = tasks.GroupBy(t => t.Urn, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            dupes.Should().BeEmpty($"seed {seed} generated duplicate urns");
            await host.AddTasksAsync(tasks);

            var expected = tasks.OrderBy(t => t.ModifiedAt).ThenBy(t => t.Urn, StringComparer.Ordinal).Select(t => t.Urn).ToList();
            var viaFirstCursor = await ScrollAsync(tech, null, random, 17);
            var viaEarliest = await ScrollAsync(tech, tasks.Min(t => t.ModifiedAt), random, 5);

            viaFirstCursor.Should().Equal(expected, $"seed {seed}: scroll from the beginning");
            viaEarliest.Should().Equal(expected, $"seed {seed}: scroll from the earliest timestamp");
            viaFirstCursor.Should().OnlyHaveUniqueItems();
        }
    }

    [Fact] // US-004-3.3: 2 500 tasks sharing one modifiedAt, pages of 1000
    public async Task Twenty_five_hundred_tasks_with_one_timestamp_page_cleanly()
    {
        var host = SpiHost.Isolated(sql);
        await host.AddTasksAsync(Enumerable.Range(0, 2500).Select(i =>
            SpiHost.BuildTask(new TaskSpec { LocalId = $"same-{i:D5}", ModifiedAt = Base })));
        var tech = await host.TechAsync();

        var pages = new List<int>();
        var all = new List<string>();
        DateTime? after = Base;
        string? lastId = null;
        while (true)
        {
            var path = "/tasks?languages=en-US&$top=1000&modifiedAfter=" + SpiHost.Iso(after!.Value) + (lastId is null ? "" : "&lastId=" + Uri.EscapeDataString(lastId));
            var page = await tech.Get(path);
            if (page.Value.Count == 0) break;
            pages.Add(page.Value.Count);
            all.AddRange(page.Value.Select(t => t!["urn"]!.GetValue<string>()));
            lastId = page.Value[^1]!["urn"]!.GetValue<string>();
        }

        pages.Should().Equal(1000, 1000, 500);
        all.Should().HaveCount(2500).And.OnlyHaveUniqueItems();
        all.Should().BeInAscendingOrder(StringComparer.Ordinal);

        // and with $top=1 for the first few: still exactly once each
        var singles = await ScrollAsync(tech, Base, new Random(1), 1);
        singles.Should().Equal(all);
    }

    [Fact] // tasks changing while a scroll is in flight: nothing is skipped, changed tasks reappear later
    public async Task Tasks_modified_during_a_scroll_are_seen_again_but_never_skipped()
    {
        var host = SpiHost.Isolated(sql);
        await host.AddTasksAsync(Enumerable.Range(0, 30).Select(i =>
            SpiHost.BuildTask(new TaskSpec { LocalId = $"mv{i:D2}", ModifiedAt = Base.AddMilliseconds(i / 5) })));
        var tech = await host.TechAsync();

        var first = await tech.Get($"/tasks?languages=en-US&$top=10&modifiedAfter={SpiHost.Iso(Base)}");
        var seen = first.Value.Select(t => t!["urn"]!.GetValue<string>()).ToList();
        var cursor = (SpiHost.Iso(DateTime.Parse(first.Value[^1]!["modifiedAt"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal)), seen[^1]);

        // an already-delivered task changes now -> it must come around again via the delta cursor
        await host.DbAsync(db => db.Tasks.Where(t => t.LocalId == "mv00")
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ModifiedAt, Base.AddHours(1))));

        var rest = new List<string>();
        var (after, lastId) = cursor;
        while (true)
        {
            var page = await tech.Get($"/tasks?languages=en-US&$top=10&modifiedAfter={after}&lastId={Uri.EscapeDataString(lastId)}");
            if (page.Value.Count == 0) break;
            rest.AddRange(page.Value.Select(t => t!["urn"]!.GetValue<string>()));
            after = page.Value[^1]!["modifiedAt"]!.GetValue<string>();
            lastId = rest[^1];
        }

        seen.Concat(rest).Distinct().Should().HaveCount(30); // nothing skipped
        rest.Should().Contain(SpiHost.TaskUrn("mv00")); // re-delivered with its new modifiedAt
    }
}
