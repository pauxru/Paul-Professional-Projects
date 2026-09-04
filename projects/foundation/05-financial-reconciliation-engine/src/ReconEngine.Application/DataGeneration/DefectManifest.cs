using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReconEngine.Application.DataGeneration;

/// <summary>
/// The ground-truth manifest emitted alongside a generated dataset. It records the <b>exact</b> number
/// of each defect class injected, so a reconciliation run over the same data can be asserted against it
/// (no magic numbers in tests, no fabricated figures in docs).
/// </summary>
public sealed record DefectManifest
{
    public required int Rows { get; init; }
    public required int Seed { get; init; }

    public required int CleanPairs { get; init; }

    public required int DuplicateInternal { get; init; }
    public required int DuplicateExternal { get; init; }
    public required int AmountMismatch { get; init; }
    public required int MissingInExternal { get; init; }
    public required int MissingInInternal { get; init; }
    public required int CurrencyMismatch { get; init; }
    public required int StatusMismatch { get; init; }
    public required int DateOutOfWindow { get; init; }
    public required int FeeAdjustedClean { get; init; }
    public required int FeeVariance { get; init; }
    public required int Refunds { get; init; }

    public required int TotalInternalRows { get; init; }
    public required int TotalExternalRows { get; init; }

    /// <summary>Matches the engine should make automatically: clean + status-mismatch + fee (clean+variance) + refunds.</summary>
    public int ExpectedAutoMatches => CleanPairs + StatusMismatch + FeeAdjustedClean + FeeVariance + Refunds;

    /// <summary>Every exception the run should raise, keyed by <c>ExceptionType</c> name.</summary>
    public IReadOnlyDictionary<string, int> ExpectedExceptionsByType => new Dictionary<string, int>
    {
        ["DuplicateInternal"] = DuplicateInternal,
        ["DuplicateExternal"] = DuplicateExternal,
        ["AmountMismatch"] = AmountMismatch,
        ["MissingInExternal"] = MissingInExternal,
        ["MissingInInternal"] = MissingInInternal,
        ["CurrencyMismatch"] = CurrencyMismatch,
        ["StatusMismatch"] = StatusMismatch,
        ["DateOutOfWindow"] = DateOutOfWindow,
        ["FeeVariance"] = FeeVariance,
    };

    public int ExpectedExceptions =>
        DuplicateInternal + DuplicateExternal + AmountMismatch + MissingInExternal + MissingInInternal +
        CurrencyMismatch + StatusMismatch + DateOutOfWindow + FeeVariance;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    public static DefectManifest Deserialize(string json) =>
        JsonSerializer.Deserialize<DefectManifest>(json, JsonOptions)
        ?? throw new InvalidOperationException("Could not deserialize defect manifest.");
}
