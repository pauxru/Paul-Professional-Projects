using Lakehouse.Application.Abstractions;
using Lakehouse.Domain.Cdc;

namespace Lakehouse.Infrastructure.Sources;

/// <summary>
/// Adapts the synthetic <see cref="ContosoSourceGenerator"/> to the <see cref="ISourceFeedProvider"/>
/// port. The feed is generated once (deterministically) and cached for the process lifetime.
/// </summary>
public sealed class ContosoFeedProvider : ISourceFeedProvider
{
    private readonly Lazy<IReadOnlyList<ChangeEvent>> _feed;

    public ContosoFeedProvider(GeneratorOptions? options = null)
        => _feed = new Lazy<IReadOnlyList<ChangeEvent>>(() => new ContosoSourceGenerator(options).Generate().Events);

    public IReadOnlyList<ChangeEvent> Load() => _feed.Value;
}
