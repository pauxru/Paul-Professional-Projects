using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Healthcare.Application.Abstractions;
using Healthcare.Application.Reminders;
using Healthcare.Domain.Appointments;
using Healthcare.Domain.Common;
using Healthcare.Domain.Reminders;
using Healthcare.Infrastructure.Adapters;
using Healthcare.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Healthcare.IntegrationTests;

public class ReminderTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ReminderTests(ApiFactory factory) => _factory = factory;

    private async Task<(TestSeed.Seeded seed, Guid apptId)> ArrangeAppointmentAsync(DateTimeOffset? slot = null)
    {
        var seed = await TestSeed.SeedAsync(_factory);
        var client = _factory.CreateClientAs("recep", new[] { Roles.Receptionist });
        var s = slot ?? UniqueSlot();
        var book = await client.PostAsJsonAsync("/api/v1/appointments", new
        {
            patientId = seed.PatientId,
            clinicianId = seed.ClinicianId,
            facilityId = seed.FacilityId,
            roomId = seed.RoomId,
            appointmentTypeId = seed.GpTypeId,
            startUtc = s
        });
        if (!book.IsSuccessStatusCode)
        {
            var body = await book.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Booking failed with {(int)book.StatusCode} {book.StatusCode}: {body}");
        }
        var apptId = (await book.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return (seed, apptId);
    }

    private static int _slotCounter = 0;
    private static DateTimeOffset UniqueSlot()
    {
        var i = System.Threading.Interlocked.Increment(ref _slotCounter);
        return new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero).AddMinutes(20 * i);
    }

    [Fact]
    public async Task ScheduleAsync_Is_Idempotent()
    {
        var (seed, apptId) = await ArrangeAppointmentAsync();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ReminderService>();
        var first = await svc.ScheduleAsync(apptId, null, CancellationToken.None);
        var second = await svc.ScheduleAsync(apptId, null, CancellationToken.None);
        Assert.True(first > 0, "First scheduling should insert reminders.");
        Assert.Equal(0, second);

        using var scope2 = _factory.Services.CreateScope();
        var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var perLead = await db.Reminders.CountAsync(r => r.AppointmentId == apptId);
        Assert.Equal(first, perLead);
    }

    [Fact]
    public async Task Reminder_Dispatch_Sends_At_Send_Time_And_Marks_Sent()
    {
        // Schedule appointment far enough in the future that all lead-times are pending.
        _factory.Clock.Set(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var slot = UniqueSlot().AddHours(4);
        var (seed, apptId) = await ArrangeAppointmentAsync(slot);
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ReminderService>();
        await svc.ScheduleAsync(apptId, null, CancellationToken.None);
        // Advance to a moment 48h before the appointment.
        _factory.Clock.Set(slot.AddHours(-46));
        var dispatched = await svc.DispatchDueAsync(CancellationToken.None);
        Assert.True(dispatched >= 1, $"Expected at least 1 dispatched reminder, got {dispatched}.");
        using var scope2 = _factory.Services.CreateScope();
        var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var sent = await db.Reminders.CountAsync(r => r.AppointmentId == apptId && r.Status == ReminderStatus.Sent);
        Assert.True(sent >= 1);
        // The SMS channel should have logged something.
        var sms = _factory.Services.GetServices<IReminderChannel>()
            .OfType<InMemorySmsReminderChannel>().First();
        Assert.NotEmpty(sms.Log);
    }

    [Fact]
    public async Task Opted_Out_Patient_Gets_No_Reminders()
    {
        var seed = await TestSeed.SeedAsync(_factory);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var p = await db.Patients.FirstAsync(p => p.Id == seed.Patient2Id);
            typeof(Domain.Patients.Patient)
                .GetProperty(nameof(Domain.Patients.Patient.OptedOutOfReminders))!
                .SetValue(p, true);
            await db.SaveChangesAsync();
        }
        var client = _factory.CreateClientAs("recep", new[] { Roles.Receptionist });
        var slot = UniqueSlot();
        var book = await client.PostAsJsonAsync("/api/v1/appointments", new
        {
            patientId = seed.Patient2Id,
            clinicianId = seed.ClinicianId,
            facilityId = seed.FacilityId,
            roomId = seed.RoomId,
            appointmentTypeId = seed.GpTypeId,
            startUtc = slot
        });
        if (!book.IsSuccessStatusCode)
        {
            var body = await book.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Booking failed with {(int)book.StatusCode}: {body}");
        }
        var apptId = (await book.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var scope2 = _factory.Services.CreateScope();
        var svc = scope2.ServiceProvider.GetRequiredService<ReminderService>();
        var scheduled = await svc.ScheduleAsync(apptId, null, CancellationToken.None);
        Assert.Equal(0, scheduled);
    }

    [Fact]
    public async Task Confirm_Reply_Updates_Appointment_To_Confirmed()
    {
        var (seed, apptId) = await ArrangeAppointmentAsync();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ReminderService>();
        var changed = await svc.ApplyConfirmationAsync(apptId, CancellationToken.None);
        Assert.True(changed);
        using var scope2 = _factory.Services.CreateScope();
        var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var appt = await db.Appointments.FirstAsync(a => a.Id == apptId);
        Assert.Equal(AppointmentStatus.Confirmed, appt.Status);
    }
}
