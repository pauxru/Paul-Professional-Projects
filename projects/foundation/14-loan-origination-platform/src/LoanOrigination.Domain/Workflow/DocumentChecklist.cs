using LoanOrigination.Domain.Models;

namespace LoanOrigination.Domain.Workflow;

public sealed record DocumentChecklistItem(
    string DocumentType,
    bool Required,
    bool Uploaded,
    bool Verified,
    bool Expired,
    string Status);

public static class DocumentChecklist
{
    private static readonly IReadOnlySet<string> PermittedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/jpeg",
        "image/png"
    };

    public static void ValidateUpload(ProductDocumentRequirement requirement, string contentType, long length)
    {
        if (!PermittedContentTypes.Contains(contentType))
        {
            throw new DomainException("Only PDF, JPEG, and PNG documents are accepted.");
        }

        if (length <= 0 || length > requirement.MaximumSizeBytes)
        {
            throw new DomainException($"Document size must be between 1 and {requirement.MaximumSizeBytes} bytes.");
        }
    }

    public static IReadOnlyList<DocumentChecklistItem> Build(
        LoanProductVersion product,
        IReadOnlyList<LoanDocument> documents,
        DateOnly today,
        IEnumerable<string>? additionalRequiredDocuments = null)
    {
        var required = product.RequiredDocuments
            .Concat((additionalRequiredDocuments ?? []).Select(type =>
                new ProductDocumentRequirement(type, true, 10_000_000)))
            .GroupBy(requirement => requirement.DocumentType, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(requirement => requirement.DocumentType, StringComparer.OrdinalIgnoreCase);

        return required.Select(requirement =>
        {
            var document = documents
                .Where(candidate => string.Equals(candidate.DocumentType, requirement.DocumentType, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => candidate.UploadedAt)
                .FirstOrDefault();
            var expired = document?.ExpiresOn is { } expiry && expiry < today;
            var verified = document is not null && document.IsVerifiedAndCurrent(today);
            var status = document is null
                ? "Missing"
                : expired
                    ? "Expired"
                    : document.VerificationStatus.ToString();
            return new DocumentChecklistItem(requirement.DocumentType, requirement.Required, document is not null, verified, expired, status);
        }).ToArray();
    }

    public static bool MandatoryDocumentsVerified(
        LoanProductVersion product,
        IReadOnlyList<LoanDocument> documents,
        DateOnly today,
        IEnumerable<string>? additionalRequiredDocuments = null) =>
        Build(product, documents, today, additionalRequiredDocuments)
            .Where(item => item.Required)
            .All(item => item.Verified);
}
