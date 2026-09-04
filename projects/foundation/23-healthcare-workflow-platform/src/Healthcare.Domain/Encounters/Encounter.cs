using Healthcare.Domain.Common;

namespace Healthcare.Domain.Encounters;

public sealed class Encounter : Entity
{
    public Guid AppointmentId { get; private set; }
    public Guid PatientId { get; private set; }
    public Guid ClinicianId { get; private set; }
    public DateTimeOffset OpenedAtUtc { get; private set; }
    public DateTimeOffset? ClosedAtUtc { get; private set; }
    public bool RequiresCoSign { get; private set; }
    public Guid? CoSignedByClinicianId { get; private set; }
    public DateTimeOffset? CoSignedAtUtc { get; private set; }

    private readonly List<ClinicalNote> _notes = new();
    public IReadOnlyCollection<ClinicalNote> Notes => _notes.AsReadOnly();

    private readonly List<VitalReading> _vitals = new();
    public IReadOnlyCollection<VitalReading> Vitals => _vitals.AsReadOnly();

    private Encounter() { }

    public static Encounter Open(Guid appointmentId, Guid patientId, Guid clinicianId,
        DateTimeOffset openedAt, bool requiresCoSign = false) =>
        new()
        {
            AppointmentId = appointmentId,
            PatientId = patientId,
            ClinicianId = clinicianId,
            OpenedAtUtc = openedAt,
            RequiresCoSign = requiresCoSign
        };

    public ClinicalNote AddNote(Guid authorId, string chiefComplaint, string observations, string assessment,
        string plan, DateTimeOffset at)
    {
        if (ClosedAtUtc is not null)
            throw new DomainException("encounter.closed", "Cannot add note to a closed encounter.");
        var note = ClinicalNote.CreateInitial(this.Id, authorId, chiefComplaint, observations, assessment, plan, at);
        _notes.Add(note);
        return note;
    }

    public ClinicalNote AmendNote(Guid originalNoteId, Guid authorId, string chiefComplaint,
        string observations, string assessment, string plan, string amendmentReason, DateTimeOffset at)
    {
        if (ClosedAtUtc is not null)
            throw new DomainException("encounter.closed", "Cannot amend a closed encounter.");
        var original = _notes.FirstOrDefault(n => n.Id == originalNoteId)
            ?? throw new DomainException("note.not_found", "Original note not found on encounter.");
        // The amendment is a new version referencing the original (never mutates original).
        var version = _notes.Count(n => n.RootNoteId == original.RootNoteId) + 1;
        var amendment = ClinicalNote.CreateAmendment(this.Id, original.RootNoteId, version, authorId,
            chiefComplaint, observations, assessment, plan, amendmentReason, at);
        _notes.Add(amendment);
        return amendment;
    }

    public void AddVital(string kind, decimal value, string unit, DateTimeOffset at)
    {
        if (ClosedAtUtc is not null)
            throw new DomainException("encounter.closed", "Cannot add vital to a closed encounter.");
        var v = VitalReading.Create(this.Id, kind, value, unit, at);
        _vitals.Add(v);
    }

    public void CoSign(Guid supervisorClinicianId, DateTimeOffset at)
    {
        if (!RequiresCoSign) throw new DomainException("encounter.cosign.not_required", "Encounter does not require co-sign.");
        if (supervisorClinicianId == ClinicianId) throw new DomainException("encounter.cosign.self", "Cannot co-sign own encounter.");
        CoSignedByClinicianId = supervisorClinicianId;
        CoSignedAtUtc = at;
    }

    public void Close(DateTimeOffset at)
    {
        if (RequiresCoSign && CoSignedAtUtc is null)
            throw new DomainException("encounter.close.cosign_required", "Encounter requires co-sign before closure.");
        ClosedAtUtc = at;
    }
}
