namespace FieldOps.Domain;

public sealed class InspectionTemplate : ITenantOwned
{
    private readonly List<InspectionTemplateItem> _items = [];

    private InspectionTemplate()
    {
    }

    public InspectionTemplate(Guid tenantId, string name, decimal passingScore)
    {
        if (passingScore is < 0 or > 100) throw new DomainRuleException("Passing score must be between 0 and 100.");
        Id = Guid.NewGuid();
        TenantId = tenantId;
        Name = string.IsNullOrWhiteSpace(name) ? throw new DomainRuleException("Template name is required.") : name.Trim();
        PassingScore = passingScore;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; set; }
    public string Name { get; private set; } = string.Empty;
    public decimal PassingScore { get; private set; }
    public IReadOnlyCollection<InspectionTemplateItem> Items => _items;

    public InspectionTemplateItem AddItem(
        string prompt,
        InspectionItemType type,
        bool required,
        decimal weight,
        decimal? minValue = null,
        decimal? maxValue = null,
        string? expectedText = null)
    {
        if (weight <= 0) throw new DomainRuleException("Inspection item weight must be positive.");
        if (type == InspectionItemType.Number && minValue.HasValue && maxValue.HasValue && minValue > maxValue)
        {
            throw new DomainRuleException("Numeric minimum cannot exceed maximum.");
        }

        var item = new InspectionTemplateItem(
            TenantId, Id, prompt, type, required, weight, minValue, maxValue, expectedText);
        _items.Add(item);
        return item;
    }
}

public sealed class InspectionTemplateItem : ITenantOwned
{
    private InspectionTemplateItem()
    {
    }

    internal InspectionTemplateItem(
        Guid tenantId,
        Guid templateId,
        string prompt,
        InspectionItemType type,
        bool required,
        decimal weight,
        decimal? minValue,
        decimal? maxValue,
        string? expectedText)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        TemplateId = templateId;
        Prompt = string.IsNullOrWhiteSpace(prompt) ? throw new DomainRuleException("Inspection prompt is required.") : prompt.Trim();
        Type = type;
        Required = required;
        Weight = weight;
        MinValue = minValue;
        MaxValue = maxValue;
        ExpectedText = expectedText;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; set; }
    public Guid TemplateId { get; private set; }
    public string Prompt { get; private set; } = string.Empty;
    public InspectionItemType Type { get; private set; }
    public bool Required { get; private set; }
    public decimal Weight { get; private set; }
    public decimal? MinValue { get; private set; }
    public decimal? MaxValue { get; private set; }
    public string? ExpectedText { get; private set; }
}

public sealed class InspectionSubmission : ITenantOwned
{
    private readonly List<InspectionAnswer> _answers = [];

    private InspectionSubmission()
    {
    }

    public InspectionSubmission(
        Guid tenantId,
        Guid templateId,
        Guid jobId,
        Guid submittedBy,
        decimal score,
        bool passed,
        DateTimeOffset submittedAt)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        TemplateId = templateId;
        JobId = jobId;
        SubmittedBy = submittedBy;
        Score = decimal.Round(score, 2);
        Passed = passed;
        SubmittedAt = submittedAt;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; set; }
    public Guid TemplateId { get; private set; }
    public Guid JobId { get; private set; }
    public Guid SubmittedBy { get; private set; }
    public decimal Score { get; private set; }
    public bool Passed { get; private set; }
    public DateTimeOffset SubmittedAt { get; private set; }
    public IReadOnlyCollection<InspectionAnswer> Answers => _answers;

    public void AddAnswer(InspectionAnswer answer) => _answers.Add(answer);
}

public sealed class InspectionAnswer : ITenantOwned
{
    private InspectionAnswer()
    {
    }

    public InspectionAnswer(
        Guid tenantId,
        Guid submissionId,
        Guid itemId,
        string? value,
        bool passed)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        SubmissionId = submissionId;
        ItemId = itemId;
        Value = value;
        Passed = passed;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; set; }
    public Guid SubmissionId { get; private set; }
    public Guid ItemId { get; private set; }
    public string? Value { get; private set; }
    public bool Passed { get; private set; }
}
