using Idp.Domain.Exports;

namespace Idp.Application.Exporting;

public interface IExportRepository
{
    void Add(ExportRecord record);
    Task<ExportRecord?> GetAsync(Guid id, CancellationToken ct = default);
    Task<ExportRecord?> GetByDocumentAsync(Guid documentId, CancellationToken ct = default);
    Task<IReadOnlyList<ExportRecord>> ListAsync(
        ExportStatus? status, CancellationToken ct = default);
}
