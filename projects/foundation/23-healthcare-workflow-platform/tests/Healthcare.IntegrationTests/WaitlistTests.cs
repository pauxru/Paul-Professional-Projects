using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Healthcare.Application.Abstractions;
using Healthcare.Application.Appointments;
using Healthcare.Application.Waitlist;
using Healthcare.Domain.Appointments;
using Healthcare.Domain.Common;
using Healthcare.Domain.Waitlist;
using Healthcare.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Healthcare.IntegrationTests;

public class WaitlistTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public WaitlistTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Cancelling_Appointment_Offers_Slot_To_Highest_Priority_Waitlist_Entry()
    {
        var seed = await TestSeed.SeedAsync(_factory);
        var client = _factory.CreateClientAs("recep", new[] { Roles.Receptionist });

        // Book an appointment.
        var slot = new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero);
        var book = await client.PostAsJsonAsync("/api/v1/appointments", new
        {
            patientId = seed.PatientId,
            clinicianId = seed.ClinicianId,
            facilityId = seed.FacilityId,
            roomId = seed.RoomId,
            appointmentTypeId = seed.GpTypeId,
            startUtc = slot
        });
        var bookedBody = await book.Content.ReadFromJsonAsync<JsonElement>();
        var apptId = bookedBody.GetProperty("id").GetGuid();

        // Two patients on waitlist, one Urgent, one Standard.
        using var scope = _factory.Services.CreateScope();
        var wl = scope.ServiceProvider.GetRequiredService<WaitlistService>();
        var stdEntry = await wl.JoinAsync(seed.Patient2Id, seed.FacilityId, seed.GpTypeId, WaitlistPriority.Standard, "recep", "Receptionist", "test", CancellationToken.None);
        // Second patient - create another patient reusing p1 (already booked patient) but with elevated waitlist priority
        var urgentEntry = await wl.JoinAsync(seed.PatientId, seed.FacilityId, seed.GpTypeId, WaitlistPriority.Urgent, "recep", "Receptionist", "test", CancellationToken.None);

        // Cancel the appointment via API — should trigger auto-offer via WaitlistService.OfferSlotAsync
        var cancel = await client.PostAsJsonAsync($"/api/v1/appointments/{apptId}/cancel",
            new { reason = CancellationReason.PatientRequest, notes = "test" });
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);

        // The urgent entry should be Offered; the standard entry should still be Active.
        using var scope2 = _factory.Services.CreateScope();
        var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var refreshedUrgent = await db.WaitlistEntries.FirstAsync(w => w.Id == urgentEntry.Id);
        var refreshedStd = await db.WaitlistEntries.FirstAsync(w => w.Id == stdEntry.Id);
        Assert.Equal(WaitlistStatus.Offered, refreshedUrgent.Status);
        Assert.Equal(WaitlistStatus.Active, refreshedStd.Status);
        Assert.Equal(apptId, refreshedUrgent.OfferedAppointmentId);
    }

    [Fact]
    public async Task Waitlist_Offer_Expires_After_Window()
    {
        var seed = await TestSeed.SeedAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var wl = scope.ServiceProvider.GetRequiredService<WaitlistService>();
        var entry = await wl.JoinAsync(seed.PatientId, seed.FacilityId, seed.GpTypeId, WaitlistPriority.Urgent, "recep", "Receptionist", "test", CancellationToken.None);
        var offered = await wl.OfferSlotAsync(seed.FacilityId, seed.GpTypeId, Guid.NewGuid(), CancellationToken.None);
        Assert.NotNull(offered);
        // Advance clock past acceptance window.
        _factory.Clock.Advance(WaitlistService.AcceptanceWindow + TimeSpan.FromMinutes(1));
        var expired = await wl.ExpireStaleOffersAsync(CancellationToken.None);
        Assert.True(expired >= 1);
        using var scope2 = _factory.Services.CreateScope();
        var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var refreshed = await db.WaitlistEntries.FirstAsync(w => w.Id == entry.Id);
        Assert.Equal(WaitlistStatus.Expired, refreshed.Status);
    }
}
