using System.Collections.Concurrent;
using System.Text.Json;
using LoanOrigination.Application.Ports;
using LoanOrigination.Domain.Models;
using LoanOrigination.Domain.Risk;
using LoanOrigination.Domain.Workflow;

namespace LoanOrigination.Infrastructure.Providers;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class LocalFileObjectStore(string rootPath) : IObjectStore
{
    private readonly string _rootPath = Path.GetFullPath(rootPath);

    public async Task<string> PutAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken)
    {
        var physicalPath = GetPhysicalPath(objectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(physicalPath)!);
        await using var output = new FileStream(physicalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(output, cancellationToken);
        return objectKey;
    }

    public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new FileStream(GetPhysicalPath(objectKey), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Task.FromResult(stream);
    }

    private string GetPhysicalPath(string objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey) || objectKey.Contains("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(objectKey))
        {
            throw new DomainException("Invalid object storage key.");
        }

        var path = Path.GetFullPath(Path.Combine(_rootPath, objectKey));
        if (!path.StartsWith(_rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("Object storage key escapes the configured root.");
        }

        return path;
    }
}

public sealed class DeterministicKycProvider : IKycProvider
{
    public Task<KycProviderResult> ScreenAsync(string syntheticIdentityNumber, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identity = syntheticIdentityNumber.ToLowerInvariant();
        var result = identity switch
        {
            var value when value.Contains("provider-timeout", StringComparison.Ordinal) =>
                new KycProviderResult(KycProviderOutcome.Timeout, $"kyc-{StableReference(identity)}", "Synthetic provider timeout; retry is allowed.", true),
            var value when value.Contains("sanctions-hit", StringComparison.Ordinal) =>
                new KycProviderResult(KycProviderOutcome.SanctionsHit, $"kyc-{StableReference(identity)}", "Synthetic sanctions screening hit.", false),
            var value when value.Contains("pep-hit", StringComparison.Ordinal) =>
                new KycProviderResult(KycProviderOutcome.PepHit, $"kyc-{StableReference(identity)}", "Synthetic PEP screening hit; enhanced due diligence required.", false),
            var value when value.Contains("refer", StringComparison.Ordinal) =>
                new KycProviderResult(KycProviderOutcome.Refer, $"kyc-{StableReference(identity)}", "Synthetic identity requires manual review.", false),
            var value when value.Contains("fail", StringComparison.Ordinal) =>
                new KycProviderResult(KycProviderOutcome.Fail, $"kyc-{StableReference(identity)}", "Synthetic identity verification failed.", false),
            _ => new KycProviderResult(KycProviderOutcome.Pass, $"kyc-{StableReference(identity)}", "Synthetic identity verification passed.", false)
        };
        return Task.FromResult(result);
    }

    private static string StableReference(string value) =>
        Math.Abs(StringComparer.Ordinal.GetHashCode(value)).ToString("X");
}

public sealed class DeterministicBureauProvider : IBureauProvider
{
    public Task<BureauReport> GetReportAsync(string syntheticIdentityNumber, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identity = syntheticIdentityNumber.ToLowerInvariant();
        var grade = identity switch
        {
            var value when value.Contains("bureau-a", StringComparison.Ordinal) => "A",
            var value when value.Contains("bureau-b", StringComparison.Ordinal) => "B",
            var value when value.Contains("bureau-d", StringComparison.Ordinal) => "D",
            var value when value.Contains("bureau-e", StringComparison.Ordinal) => "E",
            _ => "C"
        };
        var arrears = identity.Contains("arrears-3", StringComparison.Ordinal) ? 3 :
            identity.Contains("arrears-2", StringComparison.Ordinal) ? 2 :
            identity.Contains("arrears-1", StringComparison.Ordinal) ? 1 : 0;
        return Task.FromResult(new BureauReport(
            grade,
            arrears,
            arrears * 20_000m,
            $"Synthetic deterministic bureau grade {grade}, arrears count {arrears}."));
    }
}

public sealed class WeightedRiskScorer : IRiskScorer
{
    private readonly WeightedScorecard _scorecard = new();

    public RiskAssessment Score(ApplicantFacts facts, BureauReport bureau) => _scorecard.Evaluate(facts, bureau);
}

public sealed class DeterministicDisbursementProvider : IDisbursementProvider
{
    private readonly ConcurrentDictionary<string, ProviderDisbursementResult> _responses = new(StringComparer.Ordinal);

    public Task<ProviderDisbursementResult> SendAsync(
        string providerReference,
        decimal amount,
        string rail,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(providerReference) || amount <= 0m)
        {
            throw new DomainException("A provider reference and positive amount are required.");
        }

        var response = _responses.GetOrAdd(providerReference, reference =>
        {
            var normalized = reference.ToLowerInvariant();
            if (normalized.Contains("fail", StringComparison.Ordinal))
            {
                return new ProviderDisbursementResult(reference, DisbursementStatus.Failed, 0m, "Synthetic rail failure.");
            }

            if (normalized.Contains("pending", StringComparison.Ordinal))
            {
                return new ProviderDisbursementResult(reference, DisbursementStatus.Pending, 0m, "Synthetic asynchronous transfer pending.");
            }

            return new ProviderDisbursementResult(reference, DisbursementStatus.Succeeded, amount);
        });
        return Task.FromResult(response);
    }
}

public sealed class HashChainAuditWriter(ILoanRepository repository, IClock clock) : IAuditWriter
{
    public async Task WriteAsync(
        string actor,
        string action,
        string resource,
        object? before,
        object after,
        string correlationId,
        string sourceIp,
        string userAgent,
        CancellationToken cancellationToken)
    {
        var prior = (await repository.ListAuditsAsync(cancellationToken)).LastOrDefault();
        var beforePayload = before is null ? string.Empty : JsonSerializer.Serialize(before);
        var afterPayload = JsonSerializer.Serialize(after);
        var beforeHash = string.IsNullOrEmpty(beforePayload) ? null : AuditHashing.Hash(null, beforePayload);
        var previousHash = prior?.AfterHash ?? string.Empty;
        var afterHash = AuditHashing.Hash(previousHash, $"{action}|{resource}|{afterPayload}");
        var entry = new AuditEntry(
            Guid.NewGuid(),
            clock.UtcNow,
            actor,
            action,
            resource,
            correlationId,
            sourceIp,
            userAgent,
            beforeHash,
            afterHash,
            previousHash);
        await repository.AddAuditAsync(entry, cancellationToken);
    }
}
