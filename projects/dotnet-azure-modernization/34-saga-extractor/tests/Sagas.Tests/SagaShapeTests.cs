using Sagas.Core;

namespace Sagas.Tests;

/// <summary>
/// Shape rules. These are facts about a design that can be decided by reading
/// it, with no exploration -- which is exactly why they are reported through a
/// different channel from the reachability violations. A well-shaped saga is not
/// a correct one, and merging the two would let a reader believe otherwise.
/// </summary>
public class SagaShapeTests
{
    private static readonly Schema S = new("a", "b");

    private static Step Comp(string name) => new()
    {
        Name = name,
        Service = "svc",
        Kind = StepKind.Compensatable,
        Forward = st => st.Add("a", 1),
        Compensate = st => st.Add("a", -1)
    };

    private static Step Pivot(string name) => new()
    {
        Name = name,
        Service = "svc",
        Kind = StepKind.Pivot,
        Forward = st => st.Add("b", 1)
    };

    private static Step Retriable(string name) => new()
    {
        Name = name,
        Service = "svc",
        Kind = StepKind.Retriable,
        Forward = st => st.Add("b", 1),
        CanBeRejected = false
    };

    private static Saga Build(params Step[] steps) => new("t", S, S.State(), steps);

    [Fact]
    public void CanonicalShapeHasNoViolations()
    {
        Assert.Empty(Build(Comp("c1"), Comp("c2"), Pivot("p"), Retriable("r")).ShapeViolations());
    }

    [Fact]
    public void CompensatableAfterThePivotIsAViolation()
    {
        var v = Build(Pivot("p"), Comp("c")).ShapeViolations();
        Assert.Contains(v, x => x.Contains("c", StringComparison.Ordinal));
        Assert.NotEmpty(v);
    }

    [Fact]
    public void RetriableBeforeThePivotIsAViolation()
    {
        Assert.NotEmpty(Build(Retriable("r"), Pivot("p")).ShapeViolations());
    }

    [Fact]
    public void TwoPivotsIsAViolation()
    {
        var v = Build(Comp("c"), Pivot("p1"), Pivot("p2")).ShapeViolations();
        Assert.NotEmpty(v);
    }

    [Fact]
    public void ASagaWithNoPivotIsPerfectlyWellFormed()
    {
        // Worth stating explicitly because it looks like an omission. A saga
        // whose steps are all compensatable has no point of no return, which
        // means it can always be rolled back completely. That is the *best* case,
        // not a missing piece -- the pivot exists to mark the moment that
        // property is lost, and a saga that never loses it needs no marker.
        Assert.Empty(Build(Comp("c1"), Comp("c2")).ShapeViolations());
        Assert.Equal(-1, Build(Comp("c1"), Comp("c2")).PivotIndex);
    }

    [Fact]
    public void CompensatableStepWithoutACompensationIsNotEvenConstructible()
    {
        // Rejected in the constructor rather than reported by ShapeViolations,
        // because it is not a badly-ordered saga -- it is not a saga. The
        // distinction matters: ShapeViolations describes designs that exist and
        // are wrong, and this one cannot be made to exist.
        var broken = Comp("c") with { Compensate = null };
        var ex = Assert.Throws<ArgumentException>(() => Build(broken, Pivot("p")));
        Assert.Contains("no compensation", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APivotSupplyingACompensationIsRejected()
    {
        var confused = Pivot("p") with { Compensate = st => st.Add("b", -1) };
        Assert.Throws<ArgumentException>(() => Build(Comp("c"), confused));
    }

    [Fact]
    public void ASagaLongerThanThePackedCounterIsRejectedRatherThanSilentlyWrong()
    {
        // The landing counter packs two bits per step into an int. Exceeding that
        // would corrupt neighbouring steps' counters and produce a state space
        // that is wrong in a way no property could detect, so the constructor
        // refuses instead.
        var many = Enumerable.Range(0, 20).Select(i => Comp($"c{i}")).ToArray();
        var ex = Assert.Throws<ArgumentException>(() => Build(many));
        Assert.Contains("packed landing counter", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PivotIndexIsTheIndexOfThePivot()
    {
        Assert.Equal(2, Build(Comp("a"), Comp("b"), Pivot("p"), Retriable("r")).PivotIndex);
    }

    [Fact]
    public void ASagaMustHaveAtLeastOneStep()
    {
        Assert.Throws<ArgumentException>(() => new Saga("empty", S, S.State(), []));
    }

    [Fact]
    public void EveryCatalogueVersionFromV2OnwardsIsWellShaped()
    {
        foreach (var (label, _, build) in Catalogue.Progression.Where(p => p.Label != "v1"))
        {
            Assert.True(
                build().ShapeViolations().Count == 0,
                $"{label} should be well-shaped but reported: "
                + string.Join("; ", build().ShapeViolations()));
        }
    }

    [Fact]
    public void V1IsDeliberatelyMisshapen()
    {
        // The whole progression starts from a saga that is wrong on inspection.
        // If this ever passes the shape check, the narrative in results.md has
        // quietly stopped being true.
        Assert.NotEmpty(Catalogue.V1_AsWritten().ShapeViolations());
    }

    [Fact]
    public void InvariantsAreRecordedInDeclarationOrder()
    {
        var saga = Build(Comp("c"), Pivot("p"))
            .Invariant("first", _ => true)
            .Invariant("second", _ => true);
        Assert.Equal(["first", "second"], saga.Invariants.Select(i => i.Name));
    }

    [Fact]
    public void AllowResidueIsAdditive()
    {
        var saga = Build(Comp("c"), Pivot("p")).AllowResidue("a").AllowResidue("b");
        Assert.Contains("a", saga.AcceptableResidue);
        Assert.Contains("b", saga.AcceptableResidue);
    }
}
