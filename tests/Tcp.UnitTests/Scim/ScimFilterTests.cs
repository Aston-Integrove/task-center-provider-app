using System.Globalization;
using System.Text.Json.Nodes;
using Tcp.Infrastructure.Scim;
using Tcp.Infrastructure.Scim.Filter;

namespace Tcp.UnitTests.Scim;

public class ScimFilterParserTests
{
    /// <summary>S-expression dump of the AST so grammar/precedence can be asserted compactly.</summary>
    private static string Dump(FilterNode node) => node switch
    {
        AndNode a => $"(and {Dump(a.Left)} {Dump(a.Right)})",
        OrNode o => $"(or {Dump(o.Left)} {Dump(o.Right)})",
        NotNode n => $"(not {Dump(n.Inner)})",
        PresentNode p => $"(pr {Path(p.Path)})",
        CompareNode c => $"({c.Op} {Path(c.Path)} {Value(c.Value)})",
        _ => "?",
    };

    private static string Path(AttrPath p) =>
        (p.Schema is null ? "" : p.Schema + ":") + p.Name + (p.ValueFilter is null ? "" : "[" + Dump(p.ValueFilter) + "]") + (p.Sub is null ? "" : "." + p.Sub);

    private static string Value(FilterValue v) => v.Kind switch
    {
        FilterValueKind.String => "\"" + v.Text + "\"",
        FilterValueKind.Number => v.Number.ToString(CultureInfo.InvariantCulture),
        FilterValueKind.Boolean => v.Boolean ? "true" : "false",
        _ => "null",
    };

    [Theory] // T003-07: grammar table
    [InlineData("userName eq \"jdoe\"", "(eq userName \"jdoe\")")]
    [InlineData("userName EQ \"jdoe\"", "(eq userName \"jdoe\")")]
    [InlineData("  userName    eq   \"jdoe\"  ", "(eq userName \"jdoe\")")]
    [InlineData("userName ne \"x\"", "(ne userName \"x\")")]
    [InlineData("userName co \"oe\"", "(co userName \"oe\")")]
    [InlineData("userName sw \"jd\"", "(sw userName \"jd\")")]
    [InlineData("userName ew \"oe\"", "(ew userName \"oe\")")]
    [InlineData("externalId pr", "(pr externalId)")]
    [InlineData("externalId PR", "(pr externalId)")]
    [InlineData("active eq true", "(eq active true)")]
    [InlineData("active eq FALSE", "(eq active false)")]
    [InlineData("title eq null", "(eq title null)")]
    [InlineData("age gt 5", "(gt age 5)")]
    [InlineData("score le -1.5", "(le score -1.5)")]
    [InlineData("meta.lastModified gt \"2024-01-01T00:00:00Z\"", "(gt meta.lastModified \"2024-01-01T00:00:00Z\")")]
    [InlineData("name.givenName sw \"Jo\"", "(sw name.givenName \"Jo\")")]
    [InlineData("emails.value eq \"a@b.c\"", "(eq emails.value \"a@b.c\")")]
    [InlineData("emails[type eq \"work\"].value eq \"a@b.c\"", "(eq emails[(eq type \"work\")].value \"a@b.c\")")]
    [InlineData("emails[type eq \"work\" and primary eq true].value co \"x\"", "(co emails[(and (eq type \"work\") (eq primary true))].value \"x\")")]
    [InlineData("members[value eq \"abc\"] pr", "(pr members[(eq value \"abc\")])")]
    [InlineData("urn:ietf:params:scim:schemas:extension:sap:2.0:User:userUuid eq \"u\"",
        "(eq urn:ietf:params:scim:schemas:extension:sap:2.0:User:userUuid \"u\")")]
    [InlineData("urn:ietf:params:scim:schemas:core:2.0:User:name.familyName eq \"x\"",
        "(eq urn:ietf:params:scim:schemas:core:2.0:User:name.familyName \"x\")")]
    [InlineData("a eq \"1\" and b eq \"2\"", "(and (eq a \"1\") (eq b \"2\"))")]
    [InlineData("a eq \"1\" AND b eq \"2\"", "(and (eq a \"1\") (eq b \"2\"))")]
    [InlineData("a eq \"1\" or b eq \"2\"", "(or (eq a \"1\") (eq b \"2\"))")]
    [InlineData("a eq \"1\" or b eq \"2\" and c eq \"3\"", "(or (eq a \"1\") (and (eq b \"2\") (eq c \"3\")))")] // and binds tighter
    [InlineData("a eq \"1\" and b eq \"2\" or c eq \"3\"", "(or (and (eq a \"1\") (eq b \"2\")) (eq c \"3\"))")]
    [InlineData("(a eq \"1\" or b eq \"2\") and c eq \"3\"", "(and (or (eq a \"1\") (eq b \"2\")) (eq c \"3\"))")]
    [InlineData("a eq \"1\" and b eq \"2\" and c eq \"3\"", "(and (and (eq a \"1\") (eq b \"2\")) (eq c \"3\"))")] // left-assoc
    [InlineData("not (a eq \"1\")", "(not (eq a \"1\"))")]
    [InlineData("NOT(a eq \"1\" or b pr)", "(not (or (eq a \"1\") (pr b)))")]
    [InlineData("notes eq \"x\"", "(eq notes \"x\")")] // attribute that merely starts with "not"
    [InlineData("order eq \"x\"", "(eq order \"x\")")] // attribute that starts with "or"
    [InlineData("android eq \"x\"", "(eq android \"x\")")] // ... and with "and"
    [InlineData("displayName eq \"say \\\"hi\\\"\"", "(eq displayName \"say \"hi\"\")")]
    [InlineData("displayName eq \"a\\\\b\"", "(eq displayName \"a\\b\")")]
    [InlineData("displayName eq \"caf\\u00e9\"", "(eq displayName \"café\")")]
    [InlineData("displayName eq \"a and b\"", "(eq displayName \"a and b\")")] // keyword inside a string
    [InlineData("displayName eq \"\"", "(eq displayName \"\")")]
    [InlineData("((a eq \"1\"))", "(eq a \"1\")")]
    public void Parses_to_expected_tree(string filter, string expected) =>
        Dump(ScimFilterParser.Parse(filter)).Should().Be(expected);

