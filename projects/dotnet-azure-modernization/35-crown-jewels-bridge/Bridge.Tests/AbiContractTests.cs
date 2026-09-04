using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Bridge.Core;

// Every test in this assembly touches process-wide native state: one loaded copy of
// pricing.dll, one set of engine create/destroy counters, one last-error slot per
// engine. The leak tests in particular read a global counter and assert on the delta,
// which is only meaningful if nothing else is creating engines at the same time.
//
// Running these in parallel would not usually fail. It would fail occasionally, on a
// machine with more cores than mine, in a way that looks like a flaky test rather than
// like the measurement error it actually is. That is the worst possible failure mode,
// so parallelism is off.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Bridge.Tests;

/// <summary>
/// Checks the things the C# compiler cannot check: that the managed structs are laid
/// out the way the C header says, and that the DLL on disk speaks the ABI this
/// assembly was written against.
/// </summary>
/// <remarks>
/// These are the highest-value tests in the project and they look like the most
/// trivial. If <c>PricingOption</c> and <c>pj_option</c> disagree about a single
/// offset, nothing crashes. The fields are all doubles; every offset is readable; the
/// engine simply prices an option whose volatility is actually its dividend yield and
/// returns a number that is finite, plausible, and wrong. There is no exception to
/// catch and no log line to read. The only place that bug can be caught is here.
/// </remarks>
public class AbiContractTests
{
    [Fact]
    public void Verify_passes_against_the_loaded_dll()
    {
        // Throws with a specific message if any offset or the ABI version disagrees.
        AbiContract.Verify();
    }

    [Fact]
    public void PricingOption_is_exactly_56_bytes()
    {
        // Six doubles and two 32-bit ints, no padding. If the C compiler ever adds
        // tail padding and this does not, the batch stride is wrong and every element
        // after the first reads from inside its predecessor.
        Assert.Equal(56, Unsafe.SizeOf<PricingOption>());
    }

    [Fact]
    public void Greeks_is_exactly_48_bytes()
    {
        Assert.Equal(48, Unsafe.SizeOf<Greeks>());
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(8, 2.0)]
    [InlineData(16, 3.0)]
    [InlineData(24, 4.0)]
    [InlineData(32, 5.0)]
    [InlineData(40, 6.0)]
    public void Each_double_field_sits_at_the_offset_the_header_promises(int offset, double expected)
    {
        var probe = new PricingOption(1, 2, 3, 4, 5, 6, OptionKind.Put);
        var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<PricingOption>(in probe));
        Assert.Equal(expected, BitConverter.ToDouble(bytes.Slice(offset, 8)));
    }

    [Fact]
    public void Kind_is_a_32_bit_int_at_offset_48()
    {
        var probe = new PricingOption(1, 2, 3, 4, 5, 6, OptionKind.Put);
        var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<PricingOption>(in probe));
        Assert.Equal(1, BitConverter.ToInt32(bytes.Slice(48, 4)));
    }

    [Fact]
    public void Reserved_is_zero_so_the_struct_has_no_uninitialised_tail()
    {
        // The reserved word is not decoration. Without it the struct would be 52 bytes
        // of payload in a 56-byte footprint, and the four padding bytes would be
        // whatever was on the stack -- which makes a byte-for-byte comparison of two
        // logically equal options fail at random.
        var probe = new PricingOption(1, 2, 3, 4, 5, 6, OptionKind.Call);
        var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<PricingOption>(in probe));
        Assert.Equal(0, BitConverter.ToInt32(bytes.Slice(52, 4)));
    }

    [Fact]
    public void Two_equal_options_are_byte_identical()
    {
        var a = new PricingOption(42, 40, 0.1, 0.0, 0.2, 0.5, OptionKind.Call);
        var b = new PricingOption(42, 40, 0.1, 0.0, 0.2, 0.5, OptionKind.Call);
        var ba = MemoryMarshal.AsBytes(new ReadOnlySpan<PricingOption>(in a)).ToArray();
        var bb = MemoryMarshal.AsBytes(new ReadOnlySpan<PricingOption>(in b)).ToArray();
        Assert.Equal(ba, bb);
    }

    [Fact]
    public void OptionKind_call_is_zero_and_put_is_one()
    {
        // The C header hard-codes these. A renumbering on either side prices calls as
        // puts, which is a bug worth roughly the whole book.
        Assert.Equal(0, (int)OptionKind.Call);
        Assert.Equal(1, (int)OptionKind.Put);
    }

    [Theory]
    [InlineData(PricingStatus.Ok, 0)]
    [InlineData(PricingStatus.NullArgument, 1)]
    [InlineData(PricingStatus.BadArgument, 2)]
    [InlineData(PricingStatus.Capacity, 3)]
    [InlineData(PricingStatus.Overflow, 4)]
    [InlineData(PricingStatus.Cancelled, 5)]
    [InlineData(PricingStatus.NotConverged, 6)]
    [InlineData(PricingStatus.Internal, 7)]
    public void Status_codes_match_the_c_header(PricingStatus status, int value)
    {
        Assert.Equal(value, (int)status);
    }

    [Fact]
    public void Native_directory_is_found_and_holds_all_three_variants()
    {
        var dir = AbiContract.NativeDirectory;
        Assert.True(File.Exists(Path.Combine(dir, "pricing.dll")));
        Assert.True(File.Exists(Path.Combine(dir, "pricing_legacy.dll")));
        Assert.True(File.Exists(Path.Combine(dir, "pricing_fast.dll")));
    }

    [Fact]
    public void Resolver_declines_libraries_it_does_not_own()
    {
        // A resolver that returns a handle for anything it is asked about will happily
        // load an attacker's DLL from the working directory. This one answers only for
        // the three names it knows and returns zero -- "not mine, use the normal
        // rules" -- for everything else.
        var handle = AbiContract.Resolve("kernel32", typeof(AbiContract).Assembly, null);
        Assert.Equal(nint.Zero, handle);
    }
}
