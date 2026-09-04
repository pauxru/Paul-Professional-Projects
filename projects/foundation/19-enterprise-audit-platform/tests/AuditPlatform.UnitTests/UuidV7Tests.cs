using AuditPlatform.Domain.Ids;
using Xunit;

namespace AuditPlatform.UnitTests;

public class UuidV7Tests
{
    [Fact]
    public void NewGuid_SortsByTimestamp()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var a = UuidV7.NewGuid(t0);
        var b = UuidV7.NewGuid(t0.AddMilliseconds(1));
        var c = UuidV7.NewGuid(t0.AddSeconds(1));
        var list = new List<Guid> { c, a, b };
        list.Sort();
        Assert.Equal(a, list[0]);
        Assert.Equal(b, list[1]);
        Assert.Equal(c, list[2]);
    }

    [Fact]
    public void ExtractUnixMs_ReturnsOriginalTimestamp()
    {
        var t = new DateTimeOffset(2026, 6, 15, 12, 34, 56, TimeSpan.Zero);
        var id = UuidV7.NewGuid(t);
        Assert.Equal(t.ToUnixTimeMilliseconds(), UuidV7.ExtractUnixMs(id));
    }

    [Fact]
    public void NewGuid_SetsVersionAndVariantBits()
    {
        var id = UuidV7.NewGuid(DateTimeOffset.UtcNow);
        var bytes = id.ToByteArray();
        // Convert from .NET mixed-endian to RFC layout.
        Array.Reverse(bytes, 0, 4);
        Array.Reverse(bytes, 4, 2);
        Array.Reverse(bytes, 6, 2);
        Assert.Equal(0x70, bytes[6] & 0xF0);
        Assert.Equal(0x80, bytes[8] & 0xC0);
    }
}
