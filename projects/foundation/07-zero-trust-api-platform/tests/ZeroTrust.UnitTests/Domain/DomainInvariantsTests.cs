using ZeroTrust.Domain.Customer;
using ZeroTrust.Domain.Identity;

namespace ZeroTrust.UnitTests.Domain;

public class AccountOwnershipTests
{
    [Fact]
    public void IsOwnedBy_Returns_True_For_Owner()
    {
        var a = new Account("NTSF-0001", "alice", "chk", "USD", 1_000_00);
        Assert.True(a.IsOwnedBy("alice"));
    }

    [Fact]
    public void IsOwnedBy_Returns_False_For_Others()
    {
        var a = new Account("NTSF-0001", "alice", "chk", "USD", 1_000_00);
        Assert.False(a.IsOwnedBy("bob"));
    }

    [Fact]
    public void IsOwnedBy_Returns_False_For_Empty_Subject()
    {
        var a = new Account("NTSF-0001", "alice", "chk", "USD", 1_000_00);
        Assert.False(a.IsOwnedBy(""));
    }
}

public class RefreshTokenTests
{
    [Fact]
    public void Fresh_Token_Is_Active()
    {
        var t = new RefreshToken("hash", Guid.NewGuid(), "sub", "aud", "scope", DateTime.UtcNow.AddDays(7));
        Assert.True(t.IsActive(DateTime.UtcNow));
    }

    [Fact]
    public void Consumed_Token_Is_Inactive()
    {
        var t = new RefreshToken("hash", Guid.NewGuid(), "sub", "aud", "scope", DateTime.UtcNow.AddDays(7));
        t.MarkConsumed(Guid.NewGuid());
        Assert.False(t.IsActive(DateTime.UtcNow));
    }

    [Fact]
    public void Revoked_Token_Is_Inactive()
    {
        var t = new RefreshToken("hash", Guid.NewGuid(), "sub", "aud", "scope", DateTime.UtcNow.AddDays(7));
        t.Revoke();
        Assert.False(t.IsActive(DateTime.UtcNow));
    }

    [Fact]
    public void Expired_Token_Is_Inactive()
    {
        var t = new RefreshToken("hash", Guid.NewGuid(), "sub", "aud", "scope", DateTime.UtcNow.AddHours(-1));
        Assert.False(t.IsActive(DateTime.UtcNow));
    }
}

public class ApiKeyLifecycleTests
{
    [Fact]
    public void Usable_When_Not_Deprecated()
    {
        var k = new ApiKey("kid", "hash", "salt", "acme", "s", null);
        Assert.True(k.IsUsable(DateTime.UtcNow, enforcementActive: true));
    }

    [Fact]
    public void Usable_During_Dual_Accept_Even_If_Past_Deprecation_When_Enforcement_Off()
    {
        var k = new ApiKey("kid", "hash", "salt", "acme", "s", DateTime.UtcNow.AddDays(-1));
        Assert.True(k.IsUsable(DateTime.UtcNow, enforcementActive: false));
    }

    [Fact]
    public void Unusable_Past_Deprecation_When_Enforcement_Active()
    {
        var k = new ApiKey("kid", "hash", "salt", "acme", "s", DateTime.UtcNow.AddDays(-1));
        Assert.False(k.IsUsable(DateTime.UtcNow, enforcementActive: true));
    }

    [Fact]
    public void Unusable_After_Revoke()
    {
        var k = new ApiKey("kid", "hash", "salt", "acme", "s", null);
        k.Revoke();
        Assert.False(k.IsUsable(DateTime.UtcNow, enforcementActive: false));
    }
}
