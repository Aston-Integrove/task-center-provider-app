using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Tcp.Api.Configuration;

namespace Tcp.Api.Auth.Grants;

/// <summary>
/// RFC 7522 SAML 2.0 bearer grant (fallback if the destination cannot use <c>OAuth2JWTBearer</c>, FR-PP-10).
/// Uses only <see cref="SignedXml"/>; guards against signature wrapping by requiring exactly one Assertion,
/// a single enveloped signature referencing that Assertion's ID, a unique ID, and SHA-2 signatures.
/// </summary>
public sealed class Saml2BearerGrantHandler(
    IOptions<SamlOptions> saml,
    IOptions<ProviderOptions> provider,
    IConfiguration configuration,
    IGlobalUserResolver resolver,
    TimeProvider time,
    ILogger<Saml2BearerGrantHandler> logger) : IGrantHandler
{
    private const string AssertionNs = "urn:oasis:names:tc:SAML:2.0:assertion";
    private const int MaxAssertionChars = 24 * 1024;
    private const int MaxLifetimeSeconds = 600;
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);

    private static readonly HashSet<string> AllowedSignatureMethods =
    [
        "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256",
        "http://www.w3.org/2001/04/xmldsig-more#rsa-sha384",
        "http://www.w3.org/2001/04/xmldsig-more#rsa-sha512",
    ];

    private X509Certificate2? _certificate;

    public string GrantType => GrantTypes.Saml2Bearer;

    public Task<GrantResult> HandleAsync(GrantContext context) => HandleCoreAsync(context);

    private async Task<GrantResult> HandleCoreAsync(GrantContext context)
    {
        var raw = context.Form["assertion"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw)) return GrantResult.Fail("invalid_request", "assertion is required");
        if (raw.Length > MaxAssertionChars) return GrantResult.Fail("invalid_request", "assertion too large");

        var certificate = LoadCertificate();
        if (certificate is null)
        {
            logger.LogError("SAML bearer grant is enabled but no trust certificate is configured");
            return GrantResult.InvalidGrant("SAML trust is not configured");
        }

        XmlDocument doc;
        try
        {
            doc = LoadXml(System.Text.Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(raw.Trim())));
        }
        catch (Exception ex) when (ex is XmlException or FormatException or ArgumentException)
        {
            return GrantResult.InvalidGrant("assertion is not valid base64url SAML XML");
        }

        var nsm = new XmlNamespaceManager(doc.NameTable);
        nsm.AddNamespace("saml", AssertionNs);
        nsm.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);

        var assertions = doc.SelectNodes("//saml:Assertion", nsm)!;
        if (assertions.Count != 1 || assertions[0] is not XmlElement assertion)
            return GrantResult.InvalidGrant("expected exactly one Assertion");

        var signatureError = VerifySignature(doc, assertion, nsm, certificate);
        if (signatureError is not null) return GrantResult.InvalidGrant(signatureError);

        var now = time.GetUtcNow();
        var conditions = assertion.SelectSingleNode("saml:Conditions", nsm) as XmlElement;
        var notOnOrAfter = ParseTime(conditions?.GetAttribute("NotOnOrAfter"));
        if (notOnOrAfter is null) return GrantResult.InvalidGrant("assertion has no NotOnOrAfter");
        if (now - ClockSkew >= notOnOrAfter) return GrantResult.InvalidGrant("assertion expired");

        var notBefore = ParseTime(conditions?.GetAttribute("NotBefore"));
        if (notBefore is not null && now + ClockSkew < notBefore) return GrantResult.InvalidGrant("assertion not yet valid");

        var expectedAudience = string.IsNullOrWhiteSpace(saml.Value.Audience) ? provider.Value.PublicBaseUrl : saml.Value.Audience;
        var audiences = conditions?.SelectNodes("saml:AudienceRestriction/saml:Audience", nsm)?.Cast<XmlNode>()
            .Select(n => n.InnerText.Trim()).ToList() ?? [];
        if (string.IsNullOrEmpty(expectedAudience) ||
            !audiences.Contains(expectedAudience.TrimEnd('/'), StringComparer.Ordinal) &&
            !audiences.Contains(expectedAudience, StringComparer.Ordinal))
            return GrantResult.InvalidGrant("audience mismatch");

        var candidate = ReadUserId(assertion, nsm);
        if (candidate is null) return GrantResult.InvalidGrant("assertion carries no user identifier");

        var user = await resolver.ResolveByIdAsync(candidate, context.Ct);
        if (user is null)
        {
            logger.LogWarning("SAML assertion subject did not resolve to an active user");
            return GrantResult.InvalidGrant("unknown user");
        }

        var secondsLeft = (int)Math.Floor((notOnOrAfter.Value - now).TotalSeconds);
        var lifetime = Math.Min(Math.Min(context.Client.TokenLifetimeSeconds, MaxLifetimeSeconds), secondsLeft);
        if (lifetime < 1) return GrantResult.InvalidGrant("assertion expires too soon");

        var extra = new Dictionary<string, object> { ["orig_iss"] = assertion.SelectSingleNode("saml:Issuer", nsm)?.InnerText.Trim() ?? "saml" };
        if (!string.IsNullOrEmpty(user.User.Email)) extra["email"] = user.User.Email;
        if (!string.IsNullOrEmpty(user.User.DisplayName)) extra["name"] = user.User.DisplayName;
        return GrantResult.Ok(user.User.GlobalUserId, lifetime, extra);
    }

    private static string? VerifySignature(XmlDocument doc, XmlElement assertion, XmlNamespaceManager nsm, X509Certificate2 certificate)
    {
        var signatures = assertion.SelectNodes("ds:Signature", nsm)!;
        if (signatures.Count != 1 || signatures[0] is not XmlElement signatureElement)
            return "assertion must carry exactly one enveloped signature";

        var id = assertion.GetAttribute("ID");
        if (string.IsNullOrEmpty(id)) return "assertion has no ID";

        // The ID must be unique in the whole document, otherwise a wrapped copy could be what the signature covers.
        var sameId = doc.SelectNodes($"//*[@ID='{id.Replace("'", string.Empty)}' or @Id='{id.Replace("'", string.Empty)}' or @id='{id.Replace("'", string.Empty)}']")!;
        if (sameId.Count != 1) return "assertion ID is not unique";

        var signedXml = new SignedXml(assertion);
        try
        {
            signedXml.LoadXml(signatureElement);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return "malformed signature";
        }

        if (signedXml.SignedInfo is null || signedXml.SignedInfo.References.Count != 1 ||
            signedXml.SignedInfo.References[0] is not Reference reference ||
            reference.Uri != "#" + id)
            return "signature does not reference the assertion";

        if (signedXml.SignatureMethod is null || !AllowedSignatureMethods.Contains(signedXml.SignatureMethod))
            return "unsupported signature algorithm";

        return signedXml.CheckSignature(certificate, verifySignatureOnly: true) ? null : "signature invalid";
    }

    private string? ReadUserId(XmlElement assertion, XmlNamespaceManager nsm)
    {
        var attributeName = saml.Value.UserIdAttribute;
        if (!string.IsNullOrWhiteSpace(attributeName))
        {
            foreach (XmlElement attr in assertion.SelectNodes("saml:AttributeStatement/saml:Attribute", nsm)!)
            {
                if (attr.GetAttribute("Name") == attributeName || attr.GetAttribute("FriendlyName") == attributeName)
                    return attr.SelectSingleNode("saml:AttributeValue", nsm)?.InnerText.Trim();
            }
            return null;
        }
        return assertion.SelectSingleNode("saml:Subject/saml:NameID", nsm)?.InnerText.Trim();
    }

    private X509Certificate2? LoadCertificate()
    {
        if (_certificate is not null) return _certificate;
        var value = saml.Value.TrustCertificate;
        if (string.IsNullOrWhiteSpace(value)) value = configuration["Secrets:btp-saml-trust-cert"];
        if (string.IsNullOrWhiteSpace(value)) return null;

        _certificate = value.Contains("-----BEGIN", StringComparison.Ordinal)
            ? X509Certificate2.CreateFromPem(value)
            : X509CertificateLoader.LoadCertificate(Convert.FromBase64String(value.Trim()));
        return _certificate;
    }

    private static XmlDocument LoadXml(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0 };
        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        doc.Load(reader);
        return doc;
    }

    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t)
            ? t : null;
}
