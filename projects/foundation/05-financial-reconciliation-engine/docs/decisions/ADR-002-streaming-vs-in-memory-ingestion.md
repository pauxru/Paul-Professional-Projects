# ADR-002: Streaming vs In-Memory Ingestion

## Status

Accepted, dated 2026-09.

## Context

ReconEngine imports internal CSV, external CSV, and external fixed-width settlement files through `ImportService.ImportAsync`. The service selects an `IRowTokenizer`, wraps the input stream in `HashingReadStream`, maps rows through `RecordMapper`, captures row-level `ImportRejection` records, and persists an `ImportBatch` with `TotalRows`, `AcceptedRows`, `RejectedRows`, and a raw-byte SHA-256 `FileChecksum`.

The infrastructure tokenizers are streaming `IAsyncEnumerable<TokenizedRow>` implementations. `CsvRowTokenizer` reads fixed-size character buffers and supports RFC-4180-style quoted commas, escaped quotes, embedded newlines, BOM, CRLF/LF/CR line endings, and blank-line skipping. `FixedWidthRowTokenizer` reads one physical line at a time and lets the mapper slice fields by configured start/length.

Measured performance on this host shows streaming CSV parse and normalisation of 100,000 rows in 649.1 ms, or 154,058 rows/s. The reconciliation hot path handled 250,000 pairs / 500,000 rows in 2,165.5 ms at 545.5 MB peak working set.

## Decision

Use streaming tokenizers based on `IAsyncEnumerable<TokenizedRow>` for all supported ingestion formats. A 250k-row file must not be fully materialised as raw text or a full intermediate table before parsing. `ImportService` may accumulate mapped `ReconRecord` entities for persistence, but tokenization, line handling, checksum calculation, and rejection detection proceed row-by-row from the stream.

## Options Considered

1. Streaming tokenizers with row-by-row mapping.
   - Pros: bounded raw-file memory usage; checksum can be computed while reading; row-level rejection line numbers are natural; supports large files and embedded CSV newlines.
   - Cons: parser code is more complex; one-pass processing makes random access and whole-file validation harder.
2. Load the entire file into memory, then split and parse.
   - Pros: simple implementation; easier debugging; convenient for small demo files.
   - Cons: high memory pressure for 250k+ rows; incorrect for embedded newlines unless a full CSV parser is still used; delays first rejection until after full read.
3. Database bulk-load raw staging table first.
   - Pros: can leverage database indexing and restartable stages; useful for very large production ETL.
   - Cons: more infrastructure and schema; conflicts with the zero-external-infra default; still needs robust tokenization before staging.
4. Third-party CSV library only.
   - Pros: less parser maintenance; mature edge-case handling.
   - Cons: fixed-width still needs custom handling; current verified performance and behaviours are already implemented in source.

## Consequences

Positive consequences:

- Large files are parsed without holding the whole raw file in memory.
- `ImportBatch.FileChecksum` reflects raw bytes as they pass through `HashingReadStream`.
- Row-level failures become `ImportRejection` rows with line number, reason, and truncated raw line.
- The ingestion design matches existing perf evidence: 100,000 CSV rows parse+normalise at 154,058 rows/s.

Negative consequences:

- Streaming parser state machines are more error-prone than simple `ReadAllText` code.
- The current `ImportService` still builds a `List<ReconRecord>` before store insertion, so persistence memory is not fully streaming.
- Reprocessing a row after a later validation failure requires reopening the source or storing enough context.

## Risks

- Risk: subtle CSV state bugs around quotes and embedded newlines. Mitigation: keep parser tests for BOM, quoted delimiters, escaped quotes, blank lines, and line endings.
- Risk: memory still grows with accepted records because store insertion is batched after parsing. Mitigation: introduce chunked `AddRecordsAsync` later if import persistence, not tokenization, becomes the bottleneck.
- Risk: back-pressure and cancellation are mishandled. Mitigation: keep `CancellationToken` on `TokenizeAsync` and `ImportAsync`.

## Alternatives

A reviewer might expect an in-memory import because the engine is a modular monolith using SQLite by default. That was not chosen because settlement files are explicitly sized in the hundreds of thousands of rows, and the verified design already reconciles 250,000 pairs / 500,000 rows with a measured 545.5 MB peak working set. Loading entire raw files would consume memory without adding audit value.
