namespace Tcp.Infrastructure.Scim.Filter;

public abstract record FilterNode;

public sealed record AndNode(FilterNode Left, FilterNode Right) : FilterNode;

public sealed record OrNode(FilterNode Left, FilterNode Right) : FilterNode;

public sealed record NotNode(FilterNode Inner) : FilterNode;

public sealed record CompareNode(AttrPath Path, string Op, FilterValue Value) : FilterNode;

public sealed record PresentNode(AttrPath Path) : FilterNode;

/// <summary>
/// A SCIM attribute path: optional schema URN, attribute name, optional sub-attribute and optional value filter
/// (<c>emails[type eq "work"].value</c>).
/// </summary>
public sealed record AttrPath(string? Schema, string Name, string? Sub, FilterNode? ValueFilter)
{
    /// <summary>Lower-case canonical form used for whitelisting: <c>name</c>, <c>name.sub</c>, or <c>urn:...:name</c>.</summary>
    public string Canonical =>
        ((Schema is null ? "" : Schema.ToLowerInvariant() + ":") + Name.ToLowerInvariant() +
         (Sub is null ? "" : "." + Sub.ToLowerInvariant()));

    public override string ToString() =>
        (Schema is null ? "" : Schema + ":") + Name + (ValueFilter is null ? "" : "[...]") + (Sub is null ? "" : "." + Sub);
}

public enum FilterValueKind
{
    String,
    Number,
    Boolean,
    Null,
}

public sealed record FilterValue(FilterValueKind Kind, string? Text = null, double Number = 0, bool Boolean = false)
{
    public static FilterValue OfString(string s) => new(FilterValueKind.String, Text: s);
    public static FilterValue OfNumber(double n) => new(FilterValueKind.Number, Number: n);
    public static FilterValue OfBool(bool b) => new(FilterValueKind.Boolean, Boolean: b);
    public static FilterValue Null { get; } = new(FilterValueKind.Null);
}
