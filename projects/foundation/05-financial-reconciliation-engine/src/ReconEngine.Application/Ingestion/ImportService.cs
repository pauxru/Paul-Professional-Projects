using ReconEngine.Application.Abstractions;
using ReconEngine.Application.Common;
using ReconEngine.Domain.Abstractions;
using ReconEngine.Domain.Entities;

namespace ReconEngine.Application.Ingestion;

public sealed record ImportResult(
    Guid BatchId,
    int TotalRows,
    int AcceptedRows,
    int RejectedRows,
    string FileChecksum);

/// <summary>
/// Streams a file through the appropriate tokenizer and the <see cref="RecordMapper"/>, capturing
/// row-level rejections with their line numbers, checksumming the bytes as they pass, and persisting
/// the accepted records plus an <see cref="ImportBatch"/> summary.
/// </summary>
public sealed class ImportService
{
    private const int MaxRejectionsStored = 1000;
    private readonly IReadOnlyList<IRowTokenizer> _tokenizers;
    private readonly IImportStore _store;
    private readonly IUnitOfWork _uow;
    private readonly IClock _clock;

    public ImportService(IEnumerable<IRowTokenizer> tokenizers, IImportStore store, IUnitOfWork uow, IClock clock)
    {
        _tokenizers = tokenizers.ToList();
        _store = store;
        _uow = uow;
        _clock = clock;
    }

    public async Task<ImportResult> ImportAsync(Stream stream, FileFormatProfile profile, string fileName, CancellationToken ct = default)
    {
        var tokenizer = _tokenizers.FirstOrDefault(t => t.Format == profile.Format)
                        ?? throw new InvalidOperationException($"No tokenizer registered for {profile.Format}.");

        var batch = new ImportBatch
        {
            Source = profile.Source,
            FileName = fileName,
            ProfileName = profile.Name,
            CreatedAtUtc = _clock.UtcNow,
        };

        var records = new List<ReconRecord>();
        await using var hashing = new HashingReadStream(stream);
        IReadOnlyDictionary<string, int>? header = null;
        var headerConsumed = !profile.HasHeader;
        var total = 0;
        var rejected = 0;

        await foreach (var row in tokenizer.TokenizeAsync(hashing, profile, ct))
        {
            if (row.IsBlank)
                continue;

            if (!headerConsumed)
            {
                header = BuildHeaderIndex(row);
                headerConsumed = true;
                continue;
            }

            total++;
            var result = RecordMapper.Map(row, profile, header, _clock);
            if (result.Ok)
            {
                result.Record!.ImportBatchId = batch.Id;
                records.Add(result.Record);
            }
            else
            {
                rejected++;
                if (batch.Rejections.Count < MaxRejectionsStored)
                {
                    batch.Rejections.Add(new ImportRejection
                    {
                        ImportBatchId = batch.Id,
                        LineNumber = row.LineNumber,
                        Reason = result.Error!,
                        RawLine = Truncate(row.RawLine, 512),
                    });
                }
            }
        }

        batch.TotalRows = total;
        batch.AcceptedRows = records.Count;
        batch.RejectedRows = rejected;
        batch.FileChecksum = hashing.GetHashHex();

        await _store.AddBatchAsync(batch, ct);
        await _store.AddRecordsAsync(records, ct);
        await _uow.SaveChangesAsync(ct);

        return new ImportResult(batch.Id, total, records.Count, rejected, batch.FileChecksum);
    }

    private static Dictionary<string, int> BuildHeaderIndex(TokenizedRow header)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Fields.Count; i++)
        {
            var name = header.Fields[i].Trim();
            if (!string.IsNullOrEmpty(name) && !map.ContainsKey(name))
                map[name] = i;
        }
        return map;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
