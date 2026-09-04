using Healthcare.Domain.Appointments;
using Healthcare.Domain.Common;
using Xunit;

namespace Healthcare.UnitTests.Domain;

public class AppointmentStateMachineTests
{
    private static Appointment MakeAppointment()
    {
        var now = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);
        return Appointment.Book(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            now, now.AddMinutes(15));
    }

    [Fact]
    public void Book_Then_Confirm_Sets_Status()
    {
        var appt = MakeAppointment();
        appt.Confirm(DateTimeOffset.UtcNow);
        Assert.Equal(AppointmentStatus.Confirmed, appt.Status);
    }

    [Fact]
    public void CheckIn_From_Booked_Or_Confirmed_Is_Allowed()
    {
        var a = MakeAppointment();
        a.CheckIn(DateTimeOffset.UtcNow);
        Assert.Equal(AppointmentStatus.CheckedIn, a.Status);

        var b = MakeAppointment();
        b.Confirm(DateTimeOffset.UtcNow);
        b.CheckIn(DateTimeOffset.UtcNow);
        Assert.Equal(AppointmentStatus.CheckedIn, b.Status);
    }

    [Fact]
    public void Full_Happy_Path_Runs_To_Completed()
    {
        var a = MakeAppointment();
        var t = DateTimeOffset.UtcNow;
        a.Confirm(t); a.CheckIn(t.AddMinutes(1)); a.StartTriage(t.AddMinutes(2));
        a.AdmitToClinician(t.AddMinutes(6)); a.MoveToAwaitingResults(t.AddMinutes(20));
        a.Complete(t.AddMinutes(30));
        Assert.Equal(AppointmentStatus.Completed, a.Status);
        Assert.NotNull(a.CheckedInAt);
        Assert.NotNull(a.CompletedAt);
    }

    [Fact]
    public void Invalid_Transitions_Throw()
    {
        var a = MakeAppointment();
        // Cannot start triage from Booked
        Assert.Throws<DomainException>(() => a.StartTriage(DateTimeOffset.UtcNow));
        // Cannot mark no-show from Completed
        var b = MakeAppointment();
        b.Confirm(DateTimeOffset.UtcNow); b.CheckIn(DateTimeOffset.UtcNow); b.AdmitToClinician(DateTimeOffset.UtcNow);
        b.Complete(DateTimeOffset.UtcNow);
        Assert.Throws<DomainException>(() => b.MarkNoShow(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Cancel_Sets_Reason_And_Terminal()
    {
        var a = MakeAppointment();
        a.Cancel(CancellationReason.PatientRequest, "changed mind", DateTimeOffset.UtcNow);
        Assert.Equal(AppointmentStatus.Cancelled, a.Status);
        Assert.Equal(CancellationReason.PatientRequest, a.CancellationReason);
        Assert.Throws<DomainException>(() => a.Cancel(CancellationReason.Other, null, DateTimeOffset.UtcNow));
    }
}
