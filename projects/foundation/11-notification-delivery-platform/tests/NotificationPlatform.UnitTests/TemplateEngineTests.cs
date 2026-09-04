namespace NotificationPlatform.UnitTests;

using System.Text.Json;
using NotificationPlatform.Application.Templates;
using Xunit;

public sealed class TemplateEngineTests
{
    private static JsonElement Json(string s)
    {
        var doc = JsonDocument.Parse(s);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void SubstitutesSimpleToken()
    {
        var engine = new TemplateEngine();
        var res = engine.Render("Hi {{user.firstName}}!", Json("{\"user\":{\"firstName\":\"Alice\"}}"), strict: true);
        Assert.Equal("Hi Alice!", res.Body);
        Assert.Empty(res.UnknownTokens);
    }

    [Fact]
    public void HtmlEscapesByDefault()
    {
        var engine = new TemplateEngine();
        var res = engine.Render("<p>{{msg}}</p>", Json("{\"msg\":\"<script>alert('xss')</script>\"}"), strict: true);
        Assert.Contains("&lt;script&gt;", res.Body);
        Assert.DoesNotContain("<script>", res.Body);
    }

    [Fact]
    public void RawMarkerBypassesEscape()
    {
        var engine = new TemplateEngine();
        var res = engine.Render("<p>{{raw:msg}}</p>", Json("{\"msg\":\"<b>ok</b>\"}"), strict: true);
        Assert.Equal("<p><b>ok</b></p>", res.Body);
    }

    [Fact]
    public void ConditionalIncludesWhenTruthy()
    {
        var engine = new TemplateEngine();
        var res = engine.Render("A{{#if flag}}B{{/if}}C", Json("{\"flag\":true}"), strict: true);
        Assert.Equal("ABC", res.Body);
    }

    [Fact]
    public void ConditionalOmittedWhenFalse()
    {
        var engine = new TemplateEngine();
        var res = engine.Render("A{{#if flag}}B{{/if}}C", Json("{\"flag\":false}"), strict: true);
        Assert.Equal("AC", res.Body);
    }

    [Fact]
    public void EachLoopIteratesArray()
    {
        var engine = new TemplateEngine();
        var res = engine.Render("{{#each xs}}[{{.}}]{{/each}}", Json("{\"xs\":[\"a\",\"b\",\"c\"]}"), strict: true);
        Assert.Equal("[a][b][c]", res.Body);
    }

    [Fact]
    public void StrictModeThrowsOnUnknownToken()
    {
        var engine = new TemplateEngine();
        Assert.Throws<TemplateRenderException>(() => engine.Render("Hi {{user.firstName}}", Json("{}"), strict: true));
    }

    [Fact]
    public void NonStrictRecordsUnknownTokens()
    {
        var engine = new TemplateEngine();
        var res = engine.Render("Hi {{user.firstName}}", Json("{}"), strict: false);
        Assert.Equal("Hi ", res.Body);
        Assert.Contains("user.firstName", res.UnknownTokens);
    }

    [Fact]
    public void MissingDataInLoopIsHandled()
    {
        var engine = new TemplateEngine();
        var res = engine.Render("{{#each xs}}{{.}}{{/each}}!", Json("{\"xs\":[]}"), strict: true);
        Assert.Equal("!", res.Body);
    }
}
