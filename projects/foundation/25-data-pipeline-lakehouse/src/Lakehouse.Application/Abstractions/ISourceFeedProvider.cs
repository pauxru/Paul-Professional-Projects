using Lakehouse.Domain.Cdc;

namespace Lakehouse.Application.Abstractions;

/// <summary>
/// Supplies the source change feed to the ingestion layer. Implemented in Infrastructure by the
/// synthetic Contoso generator; abstracted here so the pipeline and tests do not depend on it directly.
/// </summary>
public interface ISourceFeedProvider
{
    IReadOnlyList<ChangeEvent> Load();
}
