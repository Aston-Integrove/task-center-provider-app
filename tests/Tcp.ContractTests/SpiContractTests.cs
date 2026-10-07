using System.Net;
using System.Text.Json.Nodes;
using Tcp.Domain.Tasks;
using Tcp.TestSupport;

namespace Tcp.ContractTests;

/// <summary>
/// Every E1-E8 response is validated against the authoritative SAP contract (constitution I, NFR-004-03):
/// <c>TaskProviderV2.json</c>, the file as delivered by SAP, not our subset.
/// </summary>
[Collection(SqlCollection.Name)]
public class SpiContractTests(SqlServerFixture sql)
{
    private static readonly OpenApiContract Sap =
        OpenApiContract.Load("specs/004-task-provider-spi/contracts/TaskProviderV2.json");

    private SpiHost Host => SpiHost.Get(sql, "contract");

    private static void Conforms(SpiResponse response, string path, string method, int status)
    {
        ((int)response.Status).Should().Be(status, response.RawText);
        Sap.Declares(path, method, status).Should().BeTrue($"{method.ToUpperInvariant()} {path} should declare {status}");
        OpenApiContract.AssertValid(Sap.ValidateResponse(path, method, status, response.RawText), $"{method.ToUpperInvariant()} {path} -> {status}");
    }

    /// <summary>Error bodies are validated against the SAP <c>Error</c> schema.</summary>
    private static void ConformsAsError(SpiResponse response, string path, string method, int status, string expectedCode)
    {
        ((int)response.Status).Should().Be(status, response.RawText);
        Sap.Declares(path, method, status).Should().BeTrue($"{method.ToUpperInvariant()} {path} should declare {status}");
        OpenApiContract.AssertValid(Sap.ValidateSchema("Error", response.RawText), $"{method.ToUpperInvariant()} {path} -> {status} error body");
        response.ErrorCode.Should().Be(expectedCode);
    }

    private static string TaskPath(TaskInstance t, string suffix = "") => $"/tasks/{Uri.EscapeDataString(t.Urn)}{suffix}";

    // ---- E1-E3 ----------------------------------------------------------------------------------

    [Fact]
    public async Task E1_capabilities_conform()
    {
        var tech = await Host.TechAsync();
        Conforms(await tech.Get("/capabilities"), "/capabilities", "get", 200);
    }

    [Fact]
    public async Task E2_E3_definitions_conform_in_every_language_combination()
    {
        var tech = await Host.TechAsync();

        foreach (var languages in new[] { "en-US", "de-DE", "en-US,de-DE", "de-DE,en-US", "fr-FR" })
        {
            Conforms(await tech.Get($"/taskDefinitions?languages={languages}"), "/taskDefinitions", "get", 200);
            foreach (var id in new[] { "PR_APPROVAL", "LEAVE_APPROVAL", "INVOICE_EXCEPTION" })
                Conforms(await tech.Get($"/taskDefinitions/{Uri.EscapeDataString(SpiHost.DefinitionUrn(id))}?languages={languages}"),
                    "/taskDefinitions/{taskDefinitionUrn}", "get", 200);
        }
        Conforms(await tech.Get("/taskDefinitions?languages=en-US&$top=1&$skip=1"), "/taskDefinitions", "get", 200);
        Conforms(await tech.Get("/taskDefinitions?languages=en-US&$skip=99"), "/taskDefinitions", "get", 200); // empty value[]
    }

    // ---- E4-E5 ----------------------------------------------------------------------------------

