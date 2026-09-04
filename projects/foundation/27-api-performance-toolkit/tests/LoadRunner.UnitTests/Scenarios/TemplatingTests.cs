using LoadRunner.Core.Scenarios;
using Xunit;

namespace LoadRunner.UnitTests.Scenarios;

public class TemplatingTests
{
    [Fact]
    public void Render_SubstitutesKnownVariables()
    {
        var vars = new Dictionary<string, string> { ["user"] = "alice", ["id"] = "42" };
        var result = Templating.Render("/users/{{id}}?name={{user}}", vars);
        Assert.Equal("/users/42?name=alice", result);
    }

    [Fact]
    public void Render_LeavesUnknownVariablesUntouched()
    {
        var vars = new Dictionary<string, string> { ["user"] = "alice" };
        var result = Templating.Render("hello {{missing}} {{user}}", vars);
        Assert.Equal("hello {{missing}} alice", result);
    }

    [Fact]
    public void Render_HandlesEmptyTemplate()
    {
        var vars = new Dictionary<string, string> { ["x"] = "1" };
        Assert.Equal(string.Empty, Templating.Render(string.Empty, vars));
    }
}
