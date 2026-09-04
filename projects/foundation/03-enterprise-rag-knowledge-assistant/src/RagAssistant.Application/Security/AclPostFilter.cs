using RagAssistant.Domain.Documents;
using RagAssistant.Domain.Retrieval;

namespace RagAssistant.Application.Security;

public static class AclPostFilter
{
    public static IReadOnlyList<RetrievedChunk> Filter(
        IEnumerable<RetrievedChunk> chunks,
        UserPrincipal user,
        Func<Guid, AccessControlList?> aclLookup)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(aclLookup);

        var results = new List<RetrievedChunk>();
        foreach (var chunk in chunks)
        {
            var acl = aclLookup(chunk.DocumentId);
            if (acl is null)
            {
                continue;
            }

            if (!acl.Allows(user))
            {
                continue;
            }

            results.Add(chunk);
        }

        return results;
    }
}
