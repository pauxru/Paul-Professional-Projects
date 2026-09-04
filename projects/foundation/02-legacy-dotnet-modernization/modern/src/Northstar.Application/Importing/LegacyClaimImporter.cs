using System.Security.Cryptography;
using System.Text;
using Northstar.Application.Abstractions;
using Northstar.Domain.Claims;
using Northstar.Domain.Policies;

namespace Northstar.Application.Importing;

public sealed class LegacyClaimImporter(ILegacyClaimSource source, IClaimsStore store)
{
    public async Task<LegacyImportReport> ImportAsync(CancellationToken cancellationToken)
    {
        var sourceRows = await source.ReadClaimsAsync(cancellationToken);
        var rejections = new List<LegacyImportRejection>();
        var importedReferences = new List<string>();
        var policies = new Dictionary<string, Policy>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in sourceRows)
        {
            if (string.IsNullOrWhiteSpace(row.ClaimReference))
            {
                rejections.Add(new(row.LegacyClaimId, row.ClaimReference, "Claim reference is missing."));
                continue;
            }

            if (row.ClaimedAmount <= 0)
            {
                rejections.Add(new(row.LegacyClaimId, row.ClaimReference, "Claimed amount must be greater than zero."));
                continue;
            }

            if (!Enum.TryParse<ClaimStatus>(row.Status, true, out var status))
            {
                rejections.Add(new(row.LegacyClaimId, row.ClaimReference, $"Unsupported legacy status '{row.Status}'."));
                continue;
            }

            if (await store.ClaimReferenceExistsAsync(row.ClaimReference.Trim().ToUpperInvariant(), cancellationToken))
            {
                rejections.Add(new(row.LegacyClaimId, row.ClaimReference, "Claim reference already exists in target."));
                continue;
            }

            if (!policies.TryGetValue(row.PolicyNumber, out var policy))
            {
                policy = await store.FindPolicyByNumberAsync(row.PolicyNumber, cancellationToken);
                if (policy is null)
                {
                    try
                    {
                        var policyholder = new Policyholder(Guid.NewGuid(), row.PolicyholderName, row.PolicyholderEmail);
                        policy = new Policy(Guid.NewGuid(), policyholder.Id, row.PolicyNumber, row.DeductibleAmount, row.PolicyLimitAmount, row.Currency);
                        store.AddPolicyholder(policyholder);
                        store.AddPolicy(policy);
                    }
                    catch (Exception exception)
                    {
                        rejections.Add(new(row.LegacyClaimId, row.ClaimReference, $"Policy mapping failed: {exception.Message}"));
                        continue;
                    }
                }

                policies[row.PolicyNumber] = policy;
            }

            var claim = Claim.Rehydrate(
                Guid.NewGuid(),
                policy.Id,
                row.ClaimReference,
                row.ClaimedAmount,
                row.Currency,
                Math.Max(row.ReserveAmount, 0m),
                0m,
                status,
                row.Adjuster,
                1,
                row.CreatedAt);
            store.AddClaim(claim);
            importedReferences.Add(row.ClaimReference.Trim().ToUpperInvariant());
        }

        await store.SaveChangesAsync(cancellationToken);
        return new LegacyImportReport(
            sourceRows.Count,
            importedReferences.Count,
            Checksum(sourceRows.Select(row => $"{row.LegacyClaimId}|{row.ClaimReference}|{row.ClaimedAmount}")),
            Checksum(importedReferences),
            rejections);
    }

    private static string Checksum(IEnumerable<string> values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", values.OrderBy(value => value, StringComparer.Ordinal)))));
}
