using Northstar.Secrets.Domain;

namespace Northstar.Secrets.UnitTests;

public sealed class DomainLifecycleTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 3, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NormalizeName_WithHierarchicalName_NormalizesCase()
    {
        Assert.Equal("orders/prod/database", SecretRecord.NormalizeName("Orders/Prod/Database"));
    }

    [Theory]
    [InlineData("missing-segments")]
    [InlineData("orders/prod")]
    [InlineData("orders/prod/invalid value")]
    public void NormalizeName_WithInvalidShape_Rejects(string value)
    {
        Assert.Throws<DomainRuleException>(() => SecretRecord.NormalizeName(value));
    }

    [Fact]
    public void SecretReference_Parse_RoundTripsDocumentedFormat()
    {
        var reference = SecretReference.Parse("@secret:orders/prod/database#v3");

        Assert.Equal("orders/prod/database", reference.Name);
        Assert.Equal(3, reference.Version);
        Assert.Equal("@secret:orders/prod/database#v3", reference.ToString());
    }

    [Fact]
    public void Promote_SecondVersion_MaintainsTwoActiveVersionWindow()
    {
        var secret = NewSecret();
        var first = Stage(secret, 1);
        secret.Promote(first.VersionNumber, Now);
        var second = Stage(secret, 2);
        secret.Promote(second.VersionNumber, Now.AddHours(1));

        Assert.Equal(SecretVersionState.Previous, first.State);
        Assert.Equal(SecretVersionState.Current, second.State);
        Assert.Equal(2, secret.Versions.Count(x =>
            x.State is SecretVersionState.Current or SecretVersionState.Previous));
    }

    [Fact]
    public void Promote_ThirdVersion_DeprecatesOldestVersion()
    {
        var secret = NewSecret();
        var first = Stage(secret, 1);
        secret.Promote(1, Now);
        var second = Stage(secret, 2);
        secret.Promote(2, Now.AddHours(1));
        var third = Stage(secret, 3);
        secret.Promote(3, Now.AddHours(2));

        Assert.Equal(SecretVersionState.Deprecated, first.State);
        Assert.Equal(SecretVersionState.Previous, second.State);
        Assert.Equal(SecretVersionState.Current, third.State);
    }

    [Fact]
    public void RevokeAll_InvalidatesEveryVersion()
    {
        var secret = NewSecret();
        var version = Stage(secret, 1);
        secret.Promote(version.VersionNumber, Now);

        secret.RevokeAll(Now.AddMinutes(1));

        Assert.All(secret.Versions, x => Assert.Equal(SecretVersionState.Revoked, x.State));
        Assert.Throws<DomainRuleException>(() => secret.GetReadableVersion(null, Now.AddMinutes(2)));
    }

    [Fact]
    public void DestroyVersion_ClearsCryptographicMaterial()
    {
        var secret = NewSecret();
        var first = Stage(secret, 1);
        secret.Promote(1, Now);
        var second = Stage(secret, 2);
        secret.Promote(2, Now.AddMinutes(1));

        secret.DestroyVersion(first.VersionNumber, Now.AddMinutes(2));

        Assert.Equal(SecretVersionState.Destroyed, first.State);
        Assert.Empty(first.Ciphertext);
        Assert.Empty(first.WrappedDataEncryptionKey);
    }

    [Fact]
    public void RotationStateMachine_SkippingState_IsRejected()
    {
        var rotation = new RotationOperation(
            Guid.NewGuid(), Guid.NewGuid(), RotationStrategyKind.DualWrite, "idempotency",
            "operator", "correlation", Now, Now.AddMinutes(10), null);

        Assert.Throws<DomainRuleException>(() =>
            rotation.TransitionTo(RotationState.Promoting, Now));
    }

    private static SecretRecord NewSecret() =>
        SecretRecord.Register(
            Guid.NewGuid(),
            "orders/prod/database",
            SecretType.DatabasePassword,
            "Northstar Platform Engineering (fictional)",
            "prod",
            SecretCriticality.High,
            ["synthetic"],
            "test",
            TimeSpan.FromDays(30),
            TimeSpan.FromDays(90),
            TimeSpan.FromDays(1),
            Now);

    private static SecretVersion Stage(SecretRecord secret, int expectedVersion)
    {
        var version = secret.StageVersion(
            [1, 2, 3],
            new byte[12],
            new byte[16],
            new byte[60],
            "v1",
            Now.AddHours(expectedVersion),
            Now.AddDays(90));
        Assert.Equal(expectedVersion, version.VersionNumber);
        return version;
    }
}
