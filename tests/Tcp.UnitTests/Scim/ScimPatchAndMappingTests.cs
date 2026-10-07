using System.Text.Json.Nodes;
using Tcp.Domain.Identity;
using Tcp.Infrastructure.Scim;
using Tcp.Infrastructure.Scim.Filter;
using Tcp.Infrastructure.Scim.Patch;

namespace Tcp.UnitTests.Scim;

public class ScimPatchApplierTests
{
    private const string Sap = ScimSchemas.SapUser;

    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static JsonObject Patch(params string[] operations) =>
        Obj($$"""{"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[{{string.Join(',', operations)}}]}""");

    private static JsonObject User() => Obj("""
        {"userName":"jdoe","active":true,"name":{"givenName":"John","familyName":"Doe"},
         "emails":[{"value":"j@work.com","type":"work","primary":true},{"value":"j@home.com","type":"home"}]}
        """);

    private static JsonObject Group() => Obj("""
        {"displayName":"Approvers","members":[{"value":"u1"},{"value":"u2"}]}
        """);

    [Fact] // T003-10, RFC 7644 3.5.2.1 add
    public void Add_simple_attribute_sets_it()
    {
        var result = ScimPatchApplier.Apply(User(), Patch("""{"op":"add","path":"displayName","value":"John D"}"""));
        result["displayName"]!.GetValue<string>().Should().Be("John D");
    }

    [Fact]
    public void Add_to_nested_attribute_creates_parent_object()
    {
        var result = ScimPatchApplier.Apply(Obj("""{"userName":"x"}"""), Patch("""{"op":"add","path":"name.givenName","value":"Ann"}"""));
        result["name"]!["givenName"]!.GetValue<string>().Should().Be("Ann");
    }

    [Fact] // RFC: add to multi-valued appends
    public void Add_to_multivalued_appends_and_deduplicates()
    {
        var result = ScimPatchApplier.Apply(User(), Patch(
            """{"op":"add","path":"emails","value":[{"value":"new@x.com","type":"other"}]}""",
            """{"op":"add","path":"emails","value":[{"value":"new@x.com","type":"other"}]}"""));

        result["emails"]!.AsArray().Select(e => e!["value"]!.GetValue<string>())
            .Should().Equal("j@work.com", "j@home.com", "new@x.com");
    }

    [Fact] // RFC 7644 3.5.2.2 replace
    public void Replace_overwrites_the_attribute()
    {
        var result = ScimPatchApplier.Apply(User(), Patch("""{"op":"replace","path":"name.familyName","value":"Smith"}"""));
        result["name"]!["familyName"]!.GetValue<string>().Should().Be("Smith");
        result["name"]!["givenName"]!.GetValue<string>().Should().Be("John");
    }

    [Fact] // Azure AD / Okta style: no path, object value
    public void Replace_without_path_merges_top_level_attributes()
    {
        var result = ScimPatchApplier.Apply(User(), Patch("""{"op":"replace","value":{"active":false,"displayName":"JD"}}"""));
        result["active"]!.GetValue<bool>().Should().BeFalse();
        result["displayName"]!.GetValue<string>().Should().Be("JD");
        result["userName"]!.GetValue<string>().Should().Be("jdoe");
    }

    [Fact]
    public void Dotted_keys_without_path_are_treated_as_paths()
    {
        var result = ScimPatchApplier.Apply(User(), Patch("""{"op":"replace","value":{"name.givenName":"Johnny"}}"""));
        result["name"]!["givenName"]!.GetValue<string>().Should().Be("Johnny");
    }

    [Fact] // RFC 7644 3.5.2.3 example: replace filtered sub-attribute
    public void Replace_filtered_sub_attribute()
    {
        var result = ScimPatchApplier.Apply(User(), Patch(
            """{"op":"replace","path":"emails[type eq \"work\"].value","value":"john@corp.com"}"""));

        var emails = result["emails"]!.AsArray();
        emails[0]!["value"]!.GetValue<string>().Should().Be("john@corp.com");
        emails[1]!["value"]!.GetValue<string>().Should().Be("j@home.com");
    }

