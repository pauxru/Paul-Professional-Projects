namespace RagAssistant.Domain.Prompts;

public sealed class PromptTemplate
{
    private PromptTemplate()
    {
        Id = Guid.Empty;
        Name = string.Empty;
        Body = string.Empty;
        Hash = string.Empty;
        Version = string.Empty;
    }

    public PromptTemplate(Guid id, string name, string version, string body, string hash, DateTimeOffset createdAt, bool isActive)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Prompt id must be provided.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Prompt name must be provided.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("Prompt version must be provided.", nameof(version));
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("Prompt body must be provided.", nameof(body));
        }

        Id = id;
        Name = name.Trim();
        Version = version.Trim();
        Body = body;
        Hash = hash ?? string.Empty;
        CreatedAt = createdAt;
        IsActive = isActive;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public string Version { get; private set; }
    public string Body { get; private set; }
    public string Hash { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public bool IsActive { get; private set; }

    public void Deactivate()
    {
        IsActive = false;
    }

    public string Identifier => $"{Name}@{Version}";
}
