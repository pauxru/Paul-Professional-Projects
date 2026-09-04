using Healthcare.Domain.Common;
using Healthcare.Domain.Encounters;
using Xunit;

namespace Healthcare.UnitTests.Domain;

public class VitalReadingTests
{
    [Fact]
    public void Systolic_Bp_In_Plausible_Range_Is_Accepted()
    {
        var v = VitalReading.Create(Guid.NewGuid(), "systolic_bp", 122, "mmHg", DateTimeOffset.UtcNow);
        Assert.Equal("mmhg".ToLowerInvariant(), v.Unit.ToLowerInvariant());
        Assert.Equal(122m, v.Value);
    }

    [Fact]
    public void Temperature_In_Bad_Unit_Is_Rejected()
    {
        Assert.Throws<DomainException>(() =>
            VitalReading.Create(Guid.NewGuid(), "temperature", 98.6m, "F", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Implausibly_Low_Systolic_Rejected()
    {
        Assert.Throws<DomainException>(() =>
            VitalReading.Create(Guid.NewGuid(), "systolic_bp", 20m, "mmHg", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Unknown_Kind_Rejected()
    {
        Assert.Throws<DomainException>(() =>
            VitalReading.Create(Guid.NewGuid(), "chakra_alignment", 10m, "au", DateTimeOffset.UtcNow));
    }
}
