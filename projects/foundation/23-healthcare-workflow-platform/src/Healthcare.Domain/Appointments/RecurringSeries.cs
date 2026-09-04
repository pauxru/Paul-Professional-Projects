using Healthcare.Domain.Common;

namespace Healthcare.Domain.Appointments;

/// <summary>Recurring series with exception dates (dates on which the recurring occurrence is skipped).</summary>
public sealed class RecurringSeries : Entity
{
    public Guid PatientId { get; private set; }
    public Guid ClinicianId { get; private set; }
    public Guid FacilityId { get; private set; }
    public Guid AppointmentTypeId { get; private set; }
    public DayOfWeek Day { get; private set; }
    public TimeOnly LocalStartTime { get; private set; }
    public DateOnly StartDate { get; private set; }
    public DateOnly EndDate { get; private set; }
    public string ExceptionDatesCsv { get; private set; } = string.Empty;

    private RecurringSeries() { }

    public static RecurringSeries Create(Guid patientId, Guid clinicianId, Guid facilityId,
        Guid appointmentTypeId, DayOfWeek day, TimeOnly localStart, DateOnly startDate, DateOnly endDate)
    {
        if (endDate <= startDate) throw new DomainException("series.dates.invalid", "End date must be after start date.");
        return new RecurringSeries
        {
            PatientId = patientId,
            ClinicianId = clinicianId,
            FacilityId = facilityId,
            AppointmentTypeId = appointmentTypeId,
            Day = day,
            LocalStartTime = localStart,
            StartDate = startDate,
            EndDate = endDate
        };
    }

    public void AddExceptionDate(DateOnly date)
    {
        var current = ExceptionDatesCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet();
        current.Add(date.ToString("yyyy-MM-dd"));
        ExceptionDatesCsv = string.Join(",", current.OrderBy(s => s));
    }

    public IReadOnlyCollection<DateOnly> Occurrences()
    {
        var exceptions = ExceptionDatesCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(DateOnly.Parse)
            .ToHashSet();
        var results = new List<DateOnly>();
        for (var d = StartDate; d <= EndDate; d = d.AddDays(1))
        {
            if (d.DayOfWeek != Day) continue;
            if (exceptions.Contains(d)) continue;
            results.Add(d);
        }
        return results;
    }
}
