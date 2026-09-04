using Auth.Legacy;

namespace Auth.Tests;

/// <summary>
/// The Forms authentication ticket. Two protectors: the one the framework shipped
/// (MAC-then-encrypt) and the one it should have (encrypt-then-MAC). The pair exists so
/// the difference can be measured rather than asserted.
/// </summary>
public sealed class FormsTicketTests
{
    private static readonly byte[] EncryptionKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] ValidationKey = Enumerable.Range(64, 64).Select(i => (byte)i).ToArray();
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static FormsTicket Ticket(string name = "finance.director", string roles = "Admin|Manager") =>
        new(2, name, Now, Now.AddMinutes(30), false, roles, "/");

    public static TheoryData<string> ProtectorNames() => new() { "legacy", "hardened" };

    private static ITicketProtector Protector(string which) => which == "legacy"
        ? new LegacyTicketProtector(EncryptionKey, ValidationKey)
        : new HardenedTicketProtector(EncryptionKey, ValidationKey);

    [Theory]
    [MemberData(nameof(ProtectorNames))]
    public void RoundTripsAValidTicket(string which)
    {
        var protector = Protector(which);
        var result = protector.Unprotect(protector.Protect(Ticket()), Now);

        Assert.True(result.Ok);
        Assert.Equal("finance.director", result.Ticket!.Name);
        Assert.Equal(new[] { "Admin", "Manager" }, result.Ticket.Roles);
    }

    [Theory]
    [MemberData(nameof(ProtectorNames))]
    public void PreservesEveryField(string which)
    {
        var protector = Protector(which);
        var original = new FormsTicket(2, "a.user", Now, Now.AddHours(8), true, "Clerk|Temp", "/app");
        var round = protector.Unprotect(protector.Protect(original), Now).Ticket!;

        Assert.Equal(original.Version, round.Version);
        Assert.Equal(original.Name, round.Name);
        Assert.Equal(original.IssuedUtc.ToUnixTimeSeconds(), round.IssuedUtc.ToUnixTimeSeconds());
        Assert.Equal(original.ExpiresUtc.ToUnixTimeSeconds(), round.ExpiresUtc.ToUnixTimeSeconds());
        Assert.Equal(original.IsPersistent, round.IsPersistent);
        Assert.Equal(original.UserData, round.UserData);
        Assert.Equal(original.CookiePath, round.CookiePath);
    }

    [Theory]
    [MemberData(nameof(ProtectorNames))]
    public void RejectsAnExpiredTicket(string which)
    {
        var protector = Protector(which);
        var result = protector.Unprotect(protector.Protect(Ticket()), Now.AddHours(1));

        Assert.False(result.Ok);
        Assert.Equal(TicketFailure.Expired, result.Failure);
    }

    [Theory]
    [MemberData(nameof(ProtectorNames))]
    public void AcceptsATicketAtTheInstantBeforeExpiry(string which)
    {
        var protector = Protector(which);
        Assert.True(protector.Unprotect(protector.Protect(Ticket()), Now.AddMinutes(30).AddTicks(-1)).Ok);
    }

    [Theory]
    [MemberData(nameof(ProtectorNames))]
    public void RejectsATicketFromADifferentKey(string which)
    {
        var mine = Protector(which);
        var theirs = which == "legacy"
            ? new LegacyTicketProtector(new byte[32], new byte[64])
            : (ITicketProtector)new HardenedTicketProtector(new byte[32], new byte[64]);

        Assert.False(mine.Unprotect(theirs.Protect(Ticket()), Now).Ok);
    }

    [Theory]
    [MemberData(nameof(ProtectorNames))]
    public void RejectsEverySingleByteFlip(string which)
    {
        // Exhaustive over the whole ciphertext rather than a spot check. A protector that
        // rejects a flip in the middle and accepts one in the last block has an integrity
        // check that does not cover what it appears to cover.
        var protector = Protector(which);
        var bytes = Convert.FromHexString(protector.Protect(Ticket()));

        for (var i = 0; i < bytes.Length; i++)
        {
            var mutated = (byte[])bytes.Clone();
            mutated[i] ^= 0x01;
            var result = protector.Unprotect(Convert.ToHexString(mutated), Now);
            Assert.False(result.Ok);
        }
    }

    [Theory]
    [MemberData(nameof(ProtectorNames))]
    public void RejectsTruncation(string which)
    {
        var protector = Protector(which);
        var text = protector.Protect(Ticket());

        for (var keep = 0; keep < text.Length; keep += 8)
        {
            Assert.False(protector.Unprotect(text[..keep], Now).Ok);
        }
    }

    [Fact]
    public void LegacyProtectorNamesTheParseFailure()
    {
        var protector = new LegacyTicketProtector(EncryptionKey, ValidationKey);
        foreach (var junk in new[] { "zz", "not hex", "abc" })
        {
            var result = protector.Unprotect(junk, Now);
            Assert.False(result.Ok);
            Assert.Equal(TicketFailure.Malformed, result.Failure);
        }
    }

    [Fact]
    public void HardenedProtectorReportsMalformedInputAsBadMac()
    {
        // Deliberate, and the reason the hardened protector has one rejection reason rather
        // than three. "This is not even hex" is knowable without touching the key, and it is
        // exactly the sort of free information that assembles into an oracle. The caller
        // gets one answer -- no -- and the specific reason goes to the log instead.
        var protector = new HardenedTicketProtector(EncryptionKey, ValidationKey);
        foreach (var junk in new[] { "", "zz", "not hex", "abc", "00" })
        {
            var result = protector.Unprotect(junk, Now);
            Assert.False(result.Ok);
            Assert.Equal(TicketFailure.BadMac, result.Failure);
        }
    }

    [Fact]
    public void LegacyProtectorLeaksMoreThanOneRejectionReason()
    {
        // The finding. MAC-then-encrypt has to decrypt before it can check anything, so the
        // unpadding step runs on attacker-controlled bytes and the failure it produces is
        // distinguishable from a MAC failure. That distinction is the padding oracle.
        var protector = new LegacyTicketProtector(EncryptionKey, ValidationKey);
        var bytes = Convert.FromHexString(protector.Protect(Ticket()));

        var reasons = new HashSet<TicketFailure>();
        for (var i = 0; i < bytes.Length; i++)
        {
            for (var bit = 0; bit < 8; bit++)
            {
                var mutated = (byte[])bytes.Clone();
                mutated[i] ^= (byte)(1 << bit);
                reasons.Add(protector.Unprotect(Convert.ToHexString(mutated), Now).Failure);
            }
        }

        reasons.Remove(TicketFailure.None);
        Assert.True(reasons.Count > 1,
            $"expected more than one distinguishable failure, saw {string.Join(", ", reasons)}");
        Assert.Contains(TicketFailure.BadPadding, reasons);
    }

    [Fact]
    public void HardenedProtectorLeaksExactlyOneRejectionReason()
    {
        // Encrypt-then-MAC does not handle the padding error consistently -- it makes the
        // padding code unreachable. That is a property that survives future edits, which
        // "we made sure both branches return the same message" is not.
        var protector = new HardenedTicketProtector(EncryptionKey, ValidationKey);
        var bytes = Convert.FromHexString(protector.Protect(Ticket()));

        var reasons = new HashSet<TicketFailure>();
        for (var i = 0; i < bytes.Length; i++)
        {
            for (var bit = 0; bit < 8; bit++)
            {
                var mutated = (byte[])bytes.Clone();
                mutated[i] ^= (byte)(1 << bit);
                reasons.Add(protector.Unprotect(Convert.ToHexString(mutated), Now).Failure);
            }
        }

        reasons.Remove(TicketFailure.None);
        Assert.Equal(new[] { TicketFailure.BadMac }, reasons.ToArray());
    }

    [Fact]
    public void HardenedProtectorRefusesTheLegacyFormat()
    {
        // Version rollback. If the hardened protector still accepts version 1 blobs, an
        // attacker downgrades to the format with the oracle and the hardening bought
        // nothing at all.
        var legacy = new LegacyTicketProtector(EncryptionKey, ValidationKey);
        var hardened = new HardenedTicketProtector(EncryptionKey, ValidationKey);

        Assert.False(hardened.Unprotect(legacy.Protect(Ticket()), Now).Ok);
    }

    [Fact]
    public void LegacyProtectorRefusesTheHardenedFormat()
    {
        var legacy = new LegacyTicketProtector(EncryptionKey, ValidationKey);
        var hardened = new HardenedTicketProtector(EncryptionKey, ValidationKey);

        Assert.False(legacy.Unprotect(hardened.Protect(Ticket()), Now).Ok);
    }

    [Fact]
    public void HardenedFormatIsVersionTagged()
    {
        var hardened = new HardenedTicketProtector(EncryptionKey, ValidationKey);
        var bytes = Convert.FromHexString(hardened.Protect(Ticket()));
        Assert.Equal(HardenedTicketProtector.FormatVersion, bytes[0]);
    }

    [Theory]
    [MemberData(nameof(ProtectorNames))]
    public void ProtectIsNotDeterministic(string which)
    {
        // A fresh IV per ticket. Without it, two logins by the same user at the same second
        // produce identical cookies, which is a session-fixation primitive delivered free.
        var protector = Protector(which);
        Assert.NotEqual(protector.Protect(Ticket()), protector.Protect(Ticket()));
    }

    [Fact]
    public void SerializationRoundTripsWithoutAProtector()
    {
        var original = Ticket("user", "A|B|C");
        var round = FormsTicket.Deserialize(original.Serialize());

        Assert.Equal(original.Name, round.Name);
        Assert.Equal(new[] { "A", "B", "C" }, round.Roles);
    }

    [Fact]
    public void EmptyUserDataMeansNoRoles()
    {
        Assert.Empty(new FormsTicket(2, "u", Now, Now.AddMinutes(1), false, "", "/").Roles);
    }

    [Fact]
    public void IsExpiredUsesTheSuppliedClock()
    {
        var ticket = Ticket();
        Assert.False(ticket.IsExpired(Now));
        Assert.False(ticket.IsExpired(Now.AddMinutes(29)));
        Assert.True(ticket.IsExpired(Now.AddMinutes(30)));
        Assert.True(ticket.IsExpired(Now.AddYears(1)));
    }

    [Fact]
    public void ForgeryWithALeakedValidationKeySucceeds()
    {
        // Not a defect: it is what a validation key *is*. It is here because the key lives
        // in web.config, is identical across the farm, and in a system this old is usually
        // in source control history -- which makes "the key has leaked" the assumption to
        // reason from rather than the one to dismiss.
        var attacker = new LegacyTicketProtector(EncryptionKey, ValidationKey);
        var server = new LegacyTicketProtector(EncryptionKey, ValidationKey);

        var forged = attacker.Protect(new FormsTicket(
            2, "finance.director", Now, Now.AddHours(1), false, "Admin", "/"));

        var accepted = server.Unprotect(forged, Now);
        Assert.True(accepted.Ok);
        Assert.Equal("finance.director", accepted.Ticket!.Name);
        Assert.Contains("Admin", accepted.Ticket.Roles);
    }
}
