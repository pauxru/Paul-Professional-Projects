using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Northstar.Secrets.Application;

namespace Northstar.Secrets.Infrastructure.Security;

public sealed class SecretRedactionRegistry : ISecretRedactionRegistry
{
    private readonly ConcurrentDictionary<string, byte> _values = new(StringComparer.Ordinal);

    public void Register(string secretValue)
    {
        if (!string.IsNullOrWhiteSpace(secretValue) && secretValue.Length >= 4)
        {
            _values.TryAdd(secretValue, 0);
        }
    }

    public string Redact(string value)
    {
        var redacted = value;
        foreach (var secret in _values.Keys.OrderByDescending(x => x.Length))
        {
            redacted = redacted.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        return redacted;
    }
}

public interface IRedactedLogSink
{
    void Write(LogLevel level, string category, string message);
}

public sealed class ConsoleRedactedLogSink : IRedactedLogSink
{
    public void Write(LogLevel level, string category, string message) =>
        Console.WriteLine($"{DateTimeOffset.UtcNow:O} [{level}] {category}: {message}");
}

public sealed class InMemoryRedactedLogSink : IRedactedLogSink
{
    private readonly ConcurrentQueue<string> _messages = new();
    public IReadOnlyCollection<string> Messages => _messages.ToArray();

    public void Write(LogLevel level, string category, string message) =>
        _messages.Enqueue($"{level}|{category}|{message}");
}

public sealed class RedactingLoggerProvider(
    ISecretRedactionRegistry redactionRegistry,
    IRedactedLogSink sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) =>
        new RedactingLogger(categoryName, redactionRegistry, sink);

    public void Dispose() { }

    private sealed class RedactingLogger(
        string category,
        ISecretRedactionRegistry redactionRegistry,
        IRedactedLogSink sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            NoopScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var rendered = formatter(state, exception);
            if (exception is not null)
            {
                rendered = $"{rendered} {exception}";
            }

            sink.Write(logLevel, category, redactionRegistry.Redact(rendered));
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static NoopScope Instance { get; } = new();
        public void Dispose() { }
    }
}
