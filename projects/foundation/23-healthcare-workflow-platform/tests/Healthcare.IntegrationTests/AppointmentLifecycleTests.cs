using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Healthcare.Domain.Appointments;
using Healthcare.Domain.Common;
using Xunit;

namespace Healthcare.IntegrationTests;

public class AppointmentLifecycleTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AppointmentLifecycleTests(ApiFactory factory) => _factory = factory;

    private static int _slotCounter = 0;
    private static DateTimeOffset UniqueSlot()
    {
        var i = System.Threading.Interlocked.Increment(ref _slotCounter);
        return new DateTimeOffset(2026, 9, 9, 6, 0, 0, TimeSpan.Zero).AddMinutes(20 * i);
    }

    private async Task<Guid> BookAsync(TestSeed.Seeded seed, HttpClient client, DateTimeOffset startUtc)
    {
        var r = await client.PostAsJsonAsync("/api/v1/appointments", new
        {
            patientId = seed.PatientId,
            clinicianId = seed.ClinicianId,
            facilityId = seed.FacilityId,
            roomId = seed.RoomId,
            appointmentTypeId = seed.GpTypeId,
            startUtc
        });
        if (!r.IsSuccessStatusCode)
        {
            var body = await r.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Book failed {(int)r.StatusCode}: {body}");
        }
        var respBody = await r.Content.ReadFromJsonAsync<JsonElement>();
        return respBody.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Book_Then_Reschedule_Updates_StartUtc()
    {
        var seed = await TestSeed.SeedAsync(_factory);
        var client = _factory.CreateClientAs("recep-1", new[] { Roles.Receptionist });
        var start = UniqueSlot();
        var id = await BookAsync(seed, client, start);
        var newStart = start.AddHours(2);
        var resp = await client.PostAsJsonAsync($"/api/v1/appointments/{id}/reschedule", new { newStartUtc = newStart });
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        var get = await client.GetAsync($"/api/v1/appointments/{id}");
        var body = await get.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(newStart.UtcDateTime, body.GetProperty("startUtc").GetDateTime().ToUniversalTime());
    }

    [Fact]
    public async Task Cancel_Then_Book_Same_Slot_Is_Allowed()
    {
        var seed = await TestSeed.SeedAsync(_factory);
        var client = _factory.CreateClientAs("recep-1", new[] { Roles.Receptionist });
        var slot = UniqueSlot();
        var id = await BookAsync(seed, client, slot);
        var cancel = await client.PostAsJsonAsync($"/api/v1/appointments/{id}/cancel", new { reason = CancellationReason.PatientRequest, notes = "test" });
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        // Book same slot again — should succeed for a different patient.
        var rebook = await client.PostAsJsonAsync("/api/v1/appointments", new
        {
            patientId = seed.Patient2Id,
            clinicianId = seed.ClinicianId,
            facilityId = seed.FacilityId,
            roomId = seed.RoomId,
            appointmentTypeId = seed.GpTypeId,
            startUtc = slot
        });
        if (rebook.StatusCode != HttpStatusCode.Created)
        {
            var body = await rebook.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Rebook failed {(int)rebook.StatusCode}: {body}");
        }
    }

    [Fact]
    public async Task Status_Transitions_Via_Api_Follow_The_Machine()
    {
        var seed = await TestSeed.SeedAsync(_factory);
        var client = _factory.CreateClientAs("recep-1", new[] { Roles.Receptionist });
        var id = await BookAsync(seed, client, UniqueSlot());

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/v1/appointments/{id}/status", new { action = "confirm" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/v1/appointments/{id}/status", new { action = "check-in" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/v1/appointments/{id}/status", new { action = "triage" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/v1/appointments/{id}/status", new { action = "with-clinician" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/v1/appointments/{id}/status", new { action = "complete" })).StatusCode);
    }

    [Fact]
    public async Task Bad_Booking_Returns_422_With_ProblemDetails()
    {
        var seed = await TestSeed.SeedAsync(_factory);
        var client = _factory.CreateClientAs("recep-1", new[] { Roles.Receptionist });
        // Nonexistent patient id.
        var bad = new
        {
            patientId = Guid.NewGuid(),
            clinicianId = seed.ClinicianId,
            facilityId = seed.FacilityId,
            roomId = seed.RoomId,
            appointmentTypeId = seed.GpTypeId,
            startUtc = UniqueSlot()
        };
        var r = await client.PostAsJsonAsync("/api/v1/appointments", bad);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("title", out _));
    }
}
