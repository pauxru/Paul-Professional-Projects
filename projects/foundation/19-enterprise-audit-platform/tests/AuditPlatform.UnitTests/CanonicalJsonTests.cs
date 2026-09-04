using System.Text;
using System.Text.Json;
using AuditPlatform.Domain.Serialization;
using Xunit;

namespace AuditPlatform.UnitTests;

public class CanonicalJsonTests
{
    [Fact]
    public void Serialize_SortsObjectKeys_Lexicographically()
    {
        var a = CanonicalJson.Serialize("{\"b\":1,\"a\":2,\"c\":3}");
        var b = CanonicalJson.Serialize("{\"c\":3,\"a\":2,\"b\":1}");
        Assert.Equal(Encoding.UTF8.GetString(a), Encoding.UTF8.GetString(b));
        Assert.Equal("{\"a\":2,\"b\":1,\"c\":3}", Encoding.UTF8.GetString(a));
    }

    [Fact]
    public void Serialize_PreservesArrayOrder()
    {
        var a = CanonicalJson.Serialize("[3,1,2]");
        Assert.Equal("[3,1,2]", Encoding.UTF8.GetString(a));
    }

    [Fact]
    public void Serialize_HasNoInsignificantWhitespace()
    {
        var a = CanonicalJson.Serialize("{ \"a\" : 1 ,   \"b\" : [ 1 , 2 , 3 ] }");
        Assert.Equal("{\"a\":1,\"b\":[1,2,3]}", Encoding.UTF8.GetString(a));
    }

    [Fact]
    public void Serialize_IntegerNumbersHaveNoFraction()
    {
        var a = CanonicalJson.Serialize("{\"n\": 5}");
        Assert.Equal("{\"n\":5}", Encoding.UTF8.GetString(a));
    }

    [Fact]
    public void Serialize_DecimalNumbersStripTrailingZeros()
    {
        // "1.500" is a decimal-shaped literal — canonical form drops trailing zeros.
        var a = CanonicalJson.Serialize("{\"n\": 1.500}");
        Assert.Equal("{\"n\":1.5}", Encoding.UTF8.GetString(a));
    }

    [Fact]
    public void Serialize_NestedObjectSortsRecursively()
    {
        var a = CanonicalJson.Serialize("{\"outer\":{\"z\":1,\"a\":2}}");
        Assert.Equal("{\"outer\":{\"a\":2,\"z\":1}}", Encoding.UTF8.GetString(a));
    }

    [Fact]
    public void Serialize_HandlesUnicodeStringsIdentically()
    {
        var direct = CanonicalJson.Serialize("{\"k\":\"café — résumé\"}");
        var alt = CanonicalJson.Serialize("{\"k\":\"caf\\u00e9 \\u2014 r\\u00e9sum\\u00e9\"}");
        Assert.Equal(Encoding.UTF8.GetString(direct), Encoding.UTF8.GetString(alt));
    }

    [Fact]
    public void Serialize_HandlesNullBooleanAndArrays()
    {
        var a = CanonicalJson.Serialize("{\"x\":null,\"y\":true,\"z\":false,\"arr\":[]}");
        Assert.Equal("{\"arr\":[],\"x\":null,\"y\":true,\"z\":false}", Encoding.UTF8.GetString(a));
    }

    [Fact]
    public void Serialize_IsIdempotent()
    {
        var once = CanonicalJson.Serialize("{\"b\":1,\"a\":2}");
        var twice = CanonicalJson.Serialize(Encoding.UTF8.GetString(once));
        Assert.Equal(Encoding.UTF8.GetString(once), Encoding.UTF8.GetString(twice));
    }
}
