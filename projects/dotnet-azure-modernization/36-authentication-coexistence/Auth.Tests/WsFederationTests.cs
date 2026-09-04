using System.Security.Cryptography;
using System.Xml;
using Auth.Legacy;

namespace Auth.Tests;

/// <summary>
/// WS-Federation with a SAML 1.1 assertion. XML signatures are a category of their own:
/// the signature covers a *canonical form* of the document, so every question about what
/// is protected is really a question about what canonicalisation includes.
/// </summary>
public sealed class WsFederationTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Issuer = "urn:adfs.example.test";
    private const string Audience = "urn:ledger";

    private static SamlAssertion Assertion(
        string id = "_a1", string subject = "finance.director",
        string audience = Audience, string issuer = Issuer,
        DateTimeOffset? notBefore = null, DateTimeOffset? notOnOrAfter = null,
        params (string Key, string Value)[] attributes) =>
        new(id, issuer, subject,
            notBefore ?? Now.AddMinutes(-1),
            notOnOrAfter ?? Now.AddMinutes(30),
            audience,
            attributes.Length == 0
                ? [new KeyValuePair<string, string>(ClaimNames.LegacyRole, "Admin")]
                : attributes.Select(a => new KeyValuePair<string, string>(a.Key, a.Value)).ToArray());

    private static (WsFederation Federation, RSA Key) Fresh()
    {
        var rsa = RSA.Create(2048);
        return (new WsFederation(rsa, Issuer), rsa);
    }

    [Fact]
    public void AcceptsAValidAssertion()
    {
        var (federation, rsa) = Fresh();
        using var _ = rsa;

        var result = federation.Validate(federation.Issue(Assertion()), Audience, Now);

        Assert.True(result.Ok);
        Assert.Equal("finance.director", result.Assertion!.NameIdentifier);
        Assert.Equal(new[] { "Admin" }, result.Assertion.Roles);
    }

    [Fact]
    public void PreservesMultipleRoleAttributes()
    {
        var (federation, rsa) = Fresh();
        using var _ = rsa;

        var xml = federation.Issue(Assertion(attributes:
        [
            (ClaimNames.LegacyRole, "Admin"),
            (ClaimNames.LegacyRole, "Auditor"),
            (ClaimNames.LegacyEmail, "fd@example.test"),
        ]));

        var result = federation.Validate(xml, Audience, Now);
        Assert.Equal(new[] { "Admin", "Auditor" }, result.Assertion!.Roles);
    }

    [Fact]
    public void RejectsAReplayedAssertion()
    {
        // A bearer assertion is a password with an expiry date. Without a replay cache,
        // anything that observes one -- a proxy log, a browser history, an error report --
        // can re-present it until it expires.
        var (federation, rsa) = Fresh();
        using var _ = rsa;

        var xml = federation.Issue(Assertion());

        Assert.True(federation.Validate(xml, Audience, Now).Ok);
        var second = federation.Validate(xml, Audience, Now);

        Assert.False(second.Ok);
        Assert.Equal(SamlFailure.Replayed, second.Failure);
    }

    [Fact]
    public void ReplayCacheGrowsOncePerAssertion()
    {
        var (federation, rsa) = Fresh();
        using var _ = rsa;

        Assert.Equal(0, federation.ReplayCacheSize);
        federation.Validate(federation.Issue(Assertion("_1")), Audience, Now);
        federation.Validate(federation.Issue(Assertion("_2")), Audience, Now);
        Assert.Equal(2, federation.ReplayCacheSize);

        federation.Validate(federation.Issue(Assertion("_1")), Audience, Now);
        Assert.Equal(2, federation.ReplayCacheSize);
    }

    [Fact]
    public void RejectsAnAssertionForAnotherRelyingParty()
    {
        // Every relying party behind one ADFS trusts the same signing key. Without the
        // audience check, an assertion minted for the cafeteria menu app is a valid login
        // to the ledger.
        var (federation, rsa) = Fresh();
        using var _ = rsa;

        var xml = federation.Issue(Assertion(audience: "urn:cafeteria"));
        var result = federation.Validate(xml, Audience, Now);

        Assert.False(result.Ok);
        Assert.Equal(SamlFailure.WrongAudience, result.Failure);
    }

    [Fact]
    public void RejectsAnUnknownIssuer()
    {
        var (federation, rsa) = Fresh();
        using var _ = rsa;

        var xml = federation.Issue(Assertion(issuer: "urn:someone.else"));
        Assert.Equal(SamlFailure.UnknownIssuer, federation.Validate(xml, Audience, Now).Failure);
    }

    [Fact]
    public void RejectsAnExpiredAssertion()
    {
        var (federation, rsa) = Fresh();
        using var _ = rsa;

        var xml = federation.Issue(Assertion());
        Assert.Equal(SamlFailure.Expired, federation.Validate(xml, Audience, Now.AddHours(1)).Failure);
    }

    [Fact]
    public void RejectsAnAssertionThatIsNotYetValid()
    {
        var (federation, rsa) = Fresh();
        using var _ = rsa;

        var xml = federation.Issue(Assertion(notBefore: Now.AddMinutes(10),
                                             notOnOrAfter: Now.AddMinutes(40)));
        Assert.Equal(SamlFailure.NotYetValid, federation.Validate(xml, Audience, Now).Failure);
    }

    [Fact]
    public void RejectsAnAssertionSignedByAnotherKey()
    {
        using var mine = RSA.Create(2048);
        using var theirs = RSA.Create(2048);

        var attacker = new WsFederation(theirs, Issuer);
        var server = new WsFederation(mine, Issuer);

        Assert.Equal(SamlFailure.BadSignature,
                     server.Validate(attacker.Issue(Assertion()), Audience, Now).Failure);
    }

    [Fact]
    public void RejectsAnEditedRole()
    {
        // The classic. Change one attribute value, keep the signature, and hope the
        // validator checks the signature against the document it parsed rather than the
        // document it received.
        var (federation, rsa) = Fresh();
        using var _ = rsa;

        var document = new XmlDocument();
        document.LoadXml(federation.Issue(Assertion()));
        var role = document.GetElementsByTagName("Attribute")[0]!;
        role.InnerText = "SuperAdmin";

        Assert.Equal(SamlFailure.BadSignature,
                     federation.Validate(document.OuterXml, Audience, Now).Failure);
    }

    [Fact]
    public void RejectsAnAppendedAttribute()
    {
        // Signature wrapping in its simplest form. The original attributes are untouched
        // and the signature still covers them; the question is whether the *new* one is
        // inside what was signed.
        var (federation, rsa) = Fresh();
        using var _ = rsa;

        var document = new XmlDocument();
        document.LoadXml(federation.Issue(Assertion()));

        var extra = document.CreateElement("Attribute", document.DocumentElement!.NamespaceURI);
        extra.SetAttribute("AttributeName", ClaimNames.LegacyRole);
        extra.InnerText = "Admin";
        document.DocumentElement.AppendChild(extra);

        Assert.Equal(SamlFailure.BadSignature,
                     federation.Validate(document.OuterXml, Audience, Now).Failure);
    }

    [Fact]
    public void RejectsAnEditedSubject()
    {
        var (federation, rsa) = Fresh();
        using var _ = rsa;

        var document = new XmlDocument();
        document.LoadXml(federation.Issue(Assertion()));
        document.GetElementsByTagName("NameIdentifier")[0]!.InnerText = "ceo";

        Assert.Equal(SamlFailure.BadSignature,
                     federation.Validate(document.OuterXml, Audience, Now).Failure);
    }

    [Fact]
    public void RejectsAnExtendedValidityWindow()
    {
        // Conditions have to be inside the signature, or an expired assertion is renewable
        // by anyone holding a copy. The replacement instant is in the exact format the
        // issuer emits, so the parse succeeds and the signature check is what rejects it --
        // otherwise this test would pass for the wrong reason.
        var (federation, rsa) = Fresh();
        using var _ = rsa;

        var document = new XmlDocument();
        document.LoadXml(federation.Issue(Assertion()));
        ((XmlElement)document.GetElementsByTagName("Conditions")[0]!)
            .SetAttribute("NotOnOrAfter", "2030-01-01T00:00:00.000Z");

        Assert.Equal(SamlFailure.BadSignature,
                     federation.Validate(document.OuterXml, Audience, Now).Failure);
    }

    [Fact]
    public void RejectsAnUnparseableTimestamp()
    {
        var (federation, rsa) = Fresh();
        using var _ = rsa;

        var document = new XmlDocument();
        document.LoadXml(federation.Issue(Assertion()));
        ((XmlElement)document.GetElementsByTagName("Conditions")[0]!)
            .SetAttribute("NotOnOrAfter", "whenever");

        Assert.Equal(SamlFailure.Malformed,
                     federation.Validate(document.OuterXml, Audience, Now).Failure);
    }

    [Fact]
    public void RejectsAnEditedAudience()
    {
        var (federation, rsa) = Fresh();
        using var _ = rsa;

        var document = new XmlDocument();
        document.LoadXml(federation.Issue(Assertion(audience: "urn:cafeteria")));
        document.GetElementsByTagName("Audience")[0]!.InnerText = Audience;

        Assert.Equal(SamlFailure.BadSignature,
                     federation.Validate(document.OuterXml, Audience, Now).Failure);
    }

    [Fact]
    public void RejectsAnEditedAssertionId()
    {
        // Editing the ID is how a replay evades a replay cache keyed on the ID.
        var (federation, rsa) = Fresh();
        using var _ = rsa;

        var document = new XmlDocument();
        document.LoadXml(federation.Issue(Assertion()));
        document.DocumentElement!.SetAttribute("AssertionID", "_forged");

        Assert.Equal(SamlFailure.BadSignature,
                     federation.Validate(document.OuterXml, Audience, Now).Failure);
    }

    [Fact]
    public void RejectsAStrippedSignature()
    {
        var (federation, rsa) = Fresh();
        using var _ = rsa;

        var document = new XmlDocument();
        document.LoadXml(federation.Issue(Assertion()));
        var signature = document.GetElementsByTagName("SignatureValue")[0]!;
        signature.ParentNode!.RemoveChild(signature);

        Assert.False(federation.Validate(document.OuterXml, Audience, Now).Ok);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<Assertion/>")]
    [InlineData("<Assertion xmlns=\"urn:oasis:names:tc:SAML:1.0:assertion\"/>")]
    [InlineData("<a><b/></a>")]
    public void RejectsMalformedDocuments(string xml)
    {
        var (federation, rsa) = Fresh();
        using var _ = rsa;

        var result = federation.Validate(xml, Audience, Now);
        Assert.False(result.Ok);
        Assert.Equal(SamlFailure.Malformed, result.Failure);
    }

    [Fact]
    public void RejectsADocumentWithAnExternalEntity()
    {
        // XXE. A SAML endpoint that resolves entities reads local files on behalf of
        // whoever posts to it; the assertion does not even have to be valid.
        const string xxe =
            "<!DOCTYPE r [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]>"
          + "<Assertion xmlns=\"urn:oasis:names:tc:SAML:1.0:assertion\">&e;</Assertion>";

        var (federation, rsa) = Fresh();
        using var _ = rsa;

        var result = federation.Validate(xxe, Audience, Now);
        Assert.False(result.Ok);
        Assert.Equal(SamlFailure.Malformed, result.Failure);
    }

    [Fact]
    public void RejectsABillionLaughsExpansion()
    {
        var bomb =
            "<!DOCTYPE r [<!ENTITY a \"aaaaaaaaaa\">"
          + "<!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">"
          + "<!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\">"
          + "<!ENTITY d \"&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;\">]>"
          + "<Assertion xmlns=\"urn:oasis:names:tc:SAML:1.0:assertion\">&d;</Assertion>";

        var (federation, rsa) = Fresh();
        using var _ = rsa;

        Assert.False(federation.Validate(bomb, Audience, Now).Ok);
    }

    [Fact]
    public void SignatureIsDeterministicForTheSameAssertion()
    {
        // PKCS#1 v1.5 is deterministic, so this is really a test that canonicalisation is:
        // if the same assertion canonicalised differently on two calls, the signature would
        // differ and nothing would ever validate reliably.
        var (federation, rsa) = Fresh();
        using var _ = rsa;

        Assert.Equal(federation.Issue(Assertion()), federation.Issue(Assertion()));
    }
}