    [Theory] // T003-07: everything that is not valid is invalidFilter (400), never a 500
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("userName")]
    [InlineData("userName eq")]
    [InlineData("userName \"x\"")]
    [InlineData("userName foo \"x\"")]
    [InlineData("userName eq x")]
    [InlineData("userName eq \"unterminated")]
    [InlineData("userName eq \"bad \\q escape\"")]
    [InlineData("userName eq \"bad \\u12 escape\"")]
    [InlineData("(userName eq \"x\"")]
    [InlineData("userName eq \"x\")")]
    [InlineData("userName eq \"x\" and")]
    [InlineData("and userName eq \"x\"")]
    [InlineData("userName eq \"x\" garbage")]
    [InlineData("a.b.c eq \"x\"")]
    [InlineData("urn:foo eq \"x\"")]
    [InlineData("emails[type eq \"work\" eq \"x\"")]
    [InlineData("emails[].value eq \"x\"")]
    [InlineData("not userName eq \"x\"")]
    [InlineData("userName pr \"x\"")]
    public void Rejects_invalid_filters(string filter)
    {
        var act = () => ScimFilterParser.Parse(filter);
        act.Should().Throw<ScimException>().Where(e => e.Status == 400 && e.ScimType == "invalidFilter");
    }

    [Fact]
    public void Rejects_overlong_and_deeply_nested_filters()
    {
        var deep = new string('(', 64) + "a eq \"1\"" + new string(')', 64);
        var tooDeep = () => ScimFilterParser.Parse(deep);
        tooDeep.Should().Throw<ScimException>().Where(e => e.ScimType == "invalidFilter");

        var tooLong = () => ScimFilterParser.Parse("a eq \"" + new string('x', 5000) + "\"");
        tooLong.Should().Throw<ScimException>().Where(e => e.ScimType == "invalidFilter");
    }

    [Fact]
    public void Attribute_paths_parse_for_patch()
    {
        var path = ScimFilterParser.ParseAttrPath("emails[type eq \"work\"].value");
        (path.Name, path.Sub).Should().Be(("emails", "value"));
        path.ValueFilter.Should().NotBeNull();

        var ext = ScimFilterParser.ParseAttrPath("urn:ietf:params:scim:schemas:extension:sap:2.0:User:userUuid");
        (ext.Schema, ext.Name).Should().Be(("urn:ietf:params:scim:schemas:extension:sap:2.0:User", "userUuid"));

        var bad = () => ScimFilterParser.ParseAttrPath("emails[");
        bad.Should().Throw<ScimException>().Where(e => e.ScimType == "invalidPath");
    }
}

public class ScimFilterEvaluatorTests
{
    private static bool Eval(string json, string filter) =>
        ScimFilterEvaluator.Matches(JsonNode.Parse(json), ScimFilterParser.Parse(filter));

    [Theory]
    [InlineData("""{"type":"work","value":"a@b.c","primary":true}""", "type eq \"work\"", true)]
    [InlineData("""{"type":"WORK"}""", "type eq \"work\"", true)] // case-insensitive
    [InlineData("""{"type":"home"}""", "type eq \"work\"", false)]
    [InlineData("""{"value":"a@b.c"}""", "value co \"@b\"", true)]
    [InlineData("""{"value":"a@b.c"}""", "value sw \"a@\" and value ew \".c\"", true)]
    [InlineData("""{"primary":true}""", "primary eq true", true)]
    [InlineData("""{"primary":false}""", "primary eq true", false)]
    [InlineData("""{"value":"x"}""", "type pr", false)]
    [InlineData("""{"type":"w"}""", "type pr", true)]
    [InlineData("""{"type":"w"}""", "not (type eq \"x\")", true)]
    [InlineData("""{"n":5}""", "n gt 3 and n lt 10", true)]
    [InlineData("""{"type":"w"}""", "missing ne \"x\"", true)]
    [InlineData("""{"type":"w"}""", "type eq null", false)]
    [InlineData("""{"other":1}""", "type eq null", true)]
    public void Matches_in_memory(string item, string filter, bool expected) =>
        Eval(item, filter).Should().Be(expected);
}
