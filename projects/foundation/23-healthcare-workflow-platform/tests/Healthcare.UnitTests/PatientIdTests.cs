using Healthcare.Domain.Patients;
using Xunit;

namespace Healthcare.UnitTests.Domain;

public class PatientIdTests
{
    [Fact]
    public void Create_Then_Parse_RoundTrips()
    {
        var id = PatientId.Create(1234567);
        Assert.True(PatientId.TryParse(id.Value, out var parsed));
        Assert.Equal(id, parsed);
    }

    [Fact]
    public void CheckDigit_Detects_SingleDigitSubstitution()
    {
        // Take a valid id and mutate one payload digit; expect parse to fail.
        var id = PatientId.Create(1234567);
        var chars = id.Value.ToCharArray();
        // Mutate the digit at position 5 (a payload digit)
        chars[5] = chars[5] == '9' ? '0' : (char)(chars[5] + 1);
        var tampered = new string(chars);
        Assert.False(PatientId.TryParse(tampered, out _));
    }

    [Fact]
    public void CheckDigit_Detects_TransposedAdjacentDigits()
    {
        // Two-digit-count transposition. Pick two adjacent DIFFERENT payload digits.
        var id = PatientId.Create(1234567); // -> NDC-1234567-C
        var chars = id.Value.ToCharArray();
        // swap 4 (index 4) and 5 (index 5) — '1' and '2'
        (chars[4], chars[5]) = (chars[5], chars[4]);
        var tampered = new string(chars);
        Assert.False(PatientId.TryParse(tampered, out _));
    }

    [Fact]
    public void Parse_Rejects_MalformedInput()
    {
        Assert.False(PatientId.TryParse("", out _));
        Assert.False(PatientId.TryParse("NDC-abcdefg-1", out _));
        Assert.False(PatientId.TryParse("XYZ-1234567-8", out _));
        Assert.False(PatientId.TryParse("NDC-12345678-9", out _));
    }

    [Fact]
    public void Create_RejectsOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PatientId.Create(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PatientId.Create(10_000_000));
    }
}
