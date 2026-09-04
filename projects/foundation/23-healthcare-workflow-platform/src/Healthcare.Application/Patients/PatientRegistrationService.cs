using Healthcare.Application.Abstractions;
using Healthcare.Application.Appointments;
using Healthcare.Domain.Common;
using Healthcare.Domain.Patients;
using Microsoft.EntityFrameworkCore;

namespace Healthcare.Application.Patients;

public sealed record RegisterPatientCommand(
    string GivenName,
    string FamilyName,
    DateOnly DateOfBirth,
    string Sex,
    string PhoneE164,
    string? Email,
    ContactChannel PreferredChannel,
    Guid RegisteredClinicId,
    bool AllowDuplicate = false);

public sealed record DuplicatePatientMatch(Guid PatientId, string PatientExternalId, int Score, string Reason);

/// <summary>
/// Registers a new synthetic patient, computing a fresh check-digited PatientId and refusing (unless
/// explicitly overridden) if a fuzzy match against existing patients suggests a duplicate.
/// </summary>
public sealed class PatientRegistrationService
{
    private readonly IAppDbContext _db;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    public PatientRegistrationService(IAppDbContext db, IClock clock, IAuditService audit)
    { _db = db; _clock = clock; _audit = audit; }

    public async Task<Patient> RegisterAsync(RegisterPatientCommand cmd, string actorId, string actorRole,
        string correlationId, CancellationToken ct)
    {
        var duplicates = await FindDuplicatesAsync(cmd.GivenName, cmd.FamilyName, cmd.DateOfBirth, cmd.PhoneE164, ct);
        if (duplicates.Any(d => d.Score >= 80) && !cmd.AllowDuplicate)
            throw new DomainException("patient.duplicate_suspected",
                $"Duplicate patient suspected: {string.Join("; ", duplicates.Take(3).Select(d => $"{d.PatientExternalId} ({d.Score}%)"))}");

        // Allocate deterministic sequential id (max existing + 1) with check digit.
        var count = await _db.Patients.CountAsync(ct);
        var idPayload = 1_000_000 + count + 1;
        var patientId = PatientId.Create(idPayload);

        var patient = Patient.Register(patientId, cmd.GivenName, cmd.FamilyName, cmd.DateOfBirth, cmd.Sex,
            cmd.PhoneE164, cmd.Email, cmd.PreferredChannel, cmd.RegisteredClinicId);
        _db.Patients.Add(patient);
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync(new AuditRequest(
            Healthcare.Domain.Audit.AuditKind.PatientDataWrite, actorId, actorRole,
            patient.ExternalId.Value, $"patient:{patient.Id}", "register",
            "patient.register", correlationId, false, null, patient.IsVip), ct);
        return patient;
    }

    public async Task<IReadOnlyList<DuplicatePatientMatch>> FindDuplicatesAsync(string given, string family,
        DateOnly dob, string phone, CancellationToken ct)
    {
        var all = await _db.Patients.ToListAsync(ct);
        var results = new List<DuplicatePatientMatch>();
        var normGiven = Normalise(given);
        var normFamily = Normalise(family);
        var normPhone = phone.Replace(" ", "").Replace("-", "");
        foreach (var p in all)
        {
            var score = 0;
            var reasons = new List<string>();
            if (p.DateOfBirth == dob) { score += 40; reasons.Add("dob"); }
            var pn = p.PhoneE164.Replace(" ", "").Replace("-", "");
            if (!string.IsNullOrEmpty(normPhone) && string.Equals(pn, normPhone, StringComparison.Ordinal))
            { score += 30; reasons.Add("phone"); }
            var famSim = Similarity(normFamily, Normalise(p.FamilyName));
            var givSim = Similarity(normGiven, Normalise(p.GivenName));
            if (famSim >= 0.85 && givSim >= 0.85)
            { score += 30; reasons.Add($"names ({famSim:F2}/{givSim:F2})"); }
            else if (famSim >= 0.9)
            { score += 15; reasons.Add($"family ({famSim:F2})"); }
            if (score > 0)
                results.Add(new DuplicatePatientMatch(p.Id, p.ExternalId.Value, score, string.Join("+", reasons)));
        }
        return results.OrderByDescending(r => r.Score).ToList();
    }

    private static string Normalise(string s) =>
        new string((s ?? "").Where(c => char.IsLetter(c)).ToArray()).ToLowerInvariant();

    // Simple similarity: Jaro-Winkler-lite. Uses Levenshtein-based ratio.
    private static double Similarity(string a, string b)
    {
        if (a == b) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;
        int lev = Levenshtein(a, b);
        return 1.0 - (double)lev / Math.Max(a.Length, b.Length);
    }

    private static int Levenshtein(string a, string b)
    {
        var costs = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) costs[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            costs[0] = i;
            int nw = i - 1;
            for (int j = 1; j <= b.Length; j++)
            {
                int cj = Math.Min(1 + Math.Min(costs[j], costs[j - 1]), a[i - 1] == b[j - 1] ? nw : nw + 1);
                nw = costs[j];
                costs[j] = cj;
            }
        }
        return costs[b.Length];
    }
}
