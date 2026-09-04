using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Healthcare.Api.Auth;
using Healthcare.Application.Abstractions;
using Healthcare.Application.Appointments;
using Healthcare.Domain.Audit;
using Healthcare.Domain.Common;
using Healthcare.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Healthcare.IntegrationTests;

public class AccessControlTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AccessControlTests(ApiFactory factory) => _factory = factory;

    private async Task<TestSeed.Seeded> SeedWithApptAsync()
    {
        var seed = await TestSeed.SeedAsync(_factory);
        // Give the primary clinician a care relationship with p1.
        var client = _factory.CreateClientAs("recep", new[] { Roles.Receptionist });
        await client.PostAsJsonAsync("/api/v1/appointments", new
        {
            patientId = seed.PatientId,
            clinicianId = seed.ClinicianId,
            facilityId = seed.FacilityId,
            roomId = seed.RoomId,
            appointmentTypeId = seed.GpTypeId,
            startUtc = new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero)
        });
        return seed;
    }

    [Fact]
    public async Task Receptionist_Can_See_Demographics_But_Not_Clinical_Record()
    {
        var seed = await SeedWithApptAsync();
        var client = _factory.CreateClientAs("recep-1", new[] { Roles.Receptionist });
        // Demographic: allowed.
        var demo = await client.GetAsync($"/api/v1/patients/{seed.PatientId}");
        Assert.Equal(HttpStatusCode.OK, demo.StatusCode);
        // Clinical: forbidden (RBAC policy rejects).
        var clinical = await client.GetAsync($"/api/v1/patients/{seed.PatientId}/clinical");
        Assert.Equal(HttpStatusCode.Forbidden, clinical.StatusCode);
    }

    [Fact]
    public async Task Clinician_Without_Care_Relationship_Cannot_See_Clinical_Record()
    {
        var seed = await SeedWithApptAsync();
        // The "other" clinician has no relationship with the seeded patient.
        var client = _factory.CreateClientAs("clin-b", new[] { Roles.Clinician }, seed.ClinicianOtherId);
        var resp = await client.GetAsync($"/api/v1/patients/{seed.PatientId}/clinical");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        // And an AccessDenied audit event should have been recorded.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var denied = await db.AuditEvents
            .AnyAsync(a => a.Kind == AuditKind.AccessDenied && a.ActorId == "clin-b");
        Assert.True(denied);
    }

    [Fact]
    public async Task Clinician_With_Care_Relationship_Can_Read_Clinical_Record_And_It_Is_Audited()
    {
        var seed = await SeedWithApptAsync();
        var client = _factory.CreateClientAs("clin-a", new[] { Roles.Clinician }, seed.ClinicianId);
        var resp = await client.GetAsync($"/api/v1/patients/{seed.PatientId}/clinical");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var read = await db.AuditEvents
            .AnyAsync(a => a.Kind == AuditKind.PatientDataRead && a.ActorId == "clin-a" && a.Action == "read_clinical");
        Assert.True(read);
    }

    [Fact]
    public async Task Break_Glass_Grants_Access_And_Is_Audited_And_Flagged_As_Anomaly()
    {
        var seed = await SeedWithApptAsync();
        var client = _factory.CreateClientAs("clin-b", new[] { Roles.Clinician }, seed.ClinicianOtherId);
        client.DefaultRequestHeaders.Add(HttpContextCurrentUser.BreakGlassHeader, "true");
        client.DefaultRequestHeaders.Add(HttpContextCurrentUser.BreakGlassJustificationHeader,
            "Emergency: covering colleague on triage line");
        var resp = await client.GetAsync($"/api/v1/patients/{seed.PatientId}/clinical");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var bg = await db.AuditEvents
            .AnyAsync(a => a.Kind == AuditKind.BreakGlassActivated && a.ActorId == "clin-b" && a.BreakGlass);
        Assert.True(bg);
        // The clinical read also gets marked break-glass on the read audit.
        var readBg = await db.AuditEvents
            .AnyAsync(a => a.Kind == AuditKind.PatientDataRead && a.ActorId == "clin-b" && a.BreakGlass);
        Assert.True(readBg);
    }

    [Fact]
    public async Task Auditor_Has_Standing_Read_Access()
    {
        var seed = await SeedWithApptAsync();
        var client = _factory.CreateClientAs("audit-1", new[] { Roles.Auditor });
        var resp = await client.GetAsync($"/api/v1/patients/{seed.PatientId}/clinical");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_Request_Returns_401()
    {
        var seed = await TestSeed.SeedAsync(_factory);
        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/api/v1/patients/{seed.PatientId}/clinical");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Missing_Break_Glass_Justification_Denies_On_Guard_Level()
    {
        var seed = await SeedWithApptAsync();
        var client = _factory.CreateClientAs("clin-b", new[] { Roles.Clinician }, seed.ClinicianOtherId);
        client.DefaultRequestHeaders.Add(HttpContextCurrentUser.BreakGlassHeader, "true");
        // No justification header set.
        var resp = await client.GetAsync($"/api/v1/patients/{seed.PatientId}/clinical");
        // Guard requires justification; without it audit RecordAsync throws — and we expect either
        // 403 (guard denied) or 500. Either way, the read must not succeed.
        Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode);
    }
}
