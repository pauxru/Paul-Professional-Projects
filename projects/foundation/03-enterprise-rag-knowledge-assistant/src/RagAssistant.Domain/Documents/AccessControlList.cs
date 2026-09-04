namespace RagAssistant.Domain.Documents;

public sealed class AccessControlList
{
    private AccessControlList()
    {
        Roles = Array.Empty<string>();
        Departments = Array.Empty<string>();
        Classification = Classification.Internal;
    }

    public AccessControlList(IEnumerable<string>? roles = null, IEnumerable<string>? departments = null, Classification classification = Classification.Internal)
    {
        Roles = Normalize(roles);
        Departments = Normalize(departments);
        Classification = classification;
    }

    public IReadOnlyCollection<string> Roles { get; private set; }
    public IReadOnlyCollection<string> Departments { get; private set; }
    public Classification Classification { get; private set; }

    public bool Allows(UserPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.MaxClassification < Classification)
        {
            return false;
        }

        if (Roles.Count > 0 && !Roles.Any(role => user.Roles.Contains(role, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (Departments.Count > 0 && !Departments.Any(dept => user.Departments.Contains(dept, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    private static IReadOnlyCollection<string> Normalize(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return Array.Empty<string>();
        }

        return new HashSet<string>(
            values.Select(v => (v ?? string.Empty).Trim()).Where(v => v.Length > 0),
            StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