    [Fact]
    public void Replace_filtered_with_no_match_is_no_target()
    {
        var act = () => ScimPatchApplier.Apply(User(), Patch(
            """{"op":"replace","path":"emails[type eq \"nope\"].value","value":"x"}"""));
        act.Should().Throw<ScimException>().Where(e => e.ScimType == "noTarget");
    }

    [Fact] // RFC 7644 3.5.2.2: remove
    public void Remove_attribute()
    {
        var result = ScimPatchApplier.Apply(User(), Patch("""{"op":"remove","path":"name.givenName"}"""));
        result["name"]!.AsObject().ContainsKey("givenName").Should().BeFalse();
        result["name"]!.AsObject().ContainsKey("familyName").Should().BeTrue();
    }

    [Fact] // RFC example: remove one member by filter
    public void Remove_member_by_value_filter()
    {
        var result = ScimPatchApplier.Apply(Group(), Patch("""{"op":"remove","path":"members[value eq \"u1\"]"}"""));
        result["members"]!.AsArray().Select(m => m!["value"]!.GetValue<string>()).Should().Equal("u2");
    }

    [Fact] // Azure style: remove with path "members" and value list
    public void Remove_members_with_value_list()
    {
        var result = ScimPatchApplier.Apply(Group(), Patch("""{"op":"remove","path":"members","value":[{"value":"u2"}]}"""));
        result["members"]!.AsArray().Select(m => m!["value"]!.GetValue<string>()).Should().Equal("u1");
    }

    [Fact]
    public void Remove_all_members()
    {
        var result = ScimPatchApplier.Apply(Group(), Patch("""{"op":"remove","path":"members"}"""));
        result.ContainsKey("members").Should().BeFalse();
    }

    [Fact] // RFC example: add members
    public void Add_members_appends()
    {
        var result = ScimPatchApplier.Apply(Group(), Patch("""{"op":"add","path":"members","value":[{"value":"u3"},{"value":"u1"}]}"""));
        result["members"]!.AsArray().Select(m => m!["value"]!.GetValue<string>()).Should().Equal("u1", "u2", "u3");
    }

    [Fact]
    public void Add_members_when_none_exist_creates_the_array()
    {
        var result = ScimPatchApplier.Apply(Obj("""{"displayName":"G"}"""), Patch("""{"op":"add","path":"members","value":{"value":"u9"}}"""));
        result["members"]!.AsArray().Should().ContainSingle();
    }

    [Fact]
    public void Urn_qualified_path_targets_the_extension_object()
    {
        var result = ScimPatchApplier.Apply(User(), Patch(
            $$"""{"op":"replace","path":"{{Sap}}:userUuid","value":"0b3c7d1e-4f5a-4b6c-8d9e-0a1b2c3d4e5f"}"""));
        result[Sap]!["userUuid"]!.GetValue<string>().Should().Be("0b3c7d1e-4f5a-4b6c-8d9e-0a1b2c3d4e5f");
    }

    [Fact]
    public void Urn_keys_without_path_merge_into_the_extension()
    {
        var result = ScimPatchApplier.Apply(User(), Patch("{\"op\":\"add\",\"value\":{\"" + Sap + "\":{\"userUuid\":\"abc\"}}}"));
        result[Sap]!["userUuid"]!.GetValue<string>().Should().Be("abc");
    }

    [Fact]
    public void Attribute_names_and_op_names_are_case_insensitive()
    {
        var result = ScimPatchApplier.Apply(User(), Patch("""{"op":"Replace","path":"ACTIVE","value":false}"""));
        result["active"]!.GetValue<bool>().Should().BeFalse();
        result.Count.Should().Be(User().Count);
    }

    [Fact]
    public void Operations_apply_in_order()
    {
        var result = ScimPatchApplier.Apply(User(), Patch(
            """{"op":"replace","path":"displayName","value":"one"}""",
            """{"op":"replace","path":"displayName","value":"two"}"""));
        result["displayName"]!.GetValue<string>().Should().Be("two");
    }

