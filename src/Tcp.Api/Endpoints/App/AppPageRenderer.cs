using System.Globalization;
using System.Net;
using System.Text;
using Tcp.Api.Endpoints.Admin;
using Tcp.Api.Endpoints.Spi;
using Tcp.Domain.Tasks;
using Tcp.Infrastructure.Html;

namespace Tcp.Api.Endpoints.App;

/// <summary>What the page needs besides the task itself.</summary>
public sealed record AppPageModel(
    TaskDetail Detail,
    TaskDefinition? Definition,
    string Language,
    string DefaultLanguage,
    string? CurrentUserLabel,
    string? AntiforgeryField,
    string? AntiforgeryToken,
    string? Notice,
    string? Error,
    bool CanAct);

/// <summary>
/// Server-rendered "Open in App" page (US-005-6, T005-08). Plain string building with one rule: every dynamic value
/// goes through <see cref="E"/> (HTML encoding), except the description, which is sanitised HTML by construction and
/// is sanitised again here (defence in depth: it may have been written by an older version or directly in the database).
/// </summary>
public static class AppPageRenderer
{
    private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static readonly Dictionary<string, Dictionary<string, string>> Labels = new()
    {
        ["en"] = new()
        {
            ["status"] = "Status", ["priority"] = "Priority", ["due"] = "Due", ["created"] = "Created", ["modified"] = "Last changed",
            ["processor"] = "Processor", ["completed"] = "Completed", ["definition"] = "Task type", ["attributes"] = "Details",
            ["description"] = "Description", ["recipients"] = "Recipients", ["groups"] = "Groups", ["history"] = "History",
            ["none"] = "None", ["when"] = "When", ["who"] = "Who", ["what"] = "Operation", ["outcome"] = "Outcome", ["comment"] = "Comment",
            ["reason"] = "Reason", ["act"] = "Actions", ["respond"] = "Respond", ["errors"] = "Failed operations", ["inactive"] = "inactive",
            ["signedin"] = "Signed in as", ["readonly"] = "Read-only view (administrator)", ["done"] = "Done.", ["system"] = "system",
            ["choose"] = "- choose -",
        },
        ["de"] = new()
        {
            ["status"] = "Status", ["priority"] = "Priorität", ["due"] = "Fällig", ["created"] = "Erstellt", ["modified"] = "Zuletzt geändert",
            ["processor"] = "Bearbeiter", ["completed"] = "Abgeschlossen", ["definition"] = "Aufgabentyp", ["attributes"] = "Details",
            ["description"] = "Beschreibung", ["recipients"] = "Empfänger", ["groups"] = "Gruppen", ["history"] = "Verlauf",
            ["none"] = "Keine", ["when"] = "Wann", ["who"] = "Wer", ["what"] = "Aktion", ["outcome"] = "Ergebnis", ["comment"] = "Kommentar",
            ["reason"] = "Grund", ["act"] = "Aktionen", ["respond"] = "Antworten", ["errors"] = "Fehlgeschlagene Aktionen", ["inactive"] = "inaktiv",
            ["signedin"] = "Angemeldet als", ["readonly"] = "Nur-Lese-Ansicht (Administrator)", ["done"] = "Erledigt.", ["system"] = "System",
            ["choose"] = "- auswählen -",
        },
    };

    public static string Culture(string? language) =>
        language is not null && language.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";

