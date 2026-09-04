using Healthcare.Domain.Common;

namespace Healthcare.Domain.Encounters;

/// <summary>
/// Vitals reading with unit validation and plausibility ranges. Ranges are conservative adult
/// outpatient ranges intended for a demo workflow — they are not medical guidance.
/// </summary>
public sealed class VitalReading
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid EncounterId { get; private set; }
    public string Kind { get; private set; } = string.Empty;
    public decimal Value { get; private set; }
    public string Unit { get; private set; } = string.Empty;
    public DateTimeOffset RecordedAtUtc { get; private set; }

    private VitalReading() { }

    private static readonly Dictionary<string, (string CanonicalUnit, string[] AcceptedUnits, decimal Lo, decimal Hi)> Rules =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["systolic_bp"] = ("mmHg", new[] { "mmHg", "mm hg", "mmhg" }, 60m, 260m),
            ["diastolic_bp"] = ("mmHg", new[] { "mmHg", "mm hg", "mmhg" }, 30m, 160m),
            ["heart_rate"] = ("bpm", new[] { "bpm", "beats/min" }, 30m, 220m),
            ["temperature"] = ("C", new[] { "C", "celsius", "°C" }, 30m, 43m),
            ["respiratory_rate"] = ("rpm", new[] { "rpm", "breaths/min" }, 5m, 60m),
            ["oxygen_saturation"] = ("%", new[] { "%", "percent" }, 50m, 100m),
            ["weight"] = ("kg", new[] { "kg", "kilograms" }, 1m, 400m),
            ["height"] = ("cm", new[] { "cm", "centimeters" }, 20m, 260m),
            ["blood_glucose"] = ("mmol/L", new[] { "mmol/l", "mmol per litre" }, 1m, 40m),
        };

    public static VitalReading Create(Guid encounterId, string kind, decimal value, string unit, DateTimeOffset at)
    {
        if (!Rules.TryGetValue(kind, out var rule))
            throw new DomainException("vital.kind.unknown", $"Unknown vital kind '{kind}'.");
        if (!rule.AcceptedUnits.Any(u => string.Equals(u, unit, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("vital.unit.invalid",
                $"Unit '{unit}' not accepted for {kind}; expected {rule.CanonicalUnit}.");
        if (value < rule.Lo || value > rule.Hi)
            throw new DomainException("vital.value.implausible",
                $"Value {value} {rule.CanonicalUnit} for {kind} is outside plausible range {rule.Lo}..{rule.Hi}.");
        return new VitalReading
        {
            EncounterId = encounterId,
            Kind = kind.ToLowerInvariant(),
            Value = value,
            Unit = rule.CanonicalUnit,
            RecordedAtUtc = at
        };
    }
}