    [Fact]
    public void Input_resource_is_not_mutated()
    {
        var original = User();
        var before = original.ToJsonString();
        ScimPatchApplier.Apply(original, Patch("""{"op":"replace","path":"active","value":false}"""));
        original.ToJsonString().Should().Be(before);
    }

    [Theory]
    [InlineData("""{"op":"remove"}""", "noTarget")]
    [InlineData("""{"op":"add","value":"scalar"}""", "invalidValue")]
    [InlineData("""{"op":"explode","path":"x","value":1}""", "invalidSyntax")]
    [InlineData("""{"path":"x","value":1}""", "invalidSyntax")]
    [InlineData("""{"op":"replace","path":"emails[","value":1}""", "invalidPath")]
    [InlineData("""{"op":"remove","path":"phoneNumbers[type eq \"x\"]"}""", "noTarget")]
    public void Invalid_operations_are_rejected_with_scim_types(string operation, string scimType)
    {
        var act = () => ScimPatchApplier.Apply(User(), Patch(operation));
        act.Should().Throw<ScimException>().Where(e => e.Status == 400 && e.ScimType == scimType);
    }

    [Fact]
    public void Missing_or_empty_operations_array_is_invalid_syntax()
    {
        var empty = () => ScimPatchApplier.Apply(User(), Obj("""{"schemas":[],"Operations":[]}"""));
        empty.Should().Throw<ScimException>().Where(e => e.ScimType == "invalidSyntax");
        var missing = () => ScimPatchApplier.Apply(User(), Obj("{}"));
        missing.Should().Throw<ScimException>().Where(e => e.ScimType == "invalidSyntax");
    }
}

public class ScimMappingTests
{
    private const string Uuid = "0b3c7d1e-4f5a-4b6c-8d9e-0a1b2c3d4e5f";

    private static JsonObject Payload() => (JsonObject)JsonNode.Parse($$$"""
        {"schemas":["urn:ietf:params:scim:schemas:core:2.0:User","{{{ScimSchemas.SapUser}}}"],
         "userName":"  jdoe  ","externalId":"E-1","displayName":"John Doe","active":true,
         "name":{"givenName":"John","familyName":"Doe"},
         "emails":[{"value":"j@home.com","type":"home"},{"value":"j@work.com","type":"work","primary":true}],
         "{{{ScimSchemas.SapUser}}}":{"userUuid":"{{{Uuid}}}","userId":"P000123"},
         "unknownAttribute":{"kept":"in raw"}}
        """)!;

    private static ScimUser ToEntity(UserWrite w) => new()
    {
        Id = Guid.NewGuid(), UserName = w.UserName, ExternalId = w.ExternalId, GlobalUserId = Uuid, DisplayName = w.DisplayName,
        GivenName = w.GivenName, FamilyName = w.FamilyName, PrimaryEmail = w.PrimaryEmail, EmailsJson = w.EmailsJson,
        EmailsSearch = w.EmailsSearch, Active = w.Active, RawJson = w.RawJson,
        Created = new DateTime(2026, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc), LastModified = new DateTime(2026, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc),
    };

    [Fact] // T003-05
    public void Round_trip_preserves_known_attributes_and_ignores_unknown()
    {
        var write = ScimUserMapper.Parse(Payload());
        var json = ScimUserMapper.ToJson(ToEntity(write), "https://tc.test");

        write.UserName.Should().Be("jdoe");
        write.PrimaryEmail.Should().Be("j@work.com");
        write.UserUuid.Should().Be(Uuid);
        write.RawJson.Should().Contain("unknownAttribute"); // preserved for diagnostics
        json.ContainsKey("unknownAttribute").Should().BeFalse();

        json["userName"]!.GetValue<string>().Should().Be("jdoe");
        json["externalId"]!.GetValue<string>().Should().Be("E-1");
        json["name"]!["formatted"]!.GetValue<string>().Should().Be("John Doe");
        json["emails"]!.AsArray().Should().HaveCount(2);
        json["active"]!.GetValue<bool>().Should().BeTrue();
        json[ScimSchemas.SapUser]!["userUuid"]!.GetValue<string>().Should().Be(Uuid);
        json[ScimSchemas.SapUser]!["userId"]!.GetValue<string>().Should().Be("P000123");
        json["schemas"]!.AsArray().Select(s => s!.GetValue<string>()).Should().Contain([ScimSchemas.User, ScimSchemas.SapUser]);
        json["meta"]!["resourceType"]!.GetValue<string>().Should().Be("User");
        json["meta"]!["created"]!.GetValue<string>().Should().Be("2026-01-02T03:04:05.678Z");
        json["meta"]!["location"]!.GetValue<string>().Should().Be($"https://tc.test/scim/v2/Users/{json["id"]!.GetValue<string>()}");
    }