    private async Task<(List<TaskInstance> Tasks, string User)> SeedVariedTasksAsync()
    {
        var user = await Host.AddUserAsync();
        var other = await Host.AddUserAsync();
        var inactive = await Host.AddUserAsync(active: false);
        var due = DateTime.UtcNow.AddDays(3);
        var tasks = new List<TaskInstance>
        {
            await Host.AddTaskAsync(new TaskSpec { Users = [user, inactive], Groups = ["G1", "G2"], DueAt = due, Subject = "Open task", SubjectDe = "Offene Aufgabe",
                Attributes = new Dictionary<string, string> { ["amount"] = "1234.50", ["currency"] = "ZAR", ["neededBy"] = "2026-10-31", ["bogus"] = "x", ["requester"] = "L. Robbins" } }),
            await Host.AddTaskAsync(new TaskSpec { Definition = "LEAVE_APPROVAL", Users = [user], Priority = TaskPriorities.VeryHigh,
                Attributes = new Dictionary<string, string> { ["days"] = "5", ["fromDate"] = "2026-11-01", ["toDate"] = "not-a-date", ["leaveType"] = "Annual" } }),
            await Host.AddTaskAsync(new TaskSpec { Definition = "INVOICE_EXCEPTION", Users = [user, other], Processor = other, Status = TaskStatuses.Reserved, CreatedBy = user,
                Attributes = new Dictionary<string, string> { ["variance"] = "-12.5", ["postingDate"] = "2026-10-01", ["vendor"] = "ACME" } }),
            await Host.AddTaskAsync(new TaskSpec { Users = [user], Status = TaskStatuses.Completed, Processor = user, CompletedBy = user, ModifiedAt = DateTime.UtcNow }),
            await Host.AddTaskAsync(new TaskSpec { Users = [user], Status = TaskStatuses.Canceled }),
            await Host.AddTaskAsync(new TaskSpec { Users = [], Status = TaskStatuses.Inactive, Subject = "No recipients" }),
        };
        await Host.DbAsync(async db =>
        {
            db.TaskOperationErrors.Add(new TaskOperationError { TaskUrn = tasks[0].Urn, ExecutedAt = DateTime.UtcNow, Code = "approve", Message = "boom", ExecutedBy = user });
            await db.SaveChangesAsync();
        });
        return (tasks, user);
    }

    [Fact]
    public async Task E4_task_pages_conform_for_every_kind_of_task()
    {
        await SeedVariedTasksAsync();
        var tech = await Host.TechAsync();

        foreach (var languages in new[] { "en-US", "en-US,de-DE", "de-DE", "it-IT" })
        {
            var page = await tech.Get($"/tasks?languages={languages}&$top=1000");
            Conforms(page, "/tasks", "get", 200);
            page.Value.Should().NotBeEmpty();
        }
        Conforms(await tech.Get($"/tasks?languages=en-US&modifiedAfter={SpiHost.Iso(DateTime.UtcNow.AddYears(5))}"), "/tasks", "get", 200); // empty page
    }

    [Fact]
    public async Task E5_single_tasks_conform()
    {
        var (tasks, _) = await SeedVariedTasksAsync();
        var tech = await Host.TechAsync();

        foreach (var task in tasks)
            Conforms(await tech.Get(TaskPath(task) + "?languages=en-US,de-DE"), "/tasks/{taskUrn}", "get", 200);
    }

    // ---- E6-E8 ----------------------------------------------------------------------------------

