using AgentPlatform.Domain.Budgets;

namespace AgentPlatform.UnitTests.Budgets;

/// <summary>Proves the oscillation detector halts a run that repeats an identical tool call.</summary>
public sealed class LoopDetectorTests
{
    [Fact]
    public void Trips_on_the_threshold_repetition()
    {
        var detector = new LoopDetector(threshold: 3);
        Assert.False(detector.RecordAndCheck("search", "{\"q\":\"a\"}"));
        Assert.False(detector.RecordAndCheck("search", "{\"q\":\"a\"}"));
        Assert.True(detector.RecordAndCheck("search", "{\"q\":\"a\"}"));
    }

    [Fact]
    public void Different_arguments_do_not_trip()
    {
        var detector = new LoopDetector(threshold: 3);
        Assert.False(detector.RecordAndCheck("search", "{\"q\":\"a\"}"));
        Assert.False(detector.RecordAndCheck("search", "{\"q\":\"b\"}"));
        Assert.False(detector.RecordAndCheck("search", "{\"q\":\"c\"}"));
    }

    [Fact]
    public void Different_tools_do_not_trip()
    {
        var detector = new LoopDetector(threshold: 2);
        Assert.False(detector.RecordAndCheck("a", "{}"));
        Assert.False(detector.RecordAndCheck("b", "{}"));
    }

    [Fact]
    public void SeenCount_tracks_repetitions()
    {
        var detector = new LoopDetector(threshold: 5);
        detector.RecordAndCheck("t", "{}");
        detector.RecordAndCheck("t", "{}");
        Assert.Equal(2, detector.SeenCount("t", "{}"));
    }
}
