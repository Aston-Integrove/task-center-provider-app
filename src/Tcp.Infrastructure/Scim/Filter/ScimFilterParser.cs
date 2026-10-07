using System.Globalization;
using System.Text;

namespace Tcp.Infrastructure.Scim.Filter;

/// <summary>
/// Recursive-descent parser for the RFC 7644 section 3.4.2.2 filter subset (plan 003):
/// <code>
/// filter  := or
/// or      := and ("or" and)*
/// and     := unary ("and" unary)*
/// unary   := "not" "(" filter ")" | primary
/// primary := "(" filter ")" | attrExp
/// attrExp := attrPath "pr" | attrPath compOp compValue
/// attrPath:= [schemaUrn ":"] name ("." sub)? | name "[" filter "]" ("." sub)?
/// </code>
/// Keywords and operators are case-insensitive. Any syntax error is <c>invalidFilter</c>.
/// </summary>
public sealed class ScimFilterParser
{
    private static readonly HashSet<string> Operators = new(StringComparer.OrdinalIgnoreCase)
    {
        "eq", "ne", "co", "sw", "ew", "gt", "ge", "lt", "le",
    };

    private const int MaxLength = 4096;
    private const int MaxDepth = 32;

    private readonly string _text;
    private int _pos;
    private int _depth;

    private ScimFilterParser(string text) => _text = text;

    public static FilterNode Parse(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) throw Invalid("Filter is empty");
        if (filter.Length > MaxLength) throw Invalid("Filter is too long");

