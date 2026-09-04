using LoadRunner.Core.Scenarios;
using Xunit;

namespace LoadRunner.UnitTests.Scenarios;

public class ValueExtractorTests
{
    [Fact]
    public void ExtractJson_ReturnsFieldByDottedPath()
    {
        var json = """{ "token": "abc", "user": { "id": 7, "name": "Grace" } }""";
        Assert.Equal("abc", ValueExtractor.ExtractJson(json, "token"));
        Assert.Equal("Grace", ValueExtractor.ExtractJson(json, "user.name"));
        Assert.Equal("7", ValueExtractor.ExtractJson(json, "user.id"));
    }

    [Fact]
    public void ExtractJson_HandlesArrayIndexing()
    {
        var json = """{ "items": [ { "id": 1 }, { "id": 42 }, { "id": 99 } ] }""";
        Assert.Equal("42", ValueExtractor.ExtractJson(json, "items[1].id"));
    }

    [Fact]
    public void ExtractJson_ReturnsNullForMissingPath()
    {
        var json = """{ "a": 1 }""";
        Assert.Null(ValueExtractor.ExtractJson(json, "missing"));
    }
}
