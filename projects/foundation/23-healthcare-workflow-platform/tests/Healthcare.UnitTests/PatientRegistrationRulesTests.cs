using Healthcare.Domain.Common;
using Healthcare.Domain.Patients;
using Xunit;

namespace Healthcare.UnitTests.Domain;

public class PatientRegistrationRulesTests
{
    [Fact]
    public void Register_Requires_Names_And_E164_Phone()
    {
        var id = PatientId.Create(1);
        Assert.Throws<DomainException>(() =>
            Patient.Register(id, "", "Doe", new DateOnly(1990, 1, 1), "female", "+254700111222", null, ContactChannel.Sms, Guid.NewGuid()));
        Assert.Throws<DomainException>(() =>
            Patient.Register(id, "Jane", "Doe", new DateOnly(1990, 1, 1), "female", "0700111222", null, ContactChannel.Sms, Guid.NewGuid()));
    }

    [Fact]
    public void Email_Required_For_Email_Preferred_Contact()
    {
        var id = PatientId.Create(1);
        Assert.Throws<DomainException>(() =>
            Patient.Register(id, "Jane", "Doe", new DateOnly(1990, 1, 1), "female", "+254700111222",
                email: null, ContactChannel.Email, Guid.NewGuid()));
    }

    [Fact]
    public void Consent_Grants_And_Revokes()
    {
        var id = PatientId.Create(1);
        var patient = Patient.Register(id, "Jane", "Doe", new DateOnly(1990, 1, 1), "female",
            "+254700111222", null, ContactChannel.Sms, Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        patient.GrantConsent(ConsentKind.ReminderCommunication, now);
        Assert.True(patient.HasConsent(ConsentKind.ReminderCommunication));
        patient.RevokeConsent(ConsentKind.ReminderCommunication, now);
        Assert.False(patient.HasConsent(ConsentKind.ReminderCommunication));
    }
}
