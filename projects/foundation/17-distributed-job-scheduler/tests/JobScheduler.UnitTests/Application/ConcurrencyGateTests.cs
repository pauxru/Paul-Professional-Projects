using JobScheduler.Application.Services;

namespace JobScheduler.UnitTests.Application;

/// <summary>Admission control combining global, per-definition, per-queue and singleton caps.</summary>
public sealed class ConcurrencyGateTests
{
    // Baseline: nothing active, generous caps, not singleton -> allowed.
    private static bool CanStart(
        int globalActive = 0, int globalMax = 10,
        int defActive = 0, int defLimit = 5,
        int queueActive = 0, int queueSlots = 0,
        bool singleton = false, bool anyActiveForDefinition = false)
        => ConcurrencyGate.CanStart(globalActive, globalMax, defActive, defLimit, queueActive, queueSlots, singleton, anyActiveForDefinition);

    [Fact]
    public void Allows_when_all_caps_have_headroom()
    {
        Assert.True(CanStart());
    }

    [Fact]
    public void Blocks_at_the_global_cap()
    {
        Assert.False(CanStart(globalActive: 10, globalMax: 10));
    }

    [Fact]
    public void Blocks_at_the_per_definition_cap()
    {
        Assert.False(CanStart(defActive: 5, defLimit: 5));
    }

    [Fact]
    public void Blocks_at_the_per_queue_slot_cap()
    {
        Assert.False(CanStart(queueActive: 2, queueSlots: 2));
    }

    [Fact]
    public void Queue_slots_of_zero_means_unlimited()
    {
        Assert.True(CanStart(queueActive: 1000, queueSlots: 0));
    }

    [Fact]
    public void Singleton_blocks_when_a_sibling_is_already_active()
    {
        Assert.False(CanStart(singleton: true, anyActiveForDefinition: true));
        Assert.True(CanStart(singleton: true, anyActiveForDefinition: false));
    }
}
