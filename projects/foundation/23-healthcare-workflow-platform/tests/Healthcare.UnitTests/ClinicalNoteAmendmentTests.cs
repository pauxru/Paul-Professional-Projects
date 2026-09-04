using Healthcare.Domain.Common;
using Healthcare.Domain.Encounters;
using Xunit;

namespace Healthcare.UnitTests.Domain;

public class ClinicalNoteAmendmentTests
{
    private static Encounter OpenEncounter(bool coSign = false)
    {
        return Encounter.Open(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero), coSign);
    }

    [Fact]
    public void Amendment_Creates_A_NewVersion_Referencing_The_Original()
    {
        var e = OpenEncounter();
        var authorId = Guid.NewGuid();
        var initial = e.AddNote(authorId, "cough", "clear lungs", "URTI", "rest and fluids",
            DateTimeOffset.UtcNow);
        var amendment = e.AmendNote(initial.Id, authorId, "cough + fever", "clear lungs, fever 38.2",
            "URTI likely viral", "rest, fluids, paracetamol", "added fever", DateTimeOffset.UtcNow);

        Assert.NotEqual(initial.Id, amendment.Id);
        Assert.Equal(initial.RootNoteId, amendment.RootNoteId);
        Assert.Equal(2, amendment.Version);
        Assert.True(amendment.IsAmendment);
        Assert.Equal("added fever", amendment.AmendmentReason);
        Assert.Equal(2, e.Notes.Count);
        // Original is untouched.
        Assert.Equal("cough", initial.ChiefComplaint);
        Assert.False(initial.IsAmendment);
    }

    [Fact]
    public void Amendment_Requires_Reason()
    {
        var e = OpenEncounter();
        var authorId = Guid.NewGuid();
        var initial = e.AddNote(authorId, "cough", "obs", "assess", "plan", DateTimeOffset.UtcNow);
        Assert.Throws<DomainException>(() => e.AmendNote(initial.Id, authorId, "cough", "obs2", "assess2", "plan2", "", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Amendment_Fails_If_Original_Not_Found()
    {
        var e = OpenEncounter();
        Assert.Throws<DomainException>(() =>
            e.AmendNote(Guid.NewGuid(), Guid.NewGuid(), "c", "o", "a", "p", "why", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void CoSign_Required_Before_Close()
    {
        var e = OpenEncounter(coSign: true);
        var author = Guid.NewGuid();
        e.AddNote(author, "c", "o", "a", "p", DateTimeOffset.UtcNow);
        Assert.Throws<DomainException>(() => e.Close(DateTimeOffset.UtcNow));
        e.CoSign(Guid.NewGuid(), DateTimeOffset.UtcNow);
        e.Close(DateTimeOffset.UtcNow);
        Assert.NotNull(e.ClosedAtUtc);
    }

    [Fact]
    public void Cannot_CoSign_Own_Encounter()
    {
        var e = Encounter.Open(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DateTimeOffset.UtcNow, requiresCoSign: true);
        Assert.Throws<DomainException>(() => e.CoSign(GetClinicianId(e), DateTimeOffset.UtcNow));
    }

    private static Guid GetClinicianId(Encounter e) => e.ClinicianId;
}
