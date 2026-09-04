using RagAssistant.Application.Security;
using RagAssistant.Domain.Documents;
using RagAssistant.Domain.Retrieval;

namespace RagAssistant.UnitTests.Security;

public sealed class AclPostFilterTests
{
    [Fact]
    public void Filter_RemovesChunksBelongingToInaccessibleDocuments()
    {
        var restrictedDoc = Guid.NewGuid();
        var openDoc = Guid.NewGuid();
        var restrictedAcl = new AccessControlList(["board"], [], Classification.Restricted);
        var openAcl = new AccessControlList(["employee"], [], Classification.Internal);
        var user = new UserPrincipal("u", ["employee"], [], Classification.Internal);

        var chunks = new[]
        {
            new RetrievedChunk(Guid.NewGuid(), restrictedDoc, "Restricted", 0, 0, 10, "board only", 1.0, Classification.Restricted),
            new RetrievedChunk(Guid.NewGuid(), openDoc, "Open", 0, 0, 10, "public", 0.5, Classification.Internal),
        };

        var filtered = AclPostFilter.Filter(chunks, user, id =>
            id == restrictedDoc ? restrictedAcl : openAcl);

        Assert.Single(filtered);
        Assert.Equal(openDoc, filtered[0].DocumentId);
    }

    [Fact]
    public void Filter_UnknownDocument_IsExcluded()
    {
        var user = new UserPrincipal("u", ["employee"], [], Classification.Internal);
        var chunks = new[]
        {
            new RetrievedChunk(Guid.NewGuid(), Guid.NewGuid(), "Ghost", 0, 0, 5, "content", 1.0, Classification.Internal),
        };
        var filtered = AclPostFilter.Filter(chunks, user, _ => null);
        Assert.Empty(filtered);
    }
}
