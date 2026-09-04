using JobScheduler.Application.Abstractions;

namespace JobScheduler.Infrastructure.Execution;

/// <summary>
/// The allow-list of executable handlers, built from the handlers registered in DI. A job whose
/// handler type is not present here is rejected at definition time and never executed — this is the
/// control that prevents arbitrary code/shell execution from a job payload.
/// </summary>
public sealed class HandlerRegistry : IHandlerRegistry
{
    private readonly Dictionary<string, IJobHandler> _handlers;

    public HandlerRegistry(IEnumerable<IJobHandler> handlers)
    {
        _handlers = handlers.ToDictionary(h => h.HandlerType, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsRegistered(string handlerType) =>
        !string.IsNullOrWhiteSpace(handlerType) && _handlers.ContainsKey(handlerType);

    public IJobHandler Resolve(string handlerType) =>
        _handlers.TryGetValue(handlerType, out var handler)
            ? handler
            : throw new HandlerNotRegisteredException(handlerType);

    public IReadOnlyCollection<string> RegisteredTypes => _handlers.Keys.OrderBy(k => k).ToList();
}
