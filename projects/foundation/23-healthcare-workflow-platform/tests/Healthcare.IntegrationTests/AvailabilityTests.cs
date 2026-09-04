using System.Net.Http.Json;
using System.Text.Json;
using Healthcare.Application.Availability;
using Healthcare.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Healthcare.IntegrationTests;

public class AvailabilityTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AvailabilityTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Slot_Search_Returns_Slots_On_A_WeekDay()
    {
        var seed = await TestSeed.SeedAsync(_factory);
        var client = _factory.CreateClientAs("clin-1", new[] { Roles.Clinician }, seed.ClinicianId);
        // 2026-09-07 is a Monday.
        var req = new
        {
            facilityId = seed.FacilityId,
            clinicianId = seed.ClinicianId,
            appointmentTypeId = seed.GpTypeId,
            fromDate = "2026-09-07",
            toDate = "2026-09-07"
        };
        var resp = await client.PostAsJsonAsync("/api/v1/availability/search", req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var slots = body.GetProperty("slots").EnumerateArray().ToList();
        Assert.NotEmpty(slots);
        // First slot's local time should be at or after 09:00.
        Assert.All(slots, s =>
        {
            var localIso = s.GetProperty("localStartIso").GetString()!;
            var local = DateTime.Parse(localIso, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(local.TimeOfDay >= new TimeSpan(9, 0, 0));
            Assert.True(local.TimeOfDay < new TimeSpan(17, 0, 0));
        });
    }

    [Fact]
    public async Task Slot_Search_Excludes_Weekend_Days()
    {
        var seed = await TestSeed.SeedAsync(_factory);
        var client = _factory.CreateClientAs("clin-1", new[] { Roles.Clinician }, seed.ClinicianId);
        var req = new
        {
            facilityId = seed.FacilityId,
            clinicianId = seed.ClinicianId,
            appointmentTypeId = seed.GpTypeId,
            fromDate = "2026-09-05", // Sat
            toDate = "2026-09-06" // Sun
        };
        var resp = await client.PostAsJsonAsync("/api/v1/availability/search", req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var slots = body.GetProperty("slots").EnumerateArray().ToList();
        Assert.Empty(slots);
    }

    [Fact]
    public async Task Slot_Search_Skips_Booked_Room_For_Duration_Plus_Buffer()
    {
        var seed = await TestSeed.SeedAsync(_factory);
        var client = _factory.CreateClientAs("recep-1", new[] { Roles.Receptionist });
        // Book a slot at 09:00 local (06:00 UTC EAT+3).
        var slotUtc = new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero);
        var book = new
        {
            patientId = seed.PatientId,
            clinicianId = seed.ClinicianId,
            facilityId = seed.FacilityId,
            roomId = seed.RoomId,
            appointmentTypeId = seed.GpTypeId,
            startUtc = slotUtc
        };
        var r = await client.PostAsJsonAsync("/api/v1/appointments", book);
        r.EnsureSuccessStatusCode();

        // Then search — 09:00 slot should be gone.
        var search = await client.PostAsJsonAsync("/api/v1/availability/search", new
        {
            facilityId = seed.FacilityId,
            clinicianId = seed.ClinicianId,
            appointmentTypeId = seed.GpTypeId,
            fromDate = "2026-09-07",
            toDate = "2026-09-07"
        });
        var body = await search.Content.ReadFromJsonAsync<JsonElement>();
        var slots = body.GetProperty("slots").EnumerateArray().Select(s => s.GetProperty("startUtc").GetDateTime()).ToList();
        Assert.DoesNotContain(slotUtc.UtcDateTime, slots);
    }

    [Fact]
    public async Task Slot_Times_Are_Consistent_Across_A_Dst_Transition_London_Facility()
    {
        // 2026-10-25 is when Europe/London switches from BST (+01:00) to GMT (+00:00) at 02:00 local.
        // Verify that a 10:00 local slot on 24 Oct 2026 is offset +01:00 and a 10:00 local slot on
        // 26 Oct 2026 is offset +00:00.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Healthcare.Infrastructure.Persistence.AppDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<Healthcare.Application.Abstractions.IClock>();

        // Ensure NBO test facility exists, then add a London facility.
        var seed = await TestSeed.SeedAsync(_factory);
        var london = Healthcare.Domain.Facilities.Facility.Create("LON-TEST", "Demo Health Centre (fictional)", "Europe/London");
        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                    DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
            london.SetHours(day, new TimeOnly(9, 0), new TimeOnly(17, 0));
        var room = london.AddRoom("Room 1", new[] { "gp" });
        db.Facilities.Add(london);
        var clin = Healthcare.Domain.Clinicians.Clinician.Create("Diana", "Everett", "General Practitioner",
            new[] { "MBBS (fictional)" });
        clin.AssignToFacility(london.Id);
        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                    DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
            clin.SetWorkingPattern(london.Id, day, new TimeOnly(9, 0), new TimeOnly(17, 0));
        db.Clinicians.Add(clin);
        await db.SaveChangesAsync();

        var client = _factory.CreateClientAs("recep-1", new[] { Roles.Receptionist });
        var reqBefore = new { facilityId = london.Id, clinicianId = clin.Id, appointmentTypeId = seed.GpTypeId, fromDate = "2026-10-24", toDate = "2026-10-24" };
        var respBefore = await client.PostAsJsonAsync("/api/v1/availability/search", reqBefore);
        respBefore.EnsureSuccessStatusCode();
        var slotsBefore = (await respBefore.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("slots").EnumerateArray()
            .Where(s => s.GetProperty("localStartIso").GetString()!.Contains("T10:00:00"))
            .ToList();
        Assert.NotEmpty(slotsBefore);
        var beforeUtc = slotsBefore[0].GetProperty("startUtc").GetDateTime();
        // 10:00 BST (UTC+1) => 09:00 UTC
        Assert.Equal(new DateTime(2026, 10, 24, 9, 0, 0, DateTimeKind.Utc), beforeUtc.ToUniversalTime());

        var reqAfter = new { facilityId = london.Id, clinicianId = clin.Id, appointmentTypeId = seed.GpTypeId, fromDate = "2026-10-26", toDate = "2026-10-26" };
        var respAfter = await client.PostAsJsonAsync("/api/v1/availability/search", reqAfter);
        respAfter.EnsureSuccessStatusCode();
        var slotsAfter = (await respAfter.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("slots").EnumerateArray()
            .Where(s => s.GetProperty("localStartIso").GetString()!.Contains("T10:00:00"))
            .ToList();
        Assert.NotEmpty(slotsAfter);
        var afterUtc = slotsAfter[0].GetProperty("startUtc").GetDateTime();
        // 10:00 GMT (UTC+0) => 10:00 UTC
        Assert.Equal(new DateTime(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc), afterUtc.ToUniversalTime());
    }
}
