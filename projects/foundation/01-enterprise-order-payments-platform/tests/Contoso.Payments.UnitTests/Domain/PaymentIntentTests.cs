using Contoso.Payments.Domain.Common;
using Contoso.Payments.Domain.Payments;

namespace Contoso.Payments.UnitTests.Domain;

public class PaymentIntentTests
{
    private static PaymentIntent NewIntent()
        => new(Guid.NewGuid(), Guid.NewGuid(), Money.Of(10m, "USD"), "idem-1", DateTimeOffset.UtcNow);

    [Fact]
    public void New_intent_starts_in_Requires()
    {
        var p = NewIntent();
        Assert.Equal(PaymentIntentStatus.Requires, p.Status);
    }

    [Fact]
    public void MarkAuthorized_moves_to_Authorized()
    {
        var p = NewIntent();
        p.MarkAuthorized("ref-1", DateTimeOffset.UtcNow);
        Assert.Equal(PaymentIntentStatus.Authorized, p.Status);
        Assert.Equal("ref-1", p.ProviderReference);
    }

    [Fact]
    public void MarkAuthorized_twice_throws()
    {
        var p = NewIntent();
        p.MarkAuthorized("ref-1", DateTimeOffset.UtcNow);
        var ex = Assert.Throws<DomainException>(() => p.MarkAuthorized("ref-2", DateTimeOffset.UtcNow));
        Assert.Equal("payment.illegal_transition", ex.Code);
    }

    [Fact]
    public void MarkCaptured_only_valid_from_Authorized()
    {
        var p = NewIntent();
        Assert.Throws<DomainException>(() => p.MarkCaptured(DateTimeOffset.UtcNow));
        p.MarkAuthorized("ref-1", DateTimeOffset.UtcNow);
        p.MarkCaptured(DateTimeOffset.UtcNow);
        Assert.Equal(PaymentIntentStatus.Captured, p.Status);
        Assert.Equal(10m, p.CapturedAmount.Amount);
    }

    [Fact]
    public void MarkVoided_only_valid_from_Authorized()
    {
        var p = NewIntent();
        Assert.Throws<DomainException>(() => p.MarkVoided(DateTimeOffset.UtcNow));
        p.MarkAuthorized("ref-1", DateTimeOffset.UtcNow);
        p.MarkVoided(DateTimeOffset.UtcNow);
        Assert.Equal(PaymentIntentStatus.Voided, p.Status);
    }

    [Fact]
    public void MarkFailed_from_Captured_throws()
    {
        var p = NewIntent();
        p.MarkAuthorized("ref-1", DateTimeOffset.UtcNow);
        p.MarkCaptured(DateTimeOffset.UtcNow);
        Assert.Throws<DomainException>(() => p.MarkFailed("code", "msg", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Attempts_accumulate()
    {
        var p = NewIntent();
        p.RecordAttempt(new PaymentAttempt(Guid.NewGuid(), 1, "Succeeded", "r-1", DateTimeOffset.UtcNow, 10));
        p.RecordAttempt(new PaymentAttempt(Guid.NewGuid(), 2, "Timeout", null, DateTimeOffset.UtcNow, 20));
        Assert.Equal(2, p.Attempts.Count);
    }

    [Fact]
    public void Missing_idempotency_key_throws()
    {
        Assert.Throws<DomainException>(() => new PaymentIntent(Guid.NewGuid(), Guid.NewGuid(),
            Money.Of(10m, "USD"), "", DateTimeOffset.UtcNow));
    }
}
