using Contoso.Payments.Application.Common;

namespace Contoso.Payments.UnitTests.Application;

public class HashingTests
{
    [Fact]
    public void Sha256_is_deterministic()
    {
        var a = Hashing.Sha256Hex("hello");
        var b = Hashing.Sha256Hex("hello");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Sha256_of_different_input_differs()
    {
        var a = Hashing.Sha256Hex("hello");
        var b = Hashing.Sha256Hex("Hello");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Sha256_of_empty_string_matches_known_value()
    {
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            Hashing.Sha256Hex(""));
    }
}
