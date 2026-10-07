using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using Microsoft.IdentityModel.Tokens;

namespace Tcp.TestSupport;

/// <summary>Builds signed SAML 2.0 assertions with a self-signed certificate for the saml2-bearer grant tests.</summary>
public sealed class SamlTestIssuer : IDisposable
{
    public const string Ns = "urn:oasis:names:tc:SAML:2.0:assertion";
    private readonly RSA _rsa = RSA.Create(2048);

    public SamlTestIssuer()
    {
        var request = new CertificateRequest("CN=btp-test-idp", _rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    public X509Certificate2 Certificate { get; }
    public string CertificatePem => Certificate.ExportCertificatePem();

    public sealed record Options
    {
        public string NameId { get; init; } = "";
        public string Audience { get; init; } = TcpFactory.PublicBaseUrl;
        public DateTimeOffset NotOnOrAfter { get; init; } = DateTimeOffset.UtcNow.AddMinutes(5);
        public DateTimeOffset NotBefore { get; init; } = DateTimeOffset.UtcNow.AddMinutes(-1);
        public bool Sign { get; init; } = true;
        public string SignatureMethod { get; init; } = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256";
        public string DigestMethod { get; init; } = "http://www.w3.org/2001/04/xmlenc#sha256";
        public string? AttributeName { get; init; }
        public string? AttributeValue { get; init; }
        public RSA? SigningKey { get; init; }
    }

    public string Build(Options options, Action<XmlDocument>? tamperAfterSigning = null)
    {
        var id = "_" + Guid.NewGuid().ToString("N");
        var attribute = options.AttributeName is null ? "" :
            $"<saml:AttributeStatement><saml:Attribute Name=\"{options.AttributeName}\"><saml:AttributeValue>{options.AttributeValue}</saml:AttributeValue></saml:Attribute></saml:AttributeStatement>";
        var xml = $"""
            <saml:Assertion xmlns:saml="{Ns}" ID="{id}" Version="2.0" IssueInstant="{DateTimeOffset.UtcNow:O}">
              <saml:Issuer>https://btp.test/idp</saml:Issuer>
              <saml:Subject><saml:NameID>{options.NameId}</saml:NameID></saml:Subject>
              <saml:Conditions NotBefore="{options.NotBefore:O}" NotOnOrAfter="{options.NotOnOrAfter:O}">
                <saml:AudienceRestriction><saml:Audience>{options.Audience}</saml:Audience></saml:AudienceRestriction>
              </saml:Conditions>
              {attribute}
            </saml:Assertion>
            """;

        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(xml);

        if (options.Sign)
        {
            var signed = new SignedXml(doc) { SigningKey = options.SigningKey ?? _rsa };
            signed.SignedInfo!.SignatureMethod = options.SignatureMethod;
            var reference = new Reference("#" + id) { DigestMethod = options.DigestMethod };
            reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
            reference.AddTransform(new XmlDsigExcC14NTransform());
            signed.AddReference(reference);
            signed.ComputeSignature();
            doc.DocumentElement!.AppendChild(doc.ImportNode(signed.GetXml(), true));
        }

        tamperAfterSigning?.Invoke(doc);
        return Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(doc.OuterXml));
    }

    public void Dispose()
    {
        Certificate.Dispose();
        _rsa.Dispose();
    }
}
