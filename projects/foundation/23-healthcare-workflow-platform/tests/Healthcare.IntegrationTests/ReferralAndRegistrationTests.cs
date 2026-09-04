using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Healthcare.Application.Abstractions;
using Healthcare.Application.Patients;
using Healthcare.Application.Referrals;
using Healthcare.Domain.Common;
using Healthcare.Domain.Patients;
using Healthcare.Domain.Referrals;
using Healthcare.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Healthcare.IntegrationTests;

public class ReferralAndRegistrationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ReferralAndRegistrationTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Duplicate_Patient_Registration_Is_Blocked_By_Fuzzy_Match()
    {
        var seed = await TestSeed.SeedAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<PatientRegistrationService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.Patients.FirstAsync(p => p.Id == seed.PatientId);
        // Same DOB and phone, slight spelling change on given name.
        var attempt = new RegisterPatientCommand(
            existing.GivenName.Substring(0, existing.GivenName.Length - 1) + "e",
            existing.FamilyName, existing.DateOfBirth, existing.Sex, existing.PhoneE164, existing.Email,
            ContactChannel.Sms, existing.RegisteredClinicId, false);
        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.RegisterAsync(attempt, "recep", "Receptionist", "test", CancellationToken.None));
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Different_Person_Is_Not_Flagged_As_Duplicate()
    {
        var seed = await TestSeed.SeedAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<PatientRegistrationService>();
        var attempt = new RegisterPatientCommand(
            "Zebedee", "Nyongesa", new DateOnly(1999, 3, 30), "male", "+254700123456",
            "zeb@example.test", ContactChannel.Sms, seed.FacilityId, false);
        var patient = await svc.RegisterAsync(attempt, "recep", "Receptionist", "test", CancellationToken.None);
        Assert.NotEqual(Guid.Empty, patient.Id);
    }

    [Fact]
    public async Task Urgent_Referral_Breaches_SLA_After_Advance()
    {
        var seed = await TestSeed.SeedAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ReferralService>();
        var referral = await svc.CreateAsync(new CreateReferralCommand(
            seed.PatientId, seed.ClinicianId, null, "External cardiology unit",
            "Cardiology", ReferralPriority.Urgent, "Chest pain 3 days"),
            "clin-a", Roles.Clinician, "test", CancellationToken.None);
        // Urgent SLA is 7 days per policy. Advance 8 days.
        _factory.Clock.Advance(TimeSpan.FromDays(8));
        var breached = await svc.EvaluateSlasAsync(CancellationToken.None);
        Assert.True(breached >= 1);
        using var scope2 = _factory.Services.CreateScope();
        var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var reloaded = await db.Referrals.FirstAsync(r => r.Id == referral.Id);
        Assert.True(reloaded.SlaBreached);
    }

    [Fact]
    public async Task Referral_Accepted_Before_Breach_Does_Not_Breach()
    {
        var seed = await TestSeed.SeedAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ReferralService>();
        var referral = await svc.CreateAsync(new CreateReferralCommand(
            seed.PatientId, seed.ClinicianId, null, "External clinic",
            "Cardiology", ReferralPriority.Routine, "Consult"),
            "clin-a", Roles.Clinician, "test", CancellationToken.None);
        await svc.TransitionAsync(referral.Id, "submit", null, "clin-a", Roles.Clinician, "test", CancellationToken.None);
        await svc.TransitionAsync(referral.Id, "triage", null, "clin-b", Roles.Clinician, "test", CancellationToken.None);
        await svc.TransitionAsync(referral.Id, "accept", null, "clin-b", Roles.Clinician, "test", CancellationToken.None);
        _factory.Clock.Advance(TimeSpan.FromDays(60));
        var breached = await svc.EvaluateSlasAsync(CancellationToken.None);
        Assert.Equal(0, breached);
    }
}