        var parser = new ScimFilterParser(filter);
        var node = parser.ParseOr();
        parser.SkipWs();
        if (!parser.AtEnd) throw Invalid($"Unexpected '{parser._text[parser._pos]}' at position {parser._pos}");
        return node;
    }

    /// <summary>Parses a bare attribute path as used in PATCH (<c>emails[type eq "work"].value</c>, <c>urn:...:userUuid</c>).</summary>
    public static AttrPath ParseAttrPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw ScimException.BadRequest("invalidPath", "Path is empty");
        var parser = new ScimFilterParser(path.Trim());
        try
        {
            var result = parser.ParsePath();
            if (!parser.AtEnd) throw Invalid($"Unexpected '{parser._text[parser._pos]}' in path");
            return result;
        }
        catch (ScimException ex) when (ex.ScimType == "invalidFilter")
        {
            throw ScimException.BadRequest("invalidPath", ex.Message);
        }
    }

    private bool AtEnd => _pos >= _text.Length;

    private static ScimException Invalid(string detail) => ScimException.BadRequest("invalidFilter", detail);

    private FilterNode ParseOr()
    {
        var left = ParseAnd();
        while (TryKeyword("or"))
            left = new OrNode(left, ParseAnd());
        return left;
    }

    private FilterNode ParseAnd()
    {
        var left = ParseUnary();
        while (TryKeyword("and"))
            left = new AndNode(left, ParseUnary());
        return left;
    }

    private FilterNode ParseUnary()
    {
        SkipWs();
        if (PeekKeyword("not"))
        {
            var save = _pos;
            _pos += 3;
            SkipWs();
            if (!AtEnd && _text[_pos] == '(')
            {
                _pos++;
                var inner = Nested(ParseOr);
                Expect(')');
                return new NotNode(inner);
            }
            _pos = save; // an attribute that merely starts with "not"
        }
        return ParsePrimary();
    }

    private FilterNode ParsePrimary()
    {
        SkipWs();
        if (AtEnd) throw Invalid("Unexpected end of filter");

        if (_text[_pos] == '(')
        {
            _pos++;
            var inner = Nested(ParseOr);
            Expect(')');
            return inner;
        }

        var path = ParsePath();
        SkipWs();
        var op = ReadWord();
        if (op.Length == 0) throw Invalid($"Operator expected after '{path}'");

        if (op.Equals("pr", StringComparison.OrdinalIgnoreCase)) return new PresentNode(path);
        if (!Operators.Contains(op)) throw Invalid($"Unknown operator '{op}'");

        return new CompareNode(path, op.ToLowerInvariant(), ParseValue());
    }

    private T Nested<T>(Func<T> parse)
    {
        if (++_depth > MaxDepth) throw Invalid("Filter is nested too deeply");
        try { return parse(); }
        finally { _depth--; }
    }

    private AttrPath ParsePath()
    {
        var start = _pos;
        while (!AtEnd)
        {
            var c = _text[_pos];
            if (char.IsLetterOrDigit(c) || c is '$' or '_' or '-' or ':' or '.') _pos++;
            else break;
        }
        var raw = _text[start.._pos];
        if (raw.Length == 0) throw Invalid($"Attribute name expected at position {start}");

        FilterNode? valueFilter = null;
        string? tail = null;
        if (!AtEnd && _text[_pos] == '[')
        {
            _pos++;
            valueFilter = Nested(ParseOr);
            Expect(']');
            if (!AtEnd && _text[_pos] == '.')
            {
                var subStart = ++_pos;
                while (!AtEnd && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] is '_' or '-' or '$')) _pos++;
                tail = _text[subStart.._pos];
                if (tail.Length == 0) throw Invalid("Sub-attribute expected after ']'");
            }
        }

        return BuildPath(raw, valueFilter, tail);
    }

    private static AttrPath BuildPath(string raw, FilterNode? valueFilter, string? tail)
    {
        string? schema = null;
        var nameAndSub = raw;
        if (raw.StartsWith("urn:", StringComparison.OrdinalIgnoreCase))
        {
            var idx = raw.LastIndexOf(':');
            if (idx <= 3 || idx == raw.Length - 1) throw Invalid($"Invalid attribute path '{raw}'");
            schema = raw[..idx];
            nameAndSub = raw[(idx + 1)..];
        }
        else if (raw.Contains(':', StringComparison.Ordinal))
        {
            throw Invalid($"Invalid attribute path '{raw}'");
        }

        var parts = nameAndSub.Split('.');
        if (parts.Length > 2 || parts.Any(p => p.Length == 0)) throw Invalid($"Invalid attribute path '{raw}'");

        if (valueFilter is not null && parts.Length != 1)
            throw Invalid($"Invalid attribute path '{raw}'");

        return new AttrPath(schema, parts[0], tail ?? (parts.Length == 2 ? parts[1] : null), valueFilter);
    }

    private FilterValue ParseValue()
    {
        SkipWs();
        if (AtEnd) throw Invalid("Value expected");

        if (_text[_pos] == '"') return FilterValue.OfString(ReadString());

        var word = ReadWord(allowNumberChars: true);
        if (word.Equals("true", StringComparison.OrdinalIgnoreCase)) return FilterValue.OfBool(true);
        if (word.Equals("false", StringComparison.OrdinalIgnoreCase)) return FilterValue.OfBool(false);
        if (word.Equals("null", StringComparison.OrdinalIgnoreCase)) return FilterValue.Null;
        if (double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return FilterValue.OfNumber(n);
        throw Invalid($"Invalid value '{word}'");
    }

    private string ReadString()
    {
        _pos++; // opening quote
        var sb = new StringBuilder();
        while (true)
        {
            if (AtEnd) throw Invalid("Unterminated string");
            var c = _text[_pos++];
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }

            if (AtEnd) throw Invalid("Unterminated escape sequence");
            var e = _text[_pos++];
            switch (e)
            {
                case '"': case '\\': case '/': sb.Append(e); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u':
                    if (_pos + 4 > _text.Length ||
                        !int.TryParse(_text.AsSpan(_pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        throw Invalid("Invalid unicode escape");
                    sb.Append((char)code);
                    _pos += 4;
                    break;
                default: throw Invalid($"Invalid escape '\\{e}'");
            }
        }
    }

    private string ReadWord(bool allowNumberChars = false)
    {
        var start = _pos;
        while (!AtEnd && (char.IsLetter(_text[_pos]) || allowNumberChars && (char.IsDigit(_text[_pos]) || _text[_pos] is '-' or '+' or '.'))) _pos++;
        return _text[start.._pos];
    }

    private void SkipWs()
    {
        while (!AtEnd && char.IsWhiteSpace(_text[_pos])) _pos++;
    }

    private void Expect(char c)
    {
        SkipWs();
        if (AtEnd || _text[_pos] != c) throw Invalid($"'{c}' expected at position {_pos}");
        _pos++;
    }

    private bool PeekKeyword(string keyword)
    {
        if (_pos + keyword.Length > _text.Length) return false;
        if (!string.Equals(_text.Substring(_pos, keyword.Length), keyword, StringComparison.OrdinalIgnoreCase)) return false;
        var after = _pos + keyword.Length;
        return after >= _text.Length || !(char.IsLetterOrDigit(_text[after]) || _text[after] is '_' or '-' or '.' or ':');
    }

    private bool TryKeyword(string keyword)
    {
        SkipWs();
        if (!PeekKeyword(keyword)) return false;
        _pos += keyword.Length;
        return true;
    }
}
