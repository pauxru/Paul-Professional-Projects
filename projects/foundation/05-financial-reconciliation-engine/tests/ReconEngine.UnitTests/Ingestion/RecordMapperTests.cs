using ReconEngine.Application.Ingestion;
using ReconEngine.Domain.Enums;
using ReconEngine.UnitTests.TestKit;

namespace ReconEngine.UnitTests.Ingestion;

/// <summary>Mapping a tokenised row to a normalised record: units, signs, currencies, dates, timezones.</summary>
public sealed class RecordMapperTests
{
    private static readonly TestClock Clock = new();

    private static FileFormatProfile Profile(
        string decimalSep = ".",
        bool minorUnits = false,
        int sign = 1,
        string offset = "+00:00",
        string[]? dateFormats = null) => new()
    {
        Name = "t",
        Format = RecordFileFormat.Csv,
        Source = RecordSource.Internal,
        HasHeader = false,
        Delimiter = ',',
        DecimalSeparator = decimalSep,
        AmountInMinorUnits = minorUnits,
        AmountSign = sign,
        SourceUtcOffset = offset,
        DefaultCurrency = "KES",
        DateFormats = dateFormats ?? new[] { "yyyy-MM-dd" },
        Fields = new[]
        {
            new FieldSpec(RecordField.Reference, 0),
            new FieldSpec(RecordField.Amount, 1),
            new FieldSpec(RecordField.Currency, 2),
            new FieldSpec(RecordField.Date, 3),
        },
    };

    private static TokenizedRow Row(params string[] fields) => new(7, fields, string.Join(',', fields));

    [Fact]
    public void Maps_a_well_formed_row_to_minor_units()
    {
        var result = RecordMapper.Map(Row("REF1", "100.00", "KES", "2024-01-15"), Profile(), null, Clock);

        Assert.True(result.Ok);
        Assert.Equal(10_000, result.Record!.AmountMinor);
        Assert.Equal("KES", result.Record.Currency);
        Assert.Equal("REF1", result.Record.CanonicalReference);
        Assert.Equal(7, result.Record.LineNumber);
    }

    [Fact]
    public void Missing_reference_is_rejected()
    {
        var result = RecordMapper.Map(Row("", "100.00", "KES", "2024-01-15"), Profile(), null, Clock);
        Assert.False(result.Ok);
        Assert.Contains("reference", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_currency_is_rejected()
    {
        var result = RecordMapper.Map(Row("REF1", "100.00", "ZZZ", "2024-01-15"), Profile(), null, Clock);
        Assert.False(result.Ok);
        Assert.Contains("currency", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unparseable_amount_is_rejected()
    {
        var result = RecordMapper.Map(Row("REF1", "not-a-number", "KES", "2024-01-15"), Profile(), null, Clock);
        Assert.False(result.Ok);
        Assert.Contains("amount", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unparseable_date_is_rejected()
    {
        var result = RecordMapper.Map(Row("REF1", "100.00", "KES", "31/13/2024"), Profile(), null, Clock);
        Assert.False(result.Ok);
        Assert.Contains("date", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Accountancy_parentheses_are_read_as_negative()
    {
        var result = RecordMapper.Map(Row("REF1", "(100.00)", "KES", "2024-01-15"), Profile(), null, Clock);
        Assert.True(result.Ok);
        Assert.Equal(-10_000, result.Record!.AmountMinor);
    }

    [Fact]
    public void Zero_amount_is_accepted()
    {
        var result = RecordMapper.Map(Row("REF1", "0.00", "KES", "2024-01-15"), Profile(), null, Clock);
        Assert.True(result.Ok);
        Assert.Equal(0, result.Record!.AmountMinor);
    }

    [Fact]
    public void European_decimal_separator_is_supported()
    {
        var result = RecordMapper.Map(Row("REF1", "1.234,50", "KES", "2024-01-15"), Profile(decimalSep: ","), null, Clock);
        Assert.True(result.Ok);
        Assert.Equal(123_450, result.Record!.AmountMinor); // 1,234.50 major -> 123450 minor
    }

    [Fact]
    public void Amount_sign_convention_can_flip_the_sign()
    {
        var result = RecordMapper.Map(Row("REF1", "100.00", "KES", "2024-01-15"), Profile(sign: -1), null, Clock);
        Assert.True(result.Ok);
        Assert.Equal(-10_000, result.Record!.AmountMinor);
    }

    [Fact]
    public void Minor_unit_profiles_take_the_amount_verbatim()
    {
        var result = RecordMapper.Map(Row("REF1", "10000", "KES", "2024-01-15"), Profile(minorUnits: true), null, Clock);
        Assert.True(result.Ok);
        Assert.Equal(10_000, result.Record!.AmountMinor);
    }

    [Fact]
    public void Source_timezone_is_normalised_to_utc()
    {
        var result = RecordMapper.Map(
            Row("REF1", "100.00", "KES", "2024-01-15 12:00:00"),
            Profile(offset: "+03:00", dateFormats: new[] { "yyyy-MM-dd HH:mm:ss" }),
            null, Clock);

        Assert.True(result.Ok);
        Assert.Equal(new DateTime(2024, 1, 15, 9, 0, 0, DateTimeKind.Utc), result.Record!.TransactionDateUtc);
        Assert.Equal(new DateOnly(2024, 1, 15), result.Record.ValueDate);
    }
}
