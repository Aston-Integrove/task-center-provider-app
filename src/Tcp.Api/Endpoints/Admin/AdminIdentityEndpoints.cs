using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Tcp.Infrastructure.Persistence;

namespace Tcp.Api.Endpoints.Admin;

/// <summary>Read-only views over the SCIM store and the operation log for the admin UI (US-005-4).</summary>
public static class AdminIdentityEndpoints
{
    public static RouteGroupBuilder MapAdminIdentity(this RouteGroupBuilder admin)
    {
        // GET /admin/api/users?search&top&skip : missing Global User IDs are flagged
        admin.MapGet("/users", async (HttpContext http, TcpDbContext db) =>
        {
            var (top, skip) = Paging(http, 100);
            IQueryable<Domain.Identity.ScimUser> query = db.ScimUsers.AsNoTracking().Where(u => !u.IsDeleted);
            if (http.Request.Query["search"].FirstOrDefault() is { Length: > 0 } search)
            {
                var pattern = SqlLike.Contains(search);
                query = query.Where(u => EF.Functions.Like(u.UserName, pattern, SqlLike.EscapeCharacter) ||
                                         EF.Functions.Like(u.DisplayName!, pattern, SqlLike.EscapeCharacter) ||
                                         EF.Functions.Like(u.PrimaryEmail!, pattern, SqlLike.EscapeCharacter));
            }

            var total = await query.CountAsync(http.RequestAborted);
            var page = await query.OrderBy(u => u.UserName).Skip(skip).Take(top)
                .Select(u => new
                {
                    u.Id, u.UserName, u.DisplayName, u.PrimaryEmail, u.GlobalUserId, u.Active, u.LastModified,
                    Groups = u.Memberships.Select(m => m.Group.DisplayName).OrderBy(n => n).ToList(),
                }).ToListAsync(http.RequestAborted);

            return Results.Ok(new JsonObject
            {
                ["total"] = total, ["top"] = top, ["skip"] = skip,
                ["items"] = new JsonArray(page.Select(u => (JsonNode)new JsonObject
                {
                    ["id"] = u.Id, ["userName"] = u.UserName, ["displayName"] = u.DisplayName, ["email"] = u.PrimaryEmail,
                    ["globalUserId"] = u.GlobalUserId, ["missingGlobalUserId"] = u.GlobalUserId is null, ["active"] = u.Active,
                    ["lastModified"] = Spi.SpiMapper.Format(u.LastModified),
                    ["groups"] = new JsonArray(u.Groups.Select(g => (JsonNode)g).ToArray()),
                }).ToArray()),
            });
        });

        admin.MapGet("/groups", async (HttpContext http, TcpDbContext db) =>
        {
            var (top, skip) = Paging(http, 100);
            var total = await db.ScimGroups.CountAsync(http.RequestAborted);
            var page = await db.ScimGroups.AsNoTracking().OrderBy(g => g.DisplayName).Skip(skip).Take(top)
                .Select(g => new
                {
                    g.Id, g.DisplayName, g.ExternalId, g.LastModified, Count = g.Members.Count(),
                    Members = g.Members.Where(m => !m.User.IsDeleted).OrderBy(m => m.User.UserName).Take(50)
                        .Select(m => new { m.User.UserName, m.User.GlobalUserId }).ToList(),
                }).ToListAsync(http.RequestAborted);

            return Results.Ok(new JsonObject
            {
                ["total"] = total, ["top"] = top, ["skip"] = skip,
                ["items"] = new JsonArray(page.Select(g => (JsonNode)new JsonObject
                {
                    ["id"] = g.Id, ["displayName"] = g.DisplayName, ["externalId"] = g.ExternalId, ["memberCount"] = g.Count,
                    ["lastModified"] = Spi.SpiMapper.Format(g.LastModified),
                    ["members"] = new JsonArray(g.Members.Select(m => (JsonNode)new JsonObject { ["userName"] = m.UserName, ["globalUserId"] = m.GlobalUserId }).ToArray()),
                }).ToArray()),
            });
        });

        // GET /admin/api/operations?taskUrn&user&top&skip : the OperationLog, newest first
        admin.MapGet("/operations", async (HttpContext http, TcpDbContext db) =>
        {
            var (top, skip) = Paging(http, 100);
            IQueryable<Domain.Tasks.OperationLogEntry> query = db.OperationLog.AsNoTracking();
            if (http.Request.Query["taskUrn"].FirstOrDefault() is { Length: > 0 } urn) query = query.Where(o => o.TaskUrn == urn);
            if (http.Request.Query["user"].FirstOrDefault() is { Length: > 0 } user) query = query.Where(o => o.UserId == user);

            var total = await query.CountAsync(http.RequestAborted);
            var page = await query.OrderByDescending(o => o.Id).Skip(skip).Take(top).ToListAsync(http.RequestAborted);
            return Results.Ok(new JsonObject
            {
                ["total"] = total, ["top"] = top, ["skip"] = skip,
                ["items"] = new JsonArray(page.Select(TaskDetail.OperationJson).ToArray()),
            });
        });
        return admin;
    }

    private static (int Top, int Skip) Paging(HttpContext http, int defaultTop)
    {
        var top = Read(http, "top", defaultTop, 1, 500);
        var skip = Read(http, "skip", 0, 0, int.MaxValue);
        return (top, skip);
    }

    private static int Read(HttpContext http, string name, int fallback, int min, int max)
    {
        var raw = http.Request.Query[name].FirstOrDefault();
        if (raw is null) return fallback;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw AdminException.Invalid(new AdminError(name, $"must be an integer between {min} and {max}"));
        return value;
    }
}