    [Fact]
    public async Task E6_description_conforms_to_the_plain_text_response()
    {
        var user = await Host.AddUserAsync();
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [user], Descriptions = [new("de-DE", "text/html", "<p>Bitte <b>genehmigen</b></p>")] });
        var client = await Host.UserAsync(user);

        var response = await client.Get(TaskPath(task, "/description"), "de-DE");

        response.Status.Should().Be(HttpStatusCode.OK);
        Sap.Declares("/tasks/{taskUrn}/description", "get", 200).Should().BeTrue();
        var declared = Sap.ContentTypes("/tasks/{taskUrn}/description", "get", 200);
        declared.Should().Contain(response.Raw.Content.Headers.ContentType!.ToString());
        response.Raw.Content.Headers.ContentLanguage.Should().NotBeEmpty(); // header declared by the contract
        response.RawText.Should().Be("Bitte genehmigen");
    }

    [Fact]
    public async Task E7_response_and_E8_action_results_conform()
    {
        var user = await Host.AddUserAsync();
        var client = await Host.UserAsync(user);
        var forAction = await Host.AddTaskAsync(new TaskSpec { Users = [user] });
        var forResponse = await Host.AddTaskAsync(new TaskSpec { Users = [user] });

        Conforms(await client.Act(forAction.Urn, "claim"), "/tasks/{taskUrn}/action", "post", 200);
        Conforms(await client.Act(forAction.Urn, "increasePriority"), "/tasks/{taskUrn}/action", "post", 200);
        Conforms(await client.Act(forAction.Urn, "release"), "/tasks/{taskUrn}/action", "post", 200);
        Conforms(await client.Respond(forResponse.Urn, "reject", "no money", "budget"), "/tasks/{taskUrn}/response", "post", 200);
    }

    [Fact]
    public async Task E7_asynchronous_acceptance_is_declared_as_202_without_body()
    {
        var host = SpiHost.Get(sql, "contract-async", new() { ["Spi:AsyncResponses"] = "true", ["Spi:AsyncDelaySeconds"] = "30" });
        var user = await host.AddUserAsync();
        var task = await host.AddTaskAsync(new TaskSpec { Users = [user] });

        var response = await (await host.UserAsync(user)).Respond(task.Urn, "approve");

        response.Status.Should().Be(HttpStatusCode.Accepted);
        Sap.Declares("/tasks/{taskUrn}/response", "post", 202).Should().BeTrue();
        Sap.ContentTypes("/tasks/{taskUrn}/response", "post", 202).Should().BeEmpty();
        response.RawText.Should().BeEmpty();
    }

    // ---- error bodies ---------------------------------------------------------------------------

    [Fact]
    public async Task Error_responses_conform_to_the_sap_error_schema_and_are_declared()
    {
        var tech = await Host.TechAsync();
        var user = await Host.AddUserAsync();
        var client = await Host.UserAsync(user);
        var stranger = await Host.UserAsync(await Host.AddUserAsync());
        var task = await Host.AddTaskAsync(new TaskSpec { Users = [user] });
        var done = await Host.AddTaskAsync(new TaskSpec { Users = [user], Status = TaskStatuses.Completed });
        var unknown = SpiHost.TaskUrn("does-not-exist");

        ConformsAsError(await tech.Get("/taskDefinitions?languages=bad"), "/taskDefinitions", "get", 400, "tcp.spi.invalidParameter");
        ConformsAsError(await tech.Get("/tasks?languages=en-US&$top=0"), "/tasks", "get", 400, "tcp.spi.invalidParameter");
        ConformsAsError(await tech.Get($"/tasks/{Uri.EscapeDataString(unknown)}?languages=en-US"), "/tasks/{taskUrn}", "get", 404, "tcp.spi.taskNotFound");
        ConformsAsError(await tech.Get($"/taskDefinitions/{Uri.EscapeDataString(SpiHost.DefinitionUrn("NOPE"))}?languages=en-US"),
            "/taskDefinitions/{taskDefinitionUrn}", "get", 404, "tcp.spi.taskDefinitionNotFound");

        ConformsAsError(await tech.Respond(task.Urn, "approve"), "/tasks/{taskUrn}/response", "post", 403, "tcp.auth.userContextRequired");
        ConformsAsError(await stranger.Respond(task.Urn, "approve"), "/tasks/{taskUrn}/response", "post", 403, "tcp.spi.notAuthorized");
        ConformsAsError(await client.Respond(unknown, "approve"), "/tasks/{taskUrn}/response", "post", 404, "tcp.spi.taskNotFound");
        ConformsAsError(await client.Respond(task.Urn, "nonsense"), "/tasks/{taskUrn}/response", "post", 400, "tcp.spi.invalidOperation");
        ConformsAsError(await client.Respond(task.Urn, "reject"), "/tasks/{taskUrn}/response", "post", 400, "tcp.spi.commentRequired");
        ConformsAsError(await client.Respond(done.Urn, "approve"), "/tasks/{taskUrn}/response", "post", 409, "tcp.spi.taskFinal");
        ConformsAsError(await client.Act(done.Urn, "claim"), "/tasks/{taskUrn}/action", "post", 409, "tcp.spi.taskFinal");
        ConformsAsError(await client.Act(task.Urn, "release"), "/tasks/{taskUrn}/action", "post", 409, "tcp.spi.actionNotValid");

        ConformsAsError(await Host.Anonymous().Get("/capabilities"), "/capabilities", "get", 401, "tcp.auth.unauthorized");
        ConformsAsError(await tech.Post("/bulkOperation", new JsonObject()), "/bulkOperation", "post", 501, "tcp.spi.notImplemented");
    }
}
