using ZeroTrust.Domain.Common;
using ZeroTrust.Domain.Identity;

namespace ZeroTrust.UnitTests.Domain;

public class BreakGlassGrantTests
{
    [Fact]
    public void Constructor_Requires_Two_Person_Rule()
    {
        var ex = Assert.Throws<DomainException>(() =>
            new BreakGlassGrant("alice", "requestor", "requestor", "abcdefghij123", DateTime.UtcNow.AddMinutes(30)));
        Assert.Contains("two-person", ex.Message);
    }

    [Fact]
    public void Constructor_Requires_Justification_Length()
    {
        var ex = Assert.Throws<DomainException>(() =>
            new BreakGlassGrant("alice", "req", "app", "short", DateTime.UtcNow.AddMinutes(30)));
        Assert.Contains("justification", ex.Message);
    }

    [Fact]
    public void Active_While_In_Window_And_Not_Used()
    {
        var now = DateTime.UtcNow;
        var g = new BreakGlassGrant("alice", "req", "app", "adequately-long-justification", now.AddMinutes(10));
        Assert.True(g.IsActive(now));
    }

    [Fact]
    public void Inactive_After_MarkUsed()
    {
        var now = DateTime.UtcNow;
        var g = new BreakGlassGrant("alice", "req", "app", "adequately-long-justification", now.AddMinutes(10));
        g.MarkUsed();
        Assert.False(g.IsActive(now));
    }

    [Fact]
    public void Inactive_After_Expiry()
    {
        var now = DateTime.UtcNow;
        var g = new BreakGlassGrant("alice", "req", "app", "adequately-long-justification", now.AddMinutes(-1));
        Assert.False(g.IsActive(now));
    }
}
