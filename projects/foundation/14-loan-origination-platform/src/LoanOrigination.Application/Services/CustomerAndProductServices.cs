using LoanOrigination.Application.Contracts;
using LoanOrigination.Application.Ports;
using LoanOrigination.Domain.Models;

namespace LoanOrigination.Application.Services;

public sealed class CustomerService(ILoanRepository repository, IClock clock, IAuditWriter auditWriter)
{
    public async Task<CustomerProfile> CreateAsync(
        CreateCustomerRequest request,
        string actor,
        string correlationId,
        string sourceIp,
        string userAgent,
        CancellationToken cancellationToken)
    {
        ValidateCustomer(request);
        var existing = await repository.ListCustomersAsync(cancellationToken);
        var duplicate = existing.FirstOrDefault(customer => IsPotentialDuplicate(customer, request));
        if (duplicate is not null)
        {
            throw new DuplicateCustomerException(duplicate.Id, duplicate.LegalName);
        }

        var customer = new CustomerProfile(
            Guid.NewGuid(),
            request.Kind,
            request.LegalName.Trim(),
            request.DateOfBirth,
            request.RegistrationNumber?.Trim(),
            request.SyntheticIdentityNumber.Trim(),
            new ContactDetails(request.Email.Trim(), request.Phone.Trim(), request.Address.Trim()),
            new IncomeDeclaration(request.MonthlyNetIncome, request.MonthlyExpenses, request.IncomeStabilityMonths, request.IncomeSource.Trim()),
            request.ExistingObligations ?? [],
            request.Dependants,
            KycStatus.NotStarted,
            clock.UtcNow);
        await repository.AddCustomerAsync(customer, cancellationToken);
        await auditWriter.WriteAsync(actor, "customer.created", $"customers/{customer.Id}", null, customer, correlationId, sourceIp, userAgent, cancellationToken);
        return customer;
    }

    public async Task<PagedResult<CustomerProfile>> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var values = (await repository.ListCustomersAsync(cancellationToken))
            .OrderBy(customer => customer.LegalName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Pagination.Page(values, page, pageSize);
    }

    private static void ValidateCustomer(CreateCustomerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.LegalName) || string.IsNullOrWhiteSpace(request.SyntheticIdentityNumber))
        {
            throw new DomainException("Legal name and synthetic identity number are required.");
        }

        if (request.Kind == CustomerKind.Individual && !request.DateOfBirth.HasValue)
        {
            throw new DomainException("Date of birth is required for an individual.");
        }

        if (request.Kind == CustomerKind.Sme && string.IsNullOrWhiteSpace(request.RegistrationNumber))
        {
            throw new DomainException("Registration number is required for an SME.");
        }

        if (request.MonthlyNetIncome < 0m || request.MonthlyExpenses < 0m || request.Dependants < 0)
        {
            throw new DomainException("Income, expenses, and dependants cannot be negative.");
        }
    }

    private static bool IsPotentialDuplicate(CustomerProfile existing, CreateCustomerRequest candidate)
    {
        if (existing.Kind != candidate.Kind)
        {
            return false;
        }

        var identityMatch = existing.Kind == CustomerKind.Individual
            ? existing.DateOfBirth == candidate.DateOfBirth
            : string.Equals(existing.RegistrationNumber, candidate.RegistrationNumber, StringComparison.OrdinalIgnoreCase);
        if (!identityMatch)
        {
            return false;
        }

        return NameSimilarity(existing.LegalName, candidate.LegalName) >= 0.80m;
    }

    private static decimal NameSimilarity(string left, string right)
    {
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);
        if (normalizedLeft.Length == 0 || normalizedRight.Length == 0)
        {
            return 0m;
        }

        var distances = new int[normalizedRight.Length + 1];
        for (var index = 0; index <= normalizedRight.Length; index++)
        {
            distances[index] = index;
        }

        for (var i = 1; i <= normalizedLeft.Length; i++)
        {
            var priorDiagonal = distances[0];
            distances[0] = i;
            for (var j = 1; j <= normalizedRight.Length; j++)
            {
                var priorAbove = distances[j];
                var cost = normalizedLeft[i - 1] == normalizedRight[j - 1] ? 0 : 1;
                distances[j] = Math.Min(Math.Min(distances[j] + 1, distances[j - 1] + 1), priorDiagonal + cost);
                priorDiagonal = priorAbove;
            }
        }

        return 1m - ((decimal)distances[^1] / Math.Max(normalizedLeft.Length, normalizedRight.Length));
    }

    private static string Normalize(string value) => string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();
}