    [Fact]
    public void Parsing_the_output_again_yields_the_same_write()
    {
        var first = ScimUserMapper.Parse(Payload());
        var second = ScimUserMapper.Parse(ScimUserMapper.ToJson(ToEntity(first), ""));

        (second.UserName, second.ExternalId, second.DisplayName, second.GivenName, second.FamilyName, second.PrimaryEmail, second.Active, second.UserUuid)
            .Should().Be((first.UserName, first.ExternalId, first.DisplayName, first.GivenName, first.FamilyName, first.PrimaryEmail, first.Active, first.UserUuid));
        second.EmailsSearch.Should().Be(first.EmailsSearch);
    }

    [Fact]
    public void First_email_is_primary_when_none_flagged()
    {
        var write = ScimUserMapper.Parse((JsonObject)JsonNode.Parse("""{"userName":"u","emails":[{"value":"a@x.com"},{"value":"b@x.com"}]}""")!);
        write.PrimaryEmail.Should().Be("a@x.com");
    }

    [Theory] // Azure AD sends booleans as strings
    [InlineData("\"False\"", false)]
    [InlineData("\"true\"", true)]
    [InlineData("false", false)]
    public void Active_accepts_booleans_and_boolean_strings(string value, bool expected)
    {
        var write = ScimUserMapper.Parse((JsonObject)JsonNode.Parse("{\"userName\":\"u\",\"active\":" + value + "}")!);
        write.Active.Should().Be(expected);
    }