    public static string Render(AppPageModel m, HtmlDescriptionSanitizer sanitizer)
    {
        var l = Labels[Culture(m.Language)];
        var task = m.Detail.Task;
        var languages = new[] { m.Language };
        string Text(IReadOnlyList<LocalizedText> texts) =>
            LocalizedTextSelector.Select(texts, languages, m.DefaultLanguage).FirstOrDefault()?.Text ?? string.Empty;

        var subject = Text(TaskDefinition.ParseTexts(System.Text.Json.Nodes.JsonNode.Parse(task.SubjectJson)));
        var users = m.Detail.Users.Where(u => u.GlobalUserId is not null).ToDictionary(u => u.GlobalUserId!, StringComparer.Ordinal);
        string Person(string? id)
        {
            if (id is null) return E(l["system"]);
            return users.TryGetValue(id, out var u) ? E(u.DisplayName ?? u.UserName) : "<code>" + E(id) + "</code>";
        }

        var sb = new StringBuilder(4096);
        sb.Append("<!doctype html><html lang=\"").Append(E(Culture(m.Language))).Append("\"><head><meta charset=\"utf-8\">")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
          .Append("<title>").Append(E(subject)).Append("</title><link rel=\"stylesheet\" href=\"/app/app.css\"></head><body><main>");

        sb.Append("<header><h1>").Append(E(subject)).Append("</h1><p class=\"meta\"><span class=\"badge status-")
          .Append(E(task.Status.ToLowerInvariant())).Append("\">").Append(E(task.Status)).Append("</span> <span class=\"badge\">")
          .Append(E(task.Priority)).Append("</span> <code>").Append(E(task.Urn)).Append("</code></p></header>");

        if (m.Notice is not null) sb.Append("<p class=\"notice\" role=\"status\">").Append(E(m.Notice)).Append("</p>");
        if (m.Error is not null) sb.Append("<p class=\"error\" role=\"alert\">").Append(E(m.Error)).Append("</p>");

        // details
        sb.Append("<section><dl>");
        Row(sb, l["definition"], E(m.Definition is null ? task.DefinitionUrn : Text(m.Definition.Name)));
        Row(sb, l["status"], E(task.Status));
        Row(sb, l["priority"], E(task.Priority));
        Row(sb, l["processor"], task.Processor is null ? E(l["none"]) : Person(task.Processor));
        Row(sb, l["created"], E(SpiMapper.Format(task.CreatedAt)) + " &middot; " + Person(task.CreatedBy));
        Row(sb, l["modified"], E(SpiMapper.Format(task.ModifiedAt)) + (task.ModifiedBy is null ? "" : " &middot; " + Person(task.ModifiedBy)));
        Row(sb, l["due"], task.DueAt is { } due ? E(SpiMapper.Format(due)) : E(l["none"]));
        if (task.CompletedAt is { } done) Row(sb, l["completed"], E(SpiMapper.Format(done)) + " &middot; " + Person(task.CompletedBy));
        sb.Append("</dl></section>");

        // custom attributes (labels from the definition, values validated by type)
        var attributes = new List<(string Label, string Value)>();
        if (m.Definition is not null)
            foreach (var def in m.Definition.CustomAttributes.OrderByDescending(a => a.Rank).ThenBy(a => a.Code, StringComparer.Ordinal))
            {
                var stored = task.CustomAttributes.FirstOrDefault(a => a.Code == def.Code);
                if (stored is not null && CustomAttributeValidator.TryNormalize(def.Type, stored.Value, out var value))
                    attributes.Add((Text(def.Name), value));
            }
        sb.Append("<section><h2>").Append(E(l["attributes"])).Append("</h2>");
        if (attributes.Count == 0) sb.Append("<p class=\"muted\">").Append(E(l["none"])).Append("</p>");
        else
        {
            sb.Append("<table><tbody>");
            foreach (var (label, value) in attributes) sb.Append("<tr><th scope=\"row\">").Append(E(label)).Append("</th><td>").Append(E(value)).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }
        sb.Append("</section>");

        // description: sanitised again on the way out
        var description = TaskDescriptionSelector.Select(m.Detail.Descriptions, languages, m.DefaultLanguage);
        sb.Append("<section><h2>").Append(E(l["description"])).Append("</h2>");
        if (description is null) sb.Append("<p class=\"muted\">").Append(E(l["none"])).Append("</p>");
        else if (description.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            sb.Append("<div class=\"description\">").Append(sanitizer.Sanitize(description.Body)).Append("</div>");
        else
            sb.Append("<div class=\"description\"><pre>").Append(E(description.Body)).Append("</pre></div>");
        sb.Append("</section>");

        // recipients
        sb.Append("<section><h2>").Append(E(l["recipients"])).Append("</h2><ul>");
        foreach (var r in task.RecipientUsers.OrderBy(r => r.GlobalUserId, StringComparer.Ordinal))
        {
            users.TryGetValue(r.GlobalUserId, out var u);
            sb.Append("<li>").Append(Person(r.GlobalUserId));
            if (u?.PrimaryEmail is { } email) sb.Append(" <span class=\"muted\">(").Append(E(email)).Append(")</span>");
            if (u is { Active: false }) sb.Append(" <span class=\"badge\">").Append(E(l["inactive"])).Append("</span>");
            sb.Append("</li>");
        }
        foreach (var g in task.RecipientGroups.OrderBy(g => g.GroupName, StringComparer.Ordinal))
            sb.Append("<li>").Append(E(l["groups"])).Append(": <strong>").Append(E(g.GroupName)).Append("</strong></li>");
        if (task.RecipientUsers.Count == 0 && task.RecipientGroups.Count == 0) sb.Append("<li class=\"muted\">").Append(E(l["none"])).Append("</li>");
        sb.Append("</ul></section>");

        // history
        sb.Append("<section><h2>").Append(E(l["history"])).Append("</h2>");
        if (m.Detail.Operations.Count == 0 && task.OperationErrors.Count == 0) sb.Append("<p class=\"muted\">").Append(E(l["none"])).Append("</p>");
        else
        {
            sb.Append("<table><thead><tr><th>").Append(E(l["when"])).Append("</th><th>").Append(E(l["who"])).Append("</th><th>")
              .Append(E(l["what"])).Append("</th><th>").Append(E(l["outcome"])).Append("</th><th>").Append(E(l["comment"])).Append("</th></tr></thead><tbody>");
            foreach (var o in m.Detail.Operations)
                sb.Append("<tr><td>").Append(E(SpiMapper.Format(o.At))).Append("</td><td>").Append(Person(o.UserId)).Append("</td><td>")
                  .Append(E(o.Kind.ToLowerInvariant())).Append(": ").Append(E(o.Code)).Append("</td><td>").Append(E(o.Outcome))
                  .Append(o.ErrorCode is null ? "" : " (" + E(o.ErrorCode) + ")").Append("</td><td>").Append(E(o.Comment)).Append("</td></tr>");
            foreach (var e in task.OperationErrors)
                sb.Append("<tr class=\"failed\"><td>").Append(E(SpiMapper.Format(e.ExecutedAt))).Append("</td><td>").Append(Person(e.ExecutedBy)).Append("</td><td>")
                  .Append(E(e.Code)).Append("</td><td>").Append(E(l["errors"])).Append("</td><td>").Append(E(e.Message)).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }
        sb.Append("</section>");

        // actions (IAS mode, entitled user)
        if (m.CanAct && m.Definition is not null && !task.IsFinal)
            AppendActions(sb, m, l, task, Text);
        else if (m.CurrentUserLabel is null)
            sb.Append("<p class=\"muted\">").Append(E(l["readonly"])).Append("</p>");

        if (m.CurrentUserLabel is not null)
            sb.Append("<footer>").Append(E(l["signedin"])).Append(" <strong>").Append(E(m.CurrentUserLabel)).Append("</strong></footer>");

        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string label, string htmlValue) =>
        sb.Append("<div><dt>").Append(E(label)).Append("</dt><dd>").Append(htmlValue).Append("</dd></div>");

    private static void AppendActions(StringBuilder sb, AppPageModel m, Dictionary<string, string> l, TaskInstance task, Func<IReadOnlyList<LocalizedText>, string> text)
    {
        var path = $"/app/tasks/{Uri.EscapeDataString(task.Urn)}";
        var validActions = OperationRules.ValidActionCodes(task, m.Definition!);
        sb.Append("<section class=\"actions\"><h2>").Append(E(l["act"])).Append("</h2>");

        void Form(string kind, OperationDefinition op)
        {
            sb.Append("<form method=\"post\" action=\"").Append(E(path)).Append('/').Append(kind).Append("\"><input type=\"hidden\" name=\"")
              .Append(E(m.AntiforgeryField)).Append("\" value=\"").Append(E(m.AntiforgeryToken)).Append("\"><input type=\"hidden\" name=\"code\" value=\"")
              .Append(E(op.Code)).Append("\">");
            if (op.CommentRequired != "UNSUPPORTED")
                sb.Append("<label>").Append(E(l["comment"])).Append(op.CommentRequired == "REQUIRED" ? " *" : "")
                  .Append(" <input type=\"text\" name=\"comment\" maxlength=\"2000\"").Append(op.CommentRequired == "REQUIRED" ? " required" : "").Append("></label>");
            if (op.ReasonRequired != "UNSUPPORTED" && op.PossibleReasons.Count > 0)
            {
                sb.Append("<label>").Append(E(l["reason"])).Append(op.ReasonRequired == "REQUIRED" ? " *" : "").Append(" <select name=\"reasonCode\"")
                  .Append(op.ReasonRequired == "REQUIRED" ? " required" : "").Append("><option value=\"\">").Append(E(l["choose"])).Append("</option>");
                foreach (var reason in op.PossibleReasons)
                    sb.Append("<option value=\"").Append(E(reason.Code)).Append("\">").Append(E(text(reason.Name))).Append("</option>");
                sb.Append("</select></label>");
            }
            sb.Append("<button type=\"submit\" class=\"nature-").Append(E(op.Nature.ToLowerInvariant())).Append("\">").Append(E(text(op.Name))).Append("</button></form>");
        }

        foreach (var response in m.Definition!.Responses) Form("respond", response);
        foreach (var action in m.Definition.Actions.Where(a => validActions.Contains(a.Code))) Form("action", action);
        sb.Append("</section>");
    }

    public const string Stylesheet = """
        :root { color-scheme: light dark; --fg:#1d2733; --bg:#fff; --muted:#667085; --line:#d9dee5; --accent:#0a6ed1; }
        @media (prefers-color-scheme: dark) { :root { --fg:#e6e9ee; --bg:#14181d; --muted:#98a2b3; --line:#2b323b; } }
        body { margin:0; font:15px/1.5 system-ui, "Segoe UI", sans-serif; color:var(--fg); background:var(--bg); }
        main { max-width:56rem; margin:0 auto; padding:1.5rem 1rem 3rem; }
        h1 { font-size:1.4rem; margin:0 0 .25rem; } h2 { font-size:1.05rem; margin:1.6rem 0 .5rem; border-bottom:1px solid var(--line); padding-bottom:.25rem; }
        .meta code { color:var(--muted); font-size:.8rem; word-break:break-all; }
        .badge { display:inline-block; padding:.05rem .5rem; border:1px solid var(--line); border-radius:.75rem; font-size:.8rem; }
        .status-ready { border-color:#2e9e5b; } .status-reserved { border-color:var(--accent); } .status-completed { border-color:#667085; } .status-canceled { border-color:#c4432b; }
        dl { display:grid; grid-template-columns:repeat(auto-fit, minmax(14rem, 1fr)); gap:.5rem 1.5rem; margin:0; }
        dt { color:var(--muted); font-size:.8rem; } dd { margin:0; }
        table { border-collapse:collapse; width:100%; } th, td { text-align:left; padding:.35rem .6rem; border-bottom:1px solid var(--line); vertical-align:top; }
        thead th { color:var(--muted); font-weight:600; } tbody th { width:35%; font-weight:600; } .failed td { color:#c4432b; }
        .muted { color:var(--muted); } .notice { border-left:4px solid #2e9e5b; padding:.4rem .8rem; } .error { border-left:4px solid #c4432b; padding:.4rem .8rem; }
        .description { overflow-x:auto; } .description table td, .description table th { border:1px solid var(--line); }
        .actions form { display:flex; flex-wrap:wrap; gap:.5rem; align-items:end; margin:.5rem 0; padding:.6rem; border:1px solid var(--line); border-radius:.4rem; }
        .actions label { display:flex; flex-direction:column; font-size:.8rem; color:var(--muted); } input, select, button { font:inherit; padding:.35rem .6rem; }
        button { cursor:pointer; border:1px solid var(--accent); background:var(--accent); color:#fff; border-radius:.3rem; }
        button.nature-negative { background:#c4432b; border-color:#c4432b; } button.nature-neutral { background:transparent; color:var(--accent); }
        footer { margin-top:2rem; color:var(--muted); font-size:.85rem; }
        """;
}
