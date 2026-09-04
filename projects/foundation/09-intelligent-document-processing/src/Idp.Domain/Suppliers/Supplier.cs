namespace Idp.Domain.Suppliers;

/// <summary>Supplier master data plus per-supplier learned extraction hints.</summary>
public sealed class Supplier
{
    private readonly List<SupplierHint> _hints = new();

    public Guid Id { get; private set; }
    public string Name { get; private set; } = default!;
    public string? TaxId { get; private set; }
    public string Aliases { get; private set; } = string.Empty;
    public string? DefaultCurrency { get; private set; }
    public string? BankAccount { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public IReadOnlyCollection<SupplierHint> Hints => _hints;

    private Supplier() { }

    public Supplier(
        string name, string? taxId, string? defaultCurrency, DateTime nowUtc,
        IEnumerable<string>? aliases = null, string? bankAccount = null)
    {
        Id = Guid.NewGuid();
        Name = name;
        TaxId = taxId;
        DefaultCurrency = defaultCurrency;
        BankAccount = bankAccount;
        Aliases = aliases is null ? string.Empty : string.Join("|", aliases);
        IsActive = true;
        CreatedAtUtc = nowUtc;
    }

    public IReadOnlyList<string> AliasList =>
        string.IsNullOrEmpty(Aliases)
            ? Array.Empty<string>()
            : Aliases.Split('|', StringSplitOptions.RemoveEmptyEntries);

    public SupplierHint? HintFor(string fieldKey) =>
        _hints.FirstOrDefault(h => h.FieldKey == fieldKey);

    /// <summary>Record (or reinforce) a learned anchor for a field from a reviewer correction.</summary>
    public SupplierHint LearnHint(string fieldKey, string anchorText, Guid documentId, DateTime nowUtc)
    {
        var existing = _hints.FirstOrDefault(
            h => h.FieldKey == fieldKey &&
                 string.Equals(h.AnchorText, anchorText, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Reinforce(documentId, nowUtc);
            return existing;
        }
        // A new anchor for this field replaces any prior anchor for the same field.
        _hints.RemoveAll(h => h.FieldKey == fieldKey);
        var hint = new SupplierHint(Id, fieldKey, anchorText, documentId, nowUtc);
        _hints.Add(hint);
        return hint;
    }

    public void Deactivate() => IsActive = false;
}
