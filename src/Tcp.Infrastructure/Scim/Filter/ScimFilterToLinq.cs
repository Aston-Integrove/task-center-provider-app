using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Tcp.Domain.Identity;

namespace Tcp.Infrastructure.Scim.Filter;

/// <summary>
/// Translates a parsed filter into an EF-translatable predicate. Only whitelisted attributes translate; anything
/// else is <c>invalidFilter</c> so a filter can never reach arbitrary columns.
/// </summary>
public static class ScimFilterToLinq
{
    public const string SapExtensionUrn = "urn:ietf:params:scim:schemas:extension:sap:2.0:User";

    private static readonly MethodInfo Contains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
    private static readonly MethodInfo StartsWith = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
    private static readonly MethodInfo EndsWith = typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string)])!;
    private static readonly MethodInfo Like = typeof(DbFunctionsExtensions)
        .GetMethod(nameof(DbFunctionsExtensions.Like), [typeof(DbFunctions), typeof(string), typeof(string), typeof(string)])!;

    public static Expression<Func<ScimUser, bool>> ForUsers(FilterNode filter)
    {
        var p = Expression.Parameter(typeof(ScimUser), "u");
        return Expression.Lambda<Func<ScimUser, bool>>(Translate(filter, p, UserLeaf), p);
    }

    public static Expression<Func<ScimGroup, bool>> ForGroups(FilterNode filter)
    {
        var p = Expression.Parameter(typeof(ScimGroup), "g");
        return Expression.Lambda<Func<ScimGroup, bool>>(Translate(filter, p, GroupLeaf), p);
    }

    private static Expression Translate(FilterNode node, ParameterExpression p, Func<ParameterExpression, FilterNode, Expression> leaf) => node switch
    {
        AndNode a => Expression.AndAlso(Translate(a.Left, p, leaf), Translate(a.Right, p, leaf)),
        OrNode o => Expression.OrElse(Translate(o.Left, p, leaf), Translate(o.Right, p, leaf)),
        NotNode n => Expression.Not(Translate(n.Inner, p, leaf)),
        _ => leaf(p, node),
    };

    // ---- users -------------------------------------------------------------------------------

    private static Expression UserLeaf(ParameterExpression u, FilterNode node)
    {
        var (path, op, value) = Decompose(node);
        var key = path.Canonical;

        // emails: all addresses are searchable through EmailsSearch ("|type:value|type:value|")
        if (key is "emails" or "emails.value" or "emails.type")
            return EmailLeaf(u, path, op, value);

        if (path.ValueFilter is not null) throw Unsupported(path);

        return key switch
        {
            "username" => StringLeaf(Prop(u, nameof(ScimUser.UserName)), op, value, path),
            "externalid" => StringLeaf(Prop(u, nameof(ScimUser.ExternalId)), op, value, path),
            "displayname" => StringLeaf(Prop(u, nameof(ScimUser.DisplayName)), op, value, path),
            "name.givenname" => StringLeaf(Prop(u, nameof(ScimUser.GivenName)), op, value, path),
            "name.familyname" => StringLeaf(Prop(u, nameof(ScimUser.FamilyName)), op, value, path),
            "id" => IdLeaf(Prop(u, nameof(ScimUser.Id)), op, value, path),
            "active" => BoolLeaf(Prop(u, nameof(ScimUser.Active)), op, value, path),
            "meta.created" => DateLeaf(Prop(u, nameof(ScimUser.Created)), op, value, path),
            "meta.lastmodified" => DateLeaf(Prop(u, nameof(ScimUser.LastModified)), op, value, path),
            var k when k == SapExtensionUrn.ToLowerInvariant() + ":useruuid" || k == "useruuid" =>
                GlobalIdLeaf(Prop(u, nameof(ScimUser.GlobalUserId)), op, value, path),
            _ => throw Unsupported(path),
        };
    }

    private static Expression EmailLeaf(ParameterExpression u, AttrPath path, string? op, FilterValue? value)
    {
        var column = Prop(u, nameof(ScimUser.EmailsSearch));
        string? type = null;

        if (path.ValueFilter is not null)
        {
            // Only emails[type eq "x"] is supported as value filter.
            if (path.ValueFilter is not CompareNode { Path: { Canonical: "type", ValueFilter: null }, Op: "eq", Value.Kind: FilterValueKind.String } typeFilter)
                throw ScimException.BadRequest("invalidFilter", "Only emails[type eq \"...\"] is supported as a value filter");
            type = Sanitize(typeFilter.Value.Text!);
        }
        if (path.Name.Equals("emails", StringComparison.OrdinalIgnoreCase) && path.Sub is not null &&
            !path.Sub.Equals("value", StringComparison.OrdinalIgnoreCase) && !path.Sub.Equals("type", StringComparison.OrdinalIgnoreCase))
            throw Unsupported(path);

        var onType = string.Equals(path.Sub, "type", StringComparison.OrdinalIgnoreCase);

        if (op is null) // pr
            return Expression.NotEqual(column, Expression.Constant("|"));

        var v = RequireString(value!, path);
        var sv = Sanitize(v);
        var esc = Escape(sv);
        string pattern;
        if (onType)
        {
            if (op is not ("eq" or "ne")) throw Unsupported(path);
            pattern = $"%|{esc}:%";
        }
        else
        {
            var t = type is null ? "%:" : $"%|{Escape(type)}:";
            pattern = op switch
            {
                "eq" or "ne" => $"{t}{esc}|%",
                "sw" => $"{t}{esc}%",
                "ew" => $"{t}%{esc}|%",
                "co" => $"{t}%{esc}%|%",
                _ => throw Unsupported(path),
            };
            if (type is null && op is "co") pattern = $"%:%{esc}%|%";
        }

        Expression like = LikeCall(column, pattern);
        return op == "ne" ? Expression.Not(like) : like;
    }

    private static string Sanitize(string s) => s.Replace("|", string.Empty, StringComparison.Ordinal).Replace(":", "_", StringComparison.Ordinal);

    /// <summary>Builds the <c>EmailsSearch</c> column from the structured e-mails (type may be null).</summary>
    public static string BuildEmailsSearch(IEnumerable<(string? Type, string Value)> emails)
    {
        var parts = emails.Select(e => $"{Sanitize(e.Type ?? string.Empty)}:{Sanitize(e.Value)}").ToList();
        return parts.Count == 0 ? "|" : "|" + string.Join('|', parts) + "|";
    }

    // ---- groups ------------------------------------------------------------------------------

    private static Expression GroupLeaf(ParameterExpression g, FilterNode node)
    {
        var (path, op, value) = Decompose(node);
        if (path.ValueFilter is not null) throw Unsupported(path);

        return path.Canonical switch
        {
            "displayname" => StringLeaf(Prop(g, nameof(ScimGroup.DisplayName)), op, value, path),
            "externalid" => StringLeaf(Prop(g, nameof(ScimGroup.ExternalId)), op, value, path),
            "id" => IdLeaf(Prop(g, nameof(ScimGroup.Id)), op, value, path),
            "meta.created" => DateLeaf(Prop(g, nameof(ScimGroup.Created)), op, value, path),
            "meta.lastmodified" => DateLeaf(Prop(g, nameof(ScimGroup.LastModified)), op, value, path),
            "members.value" or "members" => MembersLeaf(g, op, value, path),
            _ => throw Unsupported(path),
        };
    }

    private static Expression MembersLeaf(ParameterExpression g, string? op, FilterValue? value, AttrPath path)
    {
        var members = Expression.Property(g, nameof(ScimGroup.Members));
        var anyMethod = typeof(Enumerable).GetMethods().First(m => m.Name == "Any" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(ScimGroupMember));

        if (op is null)
            return Expression.Call(typeof(Enumerable), "Any", [typeof(ScimGroupMember)], members);

        if (op is not ("eq" or "ne")) throw Unsupported(path);
        var text = RequireString(value!, path);
        var m = Expression.Parameter(typeof(ScimGroupMember), "m");
        Expression body = Guid.TryParse(text, out var id)
            ? Expression.Equal(Expression.Property(m, nameof(ScimGroupMember.UserId)), Expression.Constant(id))
            : Expression.Constant(false);
        Expression any = Expression.Call(anyMethod, members, Expression.Lambda<Func<ScimGroupMember, bool>>(body, m));
        return op == "ne" ? Expression.Not(any) : any;
    }

    // ---- leaves ------------------------------------------------------------------------------

    private static (AttrPath Path, string? Op, FilterValue? Value) Decompose(FilterNode node) => node switch
    {
        CompareNode c => (c.Path, c.Op, c.Value),
        PresentNode p => (p.Path, null, null),
        _ => throw ScimException.BadRequest("invalidFilter", "Unsupported filter expression"),
    };

    private static Expression StringLeaf(Expression column, string? op, FilterValue? value, AttrPath path)
    {
        if (op is null)
            return Expression.AndAlso(
                Expression.NotEqual(column, Expression.Constant(null, typeof(string))),
                Expression.NotEqual(column, Expression.Constant(string.Empty)));

        var v = RequireString(value!, path);
        var constant = Expression.Constant(v);
        return op switch
        {
            "eq" => Expression.Equal(column, constant),
            "ne" => Expression.NotEqual(column, constant),
            "co" => Expression.Call(column, Contains, constant),
            "sw" => Expression.Call(column, StartsWith, constant),
            "ew" => Expression.Call(column, EndsWith, constant),
            _ => throw ScimException.BadRequest("invalidFilter", $"Operator '{op}' is not supported for attribute '{path}'"),
        };
    }

    private static Expression IdLeaf(Expression column, string? op, FilterValue? value, AttrPath path)
    {
        if (op is null) return Expression.Constant(true);
        if (op is not ("eq" or "ne")) throw ScimException.BadRequest("invalidFilter", $"Operator '{op}' is not supported for attribute '{path}'");
        var text = RequireString(value!, path);
        if (!Guid.TryParse(text, out var id)) return Expression.Constant(op == "ne");
        return op == "eq" ? Expression.Equal(column, Expression.Constant(id)) : Expression.NotEqual(column, Expression.Constant(id));
    }

    private static Expression GlobalIdLeaf(Expression column, string? op, FilterValue? value, AttrPath path)
    {
        if (op is null) return Expression.NotEqual(column, Expression.Constant(null, typeof(string)));
        if (op is not ("eq" or "ne")) throw ScimException.BadRequest("invalidFilter", $"Operator '{op}' is not supported for attribute '{path}'");
        var normalized = GlobalUserId.Normalize(RequireString(value!, path));
        if (normalized is null) return Expression.Constant(op == "ne");
        var constant = Expression.Constant(normalized);
        return op == "eq" ? Expression.Equal(column, constant) : Expression.NotEqual(column, constant);
    }

    private static Expression BoolLeaf(Expression column, string? op, FilterValue? value, AttrPath path)
    {
        if (op is null) return Expression.Constant(true);
        if (op is not ("eq" or "ne")) throw ScimException.BadRequest("invalidFilter", $"Operator '{op}' is not supported for attribute '{path}'");

        bool b;
        if (value!.Kind == FilterValueKind.Boolean) b = value.Boolean;
        else if (value.Kind == FilterValueKind.String && bool.TryParse(value.Text, out var parsed)) b = parsed;
        else throw ScimException.BadRequest("invalidFilter", $"Attribute '{path}' expects true or false");

        return op == "eq" ? Expression.Equal(column, Expression.Constant(b)) : Expression.NotEqual(column, Expression.Constant(b));
    }

    private static Expression DateLeaf(Expression column, string? op, FilterValue? value, AttrPath path)
    {
        if (op is null) return Expression.Constant(true);
        var text = RequireString(value!, path);
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            throw ScimException.BadRequest("invalidFilter", $"Attribute '{path}' expects an ISO 8601 date-time");
        var constant = Expression.Constant(date);
        return op switch
        {
            "eq" => Expression.Equal(column, constant),
            "ne" => Expression.NotEqual(column, constant),
            "gt" => Expression.GreaterThan(column, constant),
            "ge" => Expression.GreaterThanOrEqual(column, constant),
            "lt" => Expression.LessThan(column, constant),
            "le" => Expression.LessThanOrEqual(column, constant),
            _ => throw ScimException.BadRequest("invalidFilter", $"Operator '{op}' is not supported for attribute '{path}'"),
        };
    }

    private static string RequireString(FilterValue value, AttrPath path) =>
        value.Kind == FilterValueKind.String
            ? value.Text!
            : throw ScimException.BadRequest("invalidFilter", $"Attribute '{path}' expects a quoted string value");

    private static MemberExpression Prop(ParameterExpression p, string name) => Expression.Property(p, name);

    private static Expression LikeCall(Expression column, string pattern) =>
        Expression.Call(Like,
            Expression.Property(null, typeof(EF).GetProperty(nameof(EF.Functions))!),
            column, Expression.Constant(pattern), Expression.Constant("\\"));

    /// <summary>Escapes LIKE wildcards with backslash (used together with ESCAPE '\').</summary>
    internal static string Escape(string s) =>
        s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
         .Replace("_", "\\_", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal);

    private static ScimException Unsupported(AttrPath path) =>
        ScimException.BadRequest("invalidFilter", $"Attribute '{path}' is not supported in filters");
}