    [Fact]
    public void Active_defaults_to_true() =>
        ScimUserMapper.Parse((JsonObject)JsonNode.Parse("""{"userName":"u"}""")!).Active.Should().BeTrue();

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"userName":""}""")]
    [InlineData("""{"userName":42}""")]
    [InlineData("""{"userName":"u","name":"str"}""")]
    [InlineData("""{"userName":"u","emails":"a@b.c"}""")]
    [InlineData("""{"userName":"u","emails":["a@b.c"]}""")]
    [InlineData("""{"userName":"u","active":"maybe"}""")]
    [InlineData("""{"userName":"u","displayName":["x"]}""")]
    public void Invalid_payloads_are_400_invalid_value(string json)
    {
        var act = () => ScimUserMapper.Parse((JsonObject)JsonNode.Parse(json)!);
        act.Should().Throw<ScimException>().Where(e => e.Status == 400 && e.ScimType == "invalidValue");
    }

    [Fact]
    public void Over_long_values_are_rejected()
    {
        var act = () => ScimUserMapper.Parse((JsonObject)JsonNode.Parse($$"""{"userName":"{{new string('x', 300)}}"}""")!);
        act.Should().Throw<ScimException>().Where(e => e.ScimType == "invalidValue");
    }

    [Fact]
    public void Group_round_trip_includes_member_references()
    {
        var user = new ScimUser { Id = Guid.NewGuid(), UserName = "jdoe", DisplayName = "John Doe" };
        var group = new ScimGroup { Id = Guid.NewGuid(), DisplayName = "Approvers", Created = DateTime.UtcNow, LastModified = DateTime.UtcNow };
        group.Members.Add(new ScimGroupMember { GroupId = group.Id, UserId = user.Id, User = user });

        var json = ScimGroupMapper.ToJson(group, "https://tc.test");

        json["members"]![0]!["value"]!.GetValue<string>().Should().Be(user.Id.ToString());
        json["members"]![0]!["display"]!.GetValue<string>().Should().Be("John Doe");
        json["members"]![0]!["$ref"]!.GetValue<string>().Should().EndWith($"/Users/{user.Id}");
        ScimGroupMapper.ToJson(group, "x", includeMembers: false).ContainsKey("members").Should().BeFalse();
        ScimGroupMapper.Parse(json).MemberValues.Should().Equal(user.Id.ToString());
    }
}

public class ScimFilterToLinqGuardTests
{
    [Theory] // supported combinations translate
    [InlineData("userName eq \"jdoe\"")]
    [InlineData("externalId sw \"E\"")]
    [InlineData("emails.value eq \"a@b.c\" and active eq true")]
    [InlineData("emails[type eq \"work\"].value co \"corp\"")]
    [InlineData("id eq \"0b3c7d1e-4f5a-4b6c-8d9e-0a1b2c3d4e5f\"")]
    [InlineData("urn:ietf:params:scim:schemas:extension:sap:2.0:User:userUuid eq \"0b3c7d1e-4f5a-4b6c-8d9e-0a1b2c3d4e5f\"")]
    [InlineData("meta.lastModified gt \"2026-01-01T00:00:00Z\"")]
    [InlineData("not (displayName pr) or name.givenName ew \"x\"")]
    public void Supported_user_filters_translate(string filter)
    {
        var act = () => ScimFilterToLinq.ForUsers(ScimFilterParser.Parse(filter));
        act.Should().NotThrow();
    }

    [Theory] // whitelist: unknown attributes and operator/type mismatches are invalidFilter
    [InlineData("password eq \"x\"")]
    [InlineData("RawJson co \"x\"")]
    [InlineData("userName gt \"a\"")]
    [InlineData("userName eq 5")]
    [InlineData("active gt true")]
    [InlineData("active eq \"maybe\"")]
    [InlineData("id co \"abc\"")]
    [InlineData("emails[type co \"w\"].value eq \"x\"")]
    [InlineData("emails.display eq \"x\"")]
    [InlineData("userName[type eq \"x\"] eq \"y\"")]
    [InlineData("meta.lastModified gt \"not a date\"")]
    public void Unsupported_user_filters_are_invalid_filter(string filter)
    {
        var act = () => ScimFilterToLinq.ForUsers(ScimFilterParser.Parse(filter));
        act.Should().Throw<ScimException>().Where(e => e.Status == 400 && e.ScimType == "invalidFilter");
    }

    [Fact]
    public void Group_filters_translate_and_unknown_attributes_do_not()
    {
        var ok = () => ScimFilterToLinq.ForGroups(ScimFilterParser.Parse("displayName eq \"X\" or members.value eq \"0b3c7d1e-4f5a-4b6c-8d9e-0a1b2c3d4e5f\""));
        ok.Should().NotThrow();
        var bad = () => ScimFilterToLinq.ForGroups(ScimFilterParser.Parse("userName eq \"x\""));
        bad.Should().Throw<ScimException>().Where(e => e.ScimType == "invalidFilter");
    }

    [Fact]
    public void Emails_search_column_is_built_from_all_addresses()
    {
        ScimFilterToLinq.BuildEmailsSearch([("work", "a@x.com"), (null, "b@y.com")]).Should().Be("|work:a@x.com|:b@y.com|");
        ScimFilterToLinq.BuildEmailsSearch([]).Should().Be("|");
        ScimFilterToLinq.BuildEmailsSearch([("we|ird:", "a|b@x.com")]).Should().Be("|weird_:ab@x.com|");
    }

    [Fact]
    public void Like_wildcards_in_user_input_are_escaped() =>
        ScimFilterToLinq.Escape("50%_off[1]\\").Should().Be("50\\%\\_off\\[1]\\\\");
}
