using Healthcare.Domain.Common;

namespace Healthcare.Domain.Patients;

public enum ContactChannel
{
    Sms = 0,
    Email = 1,
    Phone = 2
}

public enum ConsentKind
{
    ClinicalRecordShare = 0,
    ReminderCommunication = 1,
    ResearchOptIn = 2
}

public sealed class Patient : Entity
{
    public PatientId ExternalId { get; private set; }
    public string GivenName { get; private set; } = string.Empty;
    public string FamilyName { get; private set; } = string.Empty;
    public DateOnly DateOfBirth { get; private set; }
    public string Sex { get; private set; } = "unspecified";
    public string PhoneE164 { get; private set; } = string.Empty;
    public string? Email { get; private set; }
    public ContactChannel PreferredChannel { get; private set; }
    public Guid RegisteredClinicId { get; private set; }
    public bool IsVip { get; private set; }
    public bool RequiresConfirmation { get; private set; }
    public bool OptedOutOfReminders { get; private set; }
    public string? AllergiesFreeText { get; private set; }

    private readonly List<Consent> _consents = new();
    public IReadOnlyCollection<Consent> Consents => _consents.AsReadOnly();

    // For EF
    private Patient() { }

    private Patient(PatientId id, string given, string family, DateOnly dob, string sex, string phone,
        string? email, ContactChannel channel, Guid clinicId, bool isVip)
    {
        ExternalId = id;
        GivenName = given;
        FamilyName = family;
        DateOfBirth = dob;
        Sex = sex;
        PhoneE164 = phone;
        Email = email;
        PreferredChannel = channel;
        RegisteredClinicId = clinicId;
        IsVip = isVip;
    }

    public static Patient Register(PatientId id, string givenName, string familyName, DateOnly dob,
        string sex, string phoneE164, string? email, ContactChannel channel, Guid registeredClinicId,
        bool isVip = false)
    {
        if (string.IsNullOrWhiteSpace(givenName)) throw new DomainException("patient.given_name.required", "Given name required.");
        if (string.IsNullOrWhiteSpace(familyName)) throw new DomainException("patient.family_name.required", "Family name required.");
        if (string.IsNullOrWhiteSpace(phoneE164) || !phoneE164.StartsWith("+"))
            throw new DomainException("patient.phone.invalid", "Phone must be E.164 with leading '+'.");
        if (channel == ContactChannel.Email && string.IsNullOrWhiteSpace(email))
            throw new DomainException("patient.email.required", "Email required for email-preferred contact.");
        return new Patient(id, givenName.Trim(), familyName.Trim(), dob, sex, phoneE164, email, channel,
            registeredClinicId, isVip);
    }

    public void GrantConsent(ConsentKind kind, DateTimeOffset at)
    {
        var existing = _consents.FirstOrDefault(c => c.Kind == kind);
        if (existing is null)
        {
            _consents.Add(new Consent { Kind = kind, GrantedAt = at });
        }
        else
        {
            existing.GrantedAt = at;
            existing.RevokedAt = null;
        }
    }

    public void RevokeConsent(ConsentKind kind, DateTimeOffset at)
    {
        var existing = _consents.FirstOrDefault(c => c.Kind == kind);
        if (existing is not null) existing.RevokedAt = at;
    }

    public bool HasConsent(ConsentKind kind) =>
        _consents.Any(c => c.Kind == kind && c.RevokedAt is null);

    public void OptOutOfReminders() => OptedOutOfReminders = true;
    public void OptInToReminders() => OptedOutOfReminders = false;

    public void FlagRequiresConfirmation() => RequiresConfirmation = true;
    public void ClearRequiresConfirmation() => RequiresConfirmation = false;

    public void SetAllergies(string? text) => AllergiesFreeText = text;
}

public sealed class Consent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ConsentKind Kind { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
