using Idp.Application.Documents;
using Idp.Application.Suppliers;
using Idp.Domain.Suppliers;
using Microsoft.Extensions.Logging;

namespace Idp.Infrastructure.Generation;

/// <summary>
/// Seeds supplier master data and a deterministic synthetic corpus by running each generated document
/// through the real ingestion pipeline. Purchase orders and delivery notes are ingested before the
/// invoices that reference them, so three-way matching has data to match against. Idempotent: it does
/// nothing if suppliers already exist.
/// </summary>
public sealed class DemoDataSeeder
{
    private readonly ISupplierRepository _suppliers;
    private readonly DocumentGenerator _generator;
    private readonly DocumentIntakeService _intake;
    private readonly Application.Abstractions.IUnitOfWork _unitOfWork;
    private readonly Application.Abstractions.IClock _clock;
    private readonly ILogger<DemoDataSeeder> _logger;

    public DemoDataSeeder(
        ISupplierRepository suppliers,
        DocumentGenerator generator,
        DocumentIntakeService intake,
        Application.Abstractions.IUnitOfWork unitOfWork,
        Application.Abstractions.IClock clock,
        ILogger<DemoDataSeeder> logger)
    {
        _suppliers = suppliers;
        _generator = generator;
        _intake = intake;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _logger = logger;
    }

    public async Task<int> SeedAsync(CancellationToken ct = default)
    {
        var existing = await _suppliers.GetAllAsync(ct);
        if (existing.Count > 0)
        {
            _logger.LogInformation("Seed skipped: {Count} suppliers already present.", existing.Count);
            return 0;
        }

        foreach (var ms in _generator.Suppliers)
        {
            _suppliers.Add(new Supplier(
                ms.Name, ms.TaxId, ms.Currency, _clock.UtcNow, ms.Aliases, ms.BankAccount));
        }
        await _unitOfWork.SaveChangesAsync(ct);

        var count = 0;
        foreach (var doc in _generator.GenerateCorpus())
        {
            var correlationId = Guid.NewGuid().ToString("N");
            await _intake.IngestAsync(
                doc.FileName, doc.ContentType, doc.Bytes, correlationId, "seed", ct);
            count++;
        }

        _logger.LogInformation("Seeded {Suppliers} suppliers and {Docs} documents.",
            _generator.Suppliers.Count, count);
        return count;
    }
}
