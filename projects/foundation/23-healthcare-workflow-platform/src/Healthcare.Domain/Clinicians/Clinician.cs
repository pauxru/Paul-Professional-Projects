using Healthcare.Domain.Common;

namespace Healthcare.Domain.Clinicians;

public sealed class Clinician : Entity
{
    public string GivenName { get; private set; } = string.Empty;
    public string FamilyName { get; private set; } = string.Empty;
    public string Speciality { get; private set; } = string.Empty;
    public string QualificationsCsv { get; private set; } = string.Empty;
    public int MaxPatientsPerDay { get; private set; }
    public int MinBreakBetweenMinutes { get; private set; }

    private readonly List<ClinicianFacility> _sites = new();
    public IReadOnlyCollection<ClinicianFacility> Sites => _sites.AsReadOnly();

    private readonly List<WorkingPattern> _workingPatterns = new();
    public IReadOnlyCollection<WorkingPattern> WorkingPatterns => _workingPatterns.AsReadOnly();

    private readonly List<LeavePeriod> _leaves = new();
    public IReadOnlyCollection<LeavePeriod> Leaves => _leaves.AsReadOnly();

    private Clinician() { }

    public static Clinician Create(string given, string family, string speciality,
        IEnumerable<string> qualifications, int maxPatientsPerDay = 40, int minBreakBetweenMinutes = 5)
    {
        if (string.IsNullOrWhiteSpace(given)) throw new DomainException("clinician.given_name.required", "Given name required.");
        if (string.IsNullOrWhiteSpace(family)) throw new DomainException("clinician.family_name.required", "Family name required.");
        if (string.IsNullOrWhiteSpace(speciality)) throw new DomainException("clinician.speciality.required", "Speciality required.");
        if (maxPatientsPerDay <= 0) throw new DomainException("clinician.max_patients.invalid", "Max patients/day must be > 0.");
        if (minBreakBetweenMinutes < 0) throw new DomainException("clinician.break.invalid", "Break must be >= 0.");
        return new Clinician
        {
            GivenName = given.Trim(),
            FamilyName = family.Trim(),
            Speciality = speciality.Trim(),
            QualificationsCsv = string.Join(",", qualifications.Select(q => q.Trim())),
            MaxPatientsPerDay = maxPatientsPerDay,
            MinBreakBetweenMinutes = minBreakBetweenMinutes
        };
    }

    public void AssignToFacility(Guid facilityId)
    {
        if (!_sites.Any(s => s.FacilityId == facilityId))
            _sites.Add(new ClinicianFacility(this.Id, facilityId));
    }

    public void SetWorkingPattern(Guid facilityId, DayOfWeek day, TimeOnly start, TimeOnly end)
    {
        if (end <= start) throw new DomainException("clinician.pattern.invalid", "End must be after start.");
        var existing = _workingPatterns.FirstOrDefault(p => p.FacilityId == facilityId && p.Day == day);
        if (existing is null)
        {
            _workingPatterns.Add(new WorkingPattern(this.Id, facilityId, day, start, end));
        }
        else
        {
            existing.Start = start;
            existing.End = end;
        }
    }

    public void AddLeave(DateOnly startDate, DateOnly endDate, string reason)
    {
        if (endDate < startDate) throw new DomainException("clinician.leave.invalid", "End must be >= start.");
        _leaves.Add(new LeavePeriod(this.Id, startDate, endDate, reason));
    }

    public bool WorksAt(Guid facilityId) => _sites.Any(s => s.FacilityId == facilityId);

    public bool IsOnLeave(DateOnly date) =>
        _leaves.Any(l => l.StartDate <= date && date <= l.EndDate);

    public WorkingPattern? GetPattern(Guid facilityId, DayOfWeek day) =>
        _workingPatterns.FirstOrDefault(p => p.FacilityId == facilityId && p.Day == day);
}

public sealed class ClinicianFacility
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid ClinicianId { get; private set; }
    public Guid FacilityId { get; private set; }
    private ClinicianFacility() { }
    internal ClinicianFacility(Guid clinicianId, Guid facilityId)
    {
        ClinicianId = clinicianId;
        FacilityId = facilityId;
    }
}

public sealed class WorkingPattern
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid ClinicianId { get; private set; }
    public Guid FacilityId { get; private set; }
    public DayOfWeek Day { get; private set; }
    public TimeOnly Start { get; internal set; }
    public TimeOnly End { get; internal set; }
    private WorkingPattern() { }
    internal WorkingPattern(Guid clinicianId, Guid facilityId, DayOfWeek day, TimeOnly start, TimeOnly end)
    {
        ClinicianId = clinicianId;
        FacilityId = facilityId;
        Day = day;
        Start = start;
        End = end;
    }
}

public sealed class LeavePeriod
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid ClinicianId { get; private set; }
    public DateOnly StartDate { get; private set; }
    public DateOnly EndDate { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    private LeavePeriod() { }
    internal LeavePeriod(Guid clinicianId, DateOnly start, DateOnly end, string reason)
    {
        ClinicianId = clinicianId;
        StartDate = start;
        EndDate = end;
        Reason = reason;
    }
}
