namespace RagAssistant.Domain.Documents;

public sealed class UserPrincipal
{
    public UserPrincipal(string userId, IEnumerable<string> roles, IEnumerable<string> departments, Classification maxClassification)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User id must be provided.", nameof(userId));
        }

        UserId = userId;
        Roles = new HashSet<string>((roles ?? []).Select(v => v.Trim()), StringComparer.OrdinalIgnoreCase);
        Departments = new HashSet<string>((departments ?? []).Select(v => v.Trim()), StringComparer.OrdinalIgnoreCase);
        MaxClassification = maxClassification;
    }

    public string UserId { get; }
    public IReadOnlySet<string> Roles { get; }
    public IReadOnlySet<string> Departments { get; }
    public Classification MaxClassification { get; }

    public static UserPrincipal Anonymous { get; } = new(
        "anonymous",
        ["anonymous"],
        [],
        Classification.Public);
}
