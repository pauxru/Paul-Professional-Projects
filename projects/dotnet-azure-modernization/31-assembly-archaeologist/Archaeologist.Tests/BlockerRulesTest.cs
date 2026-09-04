using Archaeologist.Core;

namespace Archaeologist.Tests;

/// <summary>
/// The rule table is the only part of this system that encodes outside knowledge: which
/// Framework APIs survive the move to modern .NET and which do not. It was written from
/// the .NET porting documentation, not from the corpus, so these tests check its shape and
/// its precision rather than its agreement with anything the corpus happens to contain.
/// </summary>
public class BlockerRulesTest
{
    [Fact]
    public void TheTableIsNotEmptyAndCoversAllThreeSeverities()
    {
        Assert.NotEmpty(BlockerRules.All);
        foreach (var s in new[] { Severity.Rewrite, Severity.PlatformNotSupported, Severity.NoEquivalent })
            Assert.Contains(BlockerRules.All, r => r.Severity == s);
    }

    [Fact]
    public void NoRuleIsMarkedSeverityNoneBecauseThatWouldBeARuleThatSaysNothing()
    {
        Assert.DoesNotContain(BlockerRules.All, r => r.Severity == Severity.None);
    }

    [Fact]
    public void EveryRuleExplainsItselfAndSaysWhatToDo()
    {
        Assert.All(BlockerRules.All, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Reason));
            Assert.False(string.IsNullOrWhiteSpace(r.Guidance));
            Assert.NotEqual(r.Reason, r.Guidance);
        });
    }

    [Fact]
    public void NoTwoRulesShareAKey()
    {
        var keys = BlockerRules.All.Select(r => r.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void RuleKeysDistinguishTypeWideFromMemberSpecific()
    {
        Assert.Contains(BlockerRules.All, r => !r.Key.Contains("::"));
        Assert.Contains(BlockerRules.All, r => r.Key.Contains("::"));
    }

    // ---------- Matching ----------

    [Fact]
    public void AnUnknownTypeMatchesNothing()
    {
        Assert.Null(BlockerRules.Match("My.Own.Service", "Run"));
    }

    [Fact]
    public void AKnownTypeWithAnUnknownMemberStillMatchesTheTypeWideRule()
    {
        var rule = BlockerRules.All.First(r => r.MemberName is null);
        Assert.NotNull(BlockerRules.Match($"{rule.Namespace}.{rule.TypeName}", "SomeMemberNobodyWroteARuleFor"));
    }

    [Fact]
    public void AMemberSpecificRuleBeatsATypeWideOneOnTheSameType()
    {
        // Bitmap is a "Rewrite": swap in an imaging library and move on. But a caller that
        // asks for the Win32 HBITMAP is not using an imaging library, it is using Windows,
        // and no swap helps. Same type, different answer, and only the member says which.
        var wide = BlockerRules.Match("System.Drawing.Bitmap", "Save");
        var specific = BlockerRules.Match("System.Drawing.Bitmap", "GetHbitmap");

        Assert.NotNull(wide);
        Assert.NotNull(specific);
        Assert.Equal(Severity.Rewrite, wide.Severity);
        Assert.Equal(Severity.NoEquivalent, specific.Severity);
    }

    [Fact]
    public void AppDomainCreateDomainIsABlockerButReadingCurrentDomainIsNot()
    {
        // The precision that makes the whole exercise defensible. Counting references to
        // System.AppDomain would flag every assembly that logs its own base directory.
        var blocked = BlockerRules.Match("System.AppDomain", "CreateDomain");
        Assert.NotNull(blocked);
        Assert.Equal(Severity.PlatformNotSupported, blocked.Severity);
        Assert.Null(BlockerRules.Match("System.AppDomain", "get_CurrentDomain"));
        Assert.Null(BlockerRules.Match("System.AppDomain", "get_BaseDirectory"));
    }

    [Fact]
    public void APlatformNotSupportedBlockerIsWorseThanARewriteBecauseTheCompilerStaysSilent()
    {
        // Thread.Abort and AppDomain.CreateDomain still compile on modern .NET. They throw
        // at runtime, on the unhappy path, in production.
        foreach (var name in new[] { "Abort" })
            Assert.Equal(Severity.PlatformNotSupported,
                BlockerRules.Match("System.Threading.Thread", name)!.Severity);
        Assert.Null(BlockerRules.Match("System.Threading.Thread", "Start"));
    }

    [Fact]
    public void BinaryFormatterIsTheWorstKindOfBlockerAndLivesInMscorlib()
    {
        // The finding that breaks manifest scanning: this needs no assembly reference at
        // all beyond the one every single assembly already has.
        var r = BlockerRules.Match("System.Runtime.Serialization.Formatters.Binary.BinaryFormatter", "Serialize");
        Assert.NotNull(r);
        Assert.Equal(Severity.NoEquivalent, r.Severity);
        Assert.Equal("System.Runtime.Serialization.Formatters.Binary", r.Namespace);
    }

    [Fact]
    public void MatchingIsCaseSensitiveBecauseIlIdentifiersAre()
    {
        Assert.Null(BlockerRules.Match("system.appdomain", "CreateDomain"));
    }

    [Fact]
    public void SeverityOrderingReflectsHowHardTheFixIs()
    {
        Assert.True(Severity.None < Severity.Rewrite);
        Assert.True(Severity.Rewrite < Severity.PlatformNotSupported);
        Assert.True(Severity.PlatformNotSupported < Severity.NoEquivalent);
    }

    [Fact]
    public void TheTableSpansMoreThanOneFrameworkAssemblyWorthOfApiSurface()
    {
        var namespaces = BlockerRules.All.Select(r => r.Namespace).Distinct(StringComparer.Ordinal).Count();
        Assert.True(namespaces >= 6, $"only {namespaces} namespaces covered");
    }

    [Fact]
    public void RuleKeysRoundTripThroughMatch()
    {
        foreach (var r in BlockerRules.All)
        {
            var matched = BlockerRules.Match($"{r.Namespace}.{r.TypeName}", r.MemberName ?? "AnythingAtAll");
            Assert.NotNull(matched);
            if (r.MemberName is not null) Assert.Equal(r.Key, matched.Key);
        }
    }
}
