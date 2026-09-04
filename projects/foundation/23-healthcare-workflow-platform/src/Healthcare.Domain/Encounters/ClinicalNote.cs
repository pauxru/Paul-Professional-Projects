using Healthcare.Domain.Common;

namespace Healthcare.Domain.Encounters;

/// <summary>
/// Clinical note. Notes are append-only; edits are represented as amendments (new versions
/// referencing the same root note id). Persistence is instructed to reject UPDATE and DELETE
/// on the notes table for this reason.
/// </summary>
public sealed class ClinicalNote
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid EncounterId { get; private set; }
    /// <summary>Points at the id of the first (initial) note of the version chain. For version 1 this equals Id.</summary>
    public Guid RootNoteId { get; private set; }
    public int Version { get; private set; }
    public bool IsAmendment { get; private set; }
    public string? AmendmentReason { get; private set; }
    public Guid AuthorId { get; private set; }
    public string ChiefComplaint { get; private set; } = string.Empty;
    public string Observations { get; private set; } = string.Empty;
    public string Assessment { get; private set; } = string.Empty;
    public string Plan { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private ClinicalNote() { }

    internal static ClinicalNote CreateInitial(Guid encounterId, Guid authorId, string chiefComplaint,
        string observations, string assessment, string plan, DateTimeOffset at)
    {
        Validate(chiefComplaint, assessment);
        var id = Guid.NewGuid();
        return new ClinicalNote
        {
            Id = id,
            EncounterId = encounterId,
            RootNoteId = id,
            Version = 1,
            IsAmendment = false,
            AuthorId = authorId,
            ChiefComplaint = chiefComplaint.Trim(),
            Observations = observations.Trim(),
            Assessment = assessment.Trim(),
            Plan = plan.Trim(),
            CreatedAtUtc = at
        };
    }

    internal static ClinicalNote CreateAmendment(Guid encounterId, Guid rootNoteId, int version, Guid authorId,
        string chiefComplaint, string observations, string assessment, string plan, string reason,
        DateTimeOffset at)
    {
        Validate(chiefComplaint, assessment);
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("note.amend.reason_required", "Amendment reason is required.");
        return new ClinicalNote
        {
            Id = Guid.NewGuid(),
            EncounterId = encounterId,
            RootNoteId = rootNoteId,
            Version = version,
            IsAmendment = true,
            AmendmentReason = reason.Trim(),
            AuthorId = authorId,
            ChiefComplaint = chiefComplaint.Trim(),
            Observations = observations.Trim(),
            Assessment = assessment.Trim(),
            Plan = plan.Trim(),
            CreatedAtUtc = at
        };
    }

    private static void Validate(string chiefComplaint, string assessment)
    {
        if (string.IsNullOrWhiteSpace(chiefComplaint))
            throw new DomainException("note.chief_complaint.required", "Chief complaint is required.");
        if (string.IsNullOrWhiteSpace(assessment))
            throw new DomainException("note.assessment.required", "Assessment is required.");
    }
}
