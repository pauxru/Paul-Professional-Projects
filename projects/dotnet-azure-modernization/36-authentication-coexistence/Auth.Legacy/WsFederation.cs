using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Auth.Legacy;

/// <summary>A SAML 1.1 assertion, reduced to the fields a WS-Federation relying party uses.</summary>
public sealed record SamlAssertion(
    string AssertionId,
    string Issuer,
    string NameIdentifier,
    DateTimeOffset NotBefore,
    DateTimeOffset NotOnOrAfter,
    string Audience,
    IReadOnlyList<KeyValuePair<string, string>> Attributes)
{
    public IEnumerable<string> Roles =>
        Attributes.Where(a => a.Key == ClaimNames.LegacyRole).Select(a => a.Value);
}

public static class ClaimNames
{
    public const string LegacyRole = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";
    public const string LegacyName = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name";
    public const string LegacyEmail = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress";
    public const string LegacyUpn = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn";
}

public enum SamlFailure
{
    None,
    BadSignature,
    Malformed,
    NotYetValid,
    Expired,
    WrongAudience,
    Replayed,
    UnknownIssuer,
}

public sealed record SamlValidation(SamlAssertion? Assertion, SamlFailure Failure)
{
    public bool Ok => Failure == SamlFailure.None;
}

/// <summary>
/// Issues and validates WS-Federation tokens over a deliberately tiny XML profile.
/// </summary>
/// <remarks>
/// <para>
/// The design decision worth defending: this does not use a general XML digital signature
/// validator. The entire family of signature-wrapping attacks exists because XML-DSig
/// separates "which bytes were signed" from "which elements the application later reads",
/// and general canonicalization is expressive enough to let an attacker satisfy the first
/// while controlling the second. A validator that follows a URI reference to find the
/// signed subtree can be pointed at a subtree that is not the one the relying party will
/// act on.
/// </para>
/// <para>
/// So the profile here has no references, no transforms, no namespace prefix rewriting and
/// no inclusive-namespace lists. Validation works the other way round: parse the document
/// into <see cref="SamlAssertion"/>, re-serialize <i>that object</i> through the single
/// canonical writer below, and require the signature to verify over those bytes. Anything
/// the attacker added that the parser did not read cannot survive the round trip, and
/// anything it did read is by definition what gets signed. The property is structural,
/// not a checklist.
/// </para>
/// <para>
/// The cost is real and is recorded in <c>docs/known-limitations.md</c>: this will not
/// interoperate with an arbitrary third-party STS. It interoperates with the one in front
/// of it, which is the actual requirement.
/// </para>
/// </remarks>
public sealed class WsFederation
{
    private readonly RSA _key;
    private readonly string _expectedIssuer;
    private readonly HashSet<string> _seenAssertionIds = [];

    public WsFederation(RSA key, string expectedIssuer)
    {
        _key = key;
        _expectedIssuer = expectedIssuer;
    }

    /// <summary>Assertion IDs consumed so far. Bounded in production by the token lifetime.</summary>
    public int ReplayCacheSize => _seenAssertionIds.Count;

