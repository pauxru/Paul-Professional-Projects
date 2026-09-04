using Northstar.Domain.Common;

namespace Northstar.Domain.Policies;

public sealed class Policyholder
{
    private Policyholder()
    {
    }

    public Policyholder(Guid id, string name, string email)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
        {
            throw new DomainRuleException("Policyholder identity, name and email are required.");
        }

        Id = id;
        Name = name.Trim();
        Email = email.Trim().ToLowerInvariant();
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public List<Policy> Policies { get; private set; } = [];
}
