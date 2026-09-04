using AuditPlatform.Domain.Errors;
using AuditPlatform.Domain.Events;
using AuditPlatform.Domain.Integrity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AuditPlatform.Infrastructure.Persistence.Interceptors;

/// <summary>
/// EF Core SaveChanges interceptor that enforces append-only semantics on audit entities.
///
/// Any tracked entity of type <see cref="AuditEvent"/> or <see cref="Checkpoint"/> in
/// <see cref="EntityState.Deleted"/> is rejected outright. For <see cref="EntityState.Modified"/>
/// the only legal mutation is the tombstone flip (IsTombstoned false → true), performed by
/// the retention pruner. Everything else — the ChainHash, ContentHash, PayloadJson (except the
/// controlled tombstone rewrite), etc — must remain byte-identical.
///
/// Documented in ADR-006 as one of three defence-in-depth layers:
///   1. Private setters on the entity (compile-time barrier).
///   2. This interceptor (runtime barrier, in-process).
///   3. Database-level revocation of UPDATE/DELETE on the AuditEvents table
///      (production hardening, out of scope for the local SQLite build).
/// </summary>
public sealed class AppendOnlyInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Enforce(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Enforce(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void Enforce(DbContext? context)
    {
        if (context is null) return;
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is AuditEvent evt)
            {
                if (entry.State == EntityState.Deleted)
                    throw new DomainException(DomainErrorCode.AppendOnlyViolation,
                        $"AuditEvent {evt.Id} cannot be deleted — the audit log is append-only.");

                if (entry.State == EntityState.Modified)
                {
                    if (!IsPermittedTombstoneTransition(entry))
                        throw new DomainException(DomainErrorCode.AppendOnlyViolation,
                            $"AuditEvent {evt.Id} cannot be modified — the audit log is append-only. " +
                            "The only permitted mutation is a retention tombstone (IsTombstoned: false → true).");
                }
            }
            else if (entry.Entity is Checkpoint cp)
            {
                if (entry.State == EntityState.Deleted)
                    throw new DomainException(DomainErrorCode.AppendOnlyViolation,
                        $"Checkpoint {cp.Id} cannot be deleted.");
                if (entry.State == EntityState.Modified)
                {
                    if (!IsPermittedSignatureAttach(entry))
                        throw new DomainException(DomainErrorCode.AppendOnlyViolation,
                            $"Checkpoint {cp.Id} cannot be modified after signing.");
                }
            }
        }
    }

    private static bool IsPermittedTombstoneTransition(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
    {
        var tomb = entry.Property(nameof(AuditEvent.IsTombstoned));
        var payload = entry.Property(nameof(AuditEvent.PayloadJson));
        var tombAt = entry.Property(nameof(AuditEvent.TombstonedAt));

        // 1. IsTombstoned must be flipping from false -> true.
        if (!(tomb.IsModified && Equals(tomb.OriginalValue, false) && Equals(tomb.CurrentValue, true))) return false;

        // 2. Every property other than IsTombstoned/PayloadJson/TombstonedAt must be unchanged.
        foreach (var p in entry.Properties)
        {
            if (p.Metadata.Name is nameof(AuditEvent.IsTombstoned) or nameof(AuditEvent.PayloadJson) or nameof(AuditEvent.TombstonedAt))
                continue;
            if (p.IsModified) return false;
        }

        // 3. The new PayloadJson must be the deterministic tombstone form.
        var contentHashProp = entry.Property(nameof(AuditEvent.ContentHash));
        var expectedPayload = AuditEvent.TombstonePayloadForContentHash((string)(contentHashProp.CurrentValue ?? string.Empty));
        return Equals(payload.CurrentValue, expectedPayload);
    }

    private static bool IsPermittedSignatureAttach(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
    {
        var sig = entry.Property(nameof(Checkpoint.SignatureBase64));
        var key = entry.Property(nameof(Checkpoint.SigningKeyId));

        // Signing is a one-shot: original values must be null; current values must be non-null; everything else unchanged.
        if (sig.OriginalValue is not null && (string?)sig.OriginalValue != null) return false;
        if (sig.CurrentValue is null || string.IsNullOrEmpty((string?)sig.CurrentValue)) return false;
        if (key.CurrentValue is null || string.IsNullOrEmpty((string?)key.CurrentValue)) return false;

        foreach (var p in entry.Properties)
        {
            if (p.Metadata.Name is nameof(Checkpoint.SignatureBase64) or nameof(Checkpoint.SigningKeyId)) continue;
            if (p.IsModified) return false;
        }
        return true;
    }
}
