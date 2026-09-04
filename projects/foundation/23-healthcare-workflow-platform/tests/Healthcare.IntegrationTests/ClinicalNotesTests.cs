using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Healthcare.Application.Abstractions;
using Healthcare.Application.Appointments;
using Healthcare.Application.Encounters;
using Healthcare.Domain.Common;
using Healthcare.Domain.Encounters;
using Healthcare.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Healthcare.IntegrationTests;

public class ClinicalNotesTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ClinicalNotesTests(ApiFactory factory) => _factory = factory;

    private static int _slotCounter = 0;
    private static DateTimeOffset UniqueSlot()
    {
        var i = System.Threading.Interlocked.Increment(ref _slotCounter);
        return new DateTimeOffset(2026, 9, 8, 6, 0, 0, TimeSpan.Zero).AddMinutes(20 * i);
    }

    private async Task<(TestSeed.Seeded seed, Guid encounterId)> SeedAndOpenEncounterAsync()
    {
        var seed = await TestSeed.SeedAsync(_factory);
        var client = _factory.CreateClientAs("clin-a", new[] { Roles.Clinician }, seed.ClinicianId);
        // Book an appointment (unique slot).
        var slot = UniqueSlot();
        var book = await client.PostAsJsonAsync("/api/v1/appointments", new
        {
            patientId = seed.PatientId,
            clinicianId = seed.ClinicianId,
            facilityId = seed.FacilityId,
            roomId = seed.RoomId,
            appointmentTypeId = seed.GpTypeId,
            startUtc = slot
        });
        if (!book.IsSuccessStatusCode)
        {
            var bookErr = await book.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Book failed {(int)book.StatusCode}: {bookErr}");
        }
        var apptId = (await book.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var encResp = await client.PostAsJsonAsync("/api/v1/encounters", new { appointmentId = apptId, requiresCoSign = false });
        if (!encResp.IsSuccessStatusCode)
        {
            var encErr = await encResp.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Encounter open failed {(int)encResp.StatusCode}: {encErr}");
        }
        var encId = (await encResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return (seed, encId);
    }

    [Fact]
    public async Task Notes_Are_Append_Only_At_The_Persistence_Layer()
    {
        var (seed, encId) = await SeedAndOpenEncounterAsync();
        // Add an initial note
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<EncounterService>();
        var note = await svc.AddNoteAsync(encId, seed.ClinicianId, "cough", "obs", "URTI", "plan",
            "clin-a", Roles.Clinician, "test", CancellationToken.None);
        // Mutate the tracked note & try to save — DbContext should reject.
        using var scope2 = _factory.Services.CreateScope();
        var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var loaded = await db.ClinicalNotes.FirstAsync(n => n.Id == note.Id);
        var prop = typeof(ClinicalNote).GetProperty(nameof(ClinicalNote.Assessment))!;
        prop.SetValue(loaded, "tampered");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("append-only", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Amendment_Creates_New_Version_Not_Overwrite()
    {
        var (seed, encId) = await SeedAndOpenEncounterAsync();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<EncounterService>();
        var initial = await svc.AddNoteAsync(encId, seed.ClinicianId, "cough", "obs", "URTI", "plan",
            "clin-a", Roles.Clinician, "test", CancellationToken.None);
        var amend = await svc.AmendNoteAsync(encId, initial.Id, seed.ClinicianId, "cough+fever", "obs2",
            "URTI viral", "plan2", "added fever", "clin-a", Roles.Clinician, "test", CancellationToken.None);

        using var scope2 = _factory.Services.CreateScope();
        var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var all = await db.ClinicalNotes.Where(n => n.EncounterId == encId).OrderBy(n => n.Version).ToListAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal(1, all[0].Version);
        Assert.Equal(2, all[1].Version);
        Assert.Equal(all[0].RootNoteId, all[1].RootNoteId);
        // Original untouched.
        Assert.Equal("URTI", all[0].Assessment);
    }

    [Fact]
    public async Task Vitals_Endpoint_Rejects_Bad_Unit_With_422()
    {
        var (seed, encId) = await SeedAndOpenEncounterAsync();
        var client = _factory.CreateClientAs("clin-a", new[] { Roles.Clinician }, seed.ClinicianId);
        var resp = await client.PostAsJsonAsync($"/api/v1/encounters/{encId}/vitals", new
        {
            kind = "temperature", value = 98.6m, unit = "F"
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }
}
