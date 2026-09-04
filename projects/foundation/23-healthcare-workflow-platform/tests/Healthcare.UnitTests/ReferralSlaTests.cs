using Healthcare.Domain.Common;
using Healthcare.Domain.Referrals;
using Xunit;

namespace Healthcare.UnitTests.Domain;

public class ReferralSlaTests
{
    [Fact]
    public void Routine_Sla_Is_28Days()
    {
        var now = new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);
        var r = Referral.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "", "cardiology",
            ReferralPriority.Routine, "chest pain", now);
        Assert.Equal(now.AddDays(28), r.SlaDueUtc);
    }

    [Fact]
    public void Urgent_Sla_Is_7Days()
    {
        var now = new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);
        var r = Referral.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "", "cardiology",
            ReferralPriority.Urgent, "sob at rest", now);
        Assert.Equal(now.AddDays(7), r.SlaDueUtc);
    }

    [Fact]
    public void Sla_Breach_Marked_After_Due()
    {
        var now = new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);
        var r = Referral.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "", "cardiology",
            ReferralPriority.Urgent, "sob", now);
        r.Submit(now);
        var breached = r.EvaluateSla(now.AddDays(8));
        Assert.True(breached);
        Assert.True(r.SlaBreached);
    }

    [Fact]
    public void Accepted_Referral_Does_Not_Breach()
    {
        var now = new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);
        var r = Referral.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "", "cardiology",
            ReferralPriority.Urgent, "sob", now);
        r.Submit(now); r.Triage(now.AddDays(1)); r.Accept(now.AddDays(3));
        var breached = r.EvaluateSla(now.AddDays(30));
        Assert.False(breached);
    }

    [Fact]
    public void Reject_Requires_Reason()
    {
        var now = new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);
        var r = Referral.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "", "cardiology",
            ReferralPriority.Urgent, "sob", now);
        r.Submit(now);
        Assert.Throws<DomainException>(() => r.Reject("", now));
    }
}