public sealed class ProductService(ILoanRepository repository, IClock clock, IAuditWriter auditWriter)
{
    public async Task<LoanProductVersion> CreateVersionAsync(
        CreateProductRequest request,
        string actor,
        string correlationId,
        string sourceIp,
        string userAgent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ProductCode) || request.MinimumPrincipal <= 0m ||
            request.MaximumPrincipal < request.MinimumPrincipal || request.MinimumTermMonths <= 0 ||
            request.MaximumTermMonths < request.MinimumTermMonths || request.AnnualInterestRate < 0m)
        {
            throw new DomainException("Product limits, term range, and rate are invalid.");
        }

        var ruleset = await repository.GetRulesetAsync(request.RulesetId, request.RulesetVersion, cancellationToken);
        if (ruleset is null)
        {
            throw new DomainException("Referenced ruleset version does not exist.");
        }

        var priorVersions = await repository.ListProductsAsync(cancellationToken);
        var version = priorVersions
            .Where(product => string.Equals(product.ProductCode, request.ProductCode, StringComparison.OrdinalIgnoreCase))
            .Select(product => product.Version)
            .DefaultIfEmpty(0)
            .Max() + 1;
        var product = new LoanProductVersion(
            Guid.NewGuid(),
            request.ProductCode.Trim().ToUpperInvariant(),
            version,
            request.Name.Trim(),
            request.MinimumPrincipal,
            request.MaximumPrincipal,
            request.MinimumTermMonths,
            request.MaximumTermMonths,
            request.AnnualInterestRate,
            request.InterestMethod,
            request.Currency.Trim().ToUpperInvariant(),
            request.RulesetId,
            request.RulesetVersion,
            request.Fees ?? [],
            request.RequiredDocuments ?? [],
            request.CollateralRequired,
            clock.UtcNow);
        await repository.AddProductAsync(product, cancellationToken);
        await auditWriter.WriteAsync(actor, "product.version-created", $"products/{product.ProductCode}/{product.Version}", null, product, correlationId, sourceIp, userAgent, cancellationToken);
        return product;
    }

    public async Task<PagedResult<LoanProductVersion>> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var values = (await repository.ListProductsAsync(cancellationToken))
            .OrderBy(product => product.ProductCode, StringComparer.Ordinal)
            .ThenByDescending(product => product.Version)
            .ToArray();
        return Pagination.Page(values, page, pageSize);
    }
}

public sealed class DuplicateCustomerException(Guid duplicateId, string duplicateName)
    : DomainException($"Potential duplicate customer '{duplicateName}' ({duplicateId}) was found.")
{
    public Guid DuplicateId { get; } = duplicateId;
}

internal static class Pagination
{
    public static PagedResult<T> Page<T>(IReadOnlyList<T> values, int page, int pageSize)
    {
        var boundedPage = Math.Max(1, page);
        var boundedPageSize = Math.Clamp(pageSize, 1, 100);
        var totalPages = Math.Max(1, (int)Math.Ceiling(values.Count / (decimal)boundedPageSize));
        return new PagedResult<T>(
            values.Skip((boundedPage - 1) * boundedPageSize).Take(boundedPageSize).ToArray(),
            boundedPage,
            boundedPageSize,
            values.Count,
            totalPages);
    }
}