    public string Issue(SamlAssertion assertion)
    {
        var canonical = Canonicalize(assertion);
        var signature = _key.SignData(canonical, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var document = new XmlDocument();
        var root = document.CreateElement("Assertion", "urn:oasis:names:tc:SAML:1.0:assertion");
        document.AppendChild(root);

        root.SetAttribute("AssertionID", assertion.AssertionId);
        root.SetAttribute("Issuer", assertion.Issuer);
        root.SetAttribute("IssueInstant", Instant(assertion.NotBefore));

        var conditions = document.CreateElement("Conditions", root.NamespaceURI);
        conditions.SetAttribute("NotBefore", Instant(assertion.NotBefore));
        conditions.SetAttribute("NotOnOrAfter", Instant(assertion.NotOnOrAfter));
        var audience = document.CreateElement("Audience", root.NamespaceURI);
        audience.InnerText = assertion.Audience;
        conditions.AppendChild(audience);
        root.AppendChild(conditions);

        var subject = document.CreateElement("NameIdentifier", root.NamespaceURI);
        subject.InnerText = assertion.NameIdentifier;
        root.AppendChild(subject);

        foreach (var attribute in assertion.Attributes)
        {
            var element = document.CreateElement("Attribute", root.NamespaceURI);
            element.SetAttribute("AttributeName", attribute.Key);
            element.InnerText = attribute.Value;
            root.AppendChild(element);
        }

        var signatureElement = document.CreateElement("SignatureValue", root.NamespaceURI);
        signatureElement.InnerText = Convert.ToBase64String(signature);
        root.AppendChild(signatureElement);

        return document.OuterXml;
    }

    public SamlValidation Validate(string xml, string expectedAudience, DateTimeOffset now)
    {
        SamlAssertion assertion;
        byte[] signature;
        try
        {
            (assertion, signature) = Parse(xml);
        }
        catch (Exception e) when (e is XmlException or FormatException or InvalidOperationException)
        {
            return new SamlValidation(null, SamlFailure.Malformed);
        }

        // Signature first. Nothing below this line may influence whether the token is
        // accepted, only which rejection it earns.
        var canonical = Canonicalize(assertion);
        if (!_key.VerifyData(canonical, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            return new SamlValidation(null, SamlFailure.BadSignature);
        }

        if (assertion.Issuer != _expectedIssuer)
        {
            return new SamlValidation(null, SamlFailure.UnknownIssuer);
        }

        if (assertion.Audience != expectedAudience)
        {
            // A token minted for a sibling application is a valid token. Accepting it here
            // is how one compromised relying party becomes all of them.
            return new SamlValidation(null, SamlFailure.WrongAudience);
        }

        if (now < assertion.NotBefore) return new SamlValidation(null, SamlFailure.NotYetValid);
        if (now >= assertion.NotOnOrAfter) return new SamlValidation(null, SamlFailure.Expired);

        if (!_seenAssertionIds.Add(assertion.AssertionId))
        {
            return new SamlValidation(null, SamlFailure.Replayed);
        }

        return new SamlValidation(assertion, SamlFailure.None);
    }

    private static (SamlAssertion, byte[]) Parse(string xml)
    {
        var settings = new XmlReaderSettings
        {
            // A token is attacker-supplied input. External entity resolution and DTD
            // processing turn a parser into a file reader and a request forwarder.
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = 256 * 1024,
        };

        var document = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(new StringReader(xml), settings))
        {
            document.Load(reader);
        }

        var root = document.DocumentElement
            ?? throw new InvalidOperationException("no document element");
        if (root.LocalName != "Assertion") throw new InvalidOperationException("not an assertion");

        string? signature = null;
        string? nameIdentifier = null;
        string? audience = null;
        DateTimeOffset? notBefore = null;
        DateTimeOffset? notOnOrAfter = null;
        var attributes = new List<KeyValuePair<string, string>>();

        foreach (XmlNode child in root.ChildNodes)
        {
            if (child is not XmlElement element) continue;
            switch (element.LocalName)
            {
                case "Conditions":
                    notBefore = ParseInstant(element.GetAttribute("NotBefore"));
                    notOnOrAfter = ParseInstant(element.GetAttribute("NotOnOrAfter"));
                    var audienceElement = element.ChildNodes.OfType<XmlElement>()
                        .SingleOrDefault(e => e.LocalName == "Audience");
                    audience = audienceElement?.InnerText;
                    break;

                case "NameIdentifier":
                    // Single, not First. Two NameIdentifiers is not a document this profile
                    // has an opinion about, so it is not a document this profile accepts.
                    if (nameIdentifier is not null) throw new InvalidOperationException("duplicate subject");
                    nameIdentifier = element.InnerText;
                    break;

                case "Attribute":
                    attributes.Add(new KeyValuePair<string, string>(
                        element.GetAttribute("AttributeName"), element.InnerText));
                    break;

                case "SignatureValue":
                    if (signature is not null) throw new InvalidOperationException("duplicate signature");
                    signature = element.InnerText;
                    break;

                default:
                    throw new InvalidOperationException($"unexpected element {element.LocalName}");
            }
        }

        if (signature is null || nameIdentifier is null || audience is null ||
            notBefore is null || notOnOrAfter is null)
        {
            throw new InvalidOperationException("missing required element");
        }

        var assertion = new SamlAssertion(
            root.GetAttribute("AssertionID"),
            root.GetAttribute("Issuer"),
            nameIdentifier,
            notBefore.Value,
            notOnOrAfter.Value,
            audience,
            attributes);

        return (assertion, Convert.FromBase64String(signature));
    }

    /// <summary>
    /// The one and only byte serialization of an assertion. Both signing and verification go
    /// through it, which is what makes "the bytes that were signed" and "the fields the
    /// application will read" the same thing by construction.
    /// </summary>
    private static byte[] Canonicalize(SamlAssertion assertion)
    {
        var builder = new StringBuilder();

        void Field(string value)
        {
            // Length-prefixed, so no combination of field contents can imitate a different
            // field split. Concatenating with a separator would let a role named "a|b"
            // become two roles.
            builder.Append(value.Length).Append(':').Append(value).Append('\n');
        }

        Field(assertion.AssertionId);
        Field(assertion.Issuer);
        Field(assertion.NameIdentifier);
        Field(Instant(assertion.NotBefore));
        Field(Instant(assertion.NotOnOrAfter));
        Field(assertion.Audience);
        builder.Append(assertion.Attributes.Count).Append('\n');
        foreach (var attribute in assertion.Attributes)
        {
            Field(attribute.Key);
            Field(attribute.Value);
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static string Instant(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseInstant(string value) =>
        DateTimeOffset.ParseExact(value, "yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
