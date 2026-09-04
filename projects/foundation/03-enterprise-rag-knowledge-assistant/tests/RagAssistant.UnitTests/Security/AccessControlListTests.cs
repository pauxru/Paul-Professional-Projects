using RagAssistant.Domain.Documents;

namespace RagAssistant.UnitTests.Security;

public sealed class AccessControlListTests
{
    [Fact]
    public void Allows_UserWithMatchingRole_ReturnsTrue()
    {
        var acl = new AccessControlList(["employee"], [], Classification.Internal);
        var user = new UserPrincipal("u1", ["employee"], [], Classification.Internal);
        Assert.True(acl.Allows(user));
    }

    [Fact]
    public void Allows_UserWithoutRole_ReturnsFalse()
    {
        var acl = new AccessControlList(["board"], [], Classification.Restricted);
        var user = new UserPrincipal("u1", ["employee"], [], Classification.Confidential);
        Assert.False(acl.Allows(user));
    }

    [Fact]
    public void Allows_ClassificationTooHigh_ReturnsFalse()
    {
        var acl = new AccessControlList([], [], Classification.Confidential);
        var user = new UserPrincipal("u1", [], [], Classification.Internal);
        Assert.False(acl.Allows(user));
    }

    [Fact]
    public void Allows_DepartmentMismatch_ReturnsFalse()
    {
        var acl = new AccessControlList(["employee"], ["finance"], Classification.Internal);
        var user = new UserPrincipal("u1", ["employee"], ["engineering"], Classification.Internal);
        Assert.False(acl.Allows(user));
    }

    [Fact]
    public void Allows_MatchingDepartmentOverlap_ReturnsTrue()
    {
        var acl = new AccessControlList(["employee"], ["finance", "engineering"], Classification.Internal);
        var user = new UserPrincipal("u1", ["employee"], ["engineering"], Classification.Internal);
        Assert.True(acl.Allows(user));
    }

    [Fact]
    public void Allows_PublicDocument_AllowsPublicUser()
    {
        var acl = new AccessControlList([], [], Classification.Public);
        var user = new UserPrincipal("u1", [], [], Classification.Public);
        Assert.True(acl.Allows(user));
    }
}
