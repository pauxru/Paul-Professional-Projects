using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Iiot.Application;
using Iiot.Domain;
using Iiot.Protocol;
using Microsoft.Data.Sqlite;

namespace Iiot.EdgeGateway;

public sealed record CloudIngestionReceipt(int Accepted, int Duplicates, int Rejected)
{
    public bool IsCompleteFor(int count) => Rejected == 0 && Accepted + Duplicates == count;
}

public interface ICloudTelemetrySink
{
    Task<CloudIngestionReceipt> IngestAsync(IReadOnlyList<TelemetryReading> readings, CancellationToken cancellationToken = default);
}

public sealed record BufferedTelemetry(long Id, TelemetryReading Reading);

public sealed class SqliteEdgeBuffer
{
    private readonly string _connectionString;
    private readonly int _capacity;

    public SqliteEdgeBuffer(string databasePath, int capacity = 10_000)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        // The edge buffer opens short-lived connections; disabling pooling releases a removable
        // field database immediately during maintenance or a gateway restart on Windows.
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        _capacity = capacity;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS edge_queue (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                device_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                received_at TEXT NOT NULL,
                payload TEXT NOT NULL,
                UNIQUE(device_id, sequence)
            );
            CREATE INDEX IF NOT EXISTS ix_edge_queue_order ON edge_queue(id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> EnqueueAsync(TelemetryReading reading, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO edge_queue(device_id, sequence, received_at, payload)
                VALUES($deviceId, $sequence, $receivedAt, $payload);
                """;
            insert.Parameters.AddWithValue("$deviceId", reading.DeviceId);
            insert.Parameters.AddWithValue("$sequence", reading.Sequence);
            insert.Parameters.AddWithValue("$receivedAt", reading.DeviceTimestamp.ToString("O"));
            insert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(reading, EdgeJson.Options));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        int count;
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.Transaction = transaction;
            countCommand.CommandText = "SELECT COUNT(*) FROM edge_queue;";
            count = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        }

        var excess = Math.Max(0, count - _capacity);
        if (excess > 0)
        {
            await using var evict = connection.CreateCommand();
            evict.Transaction = transaction;
            evict.CommandText = "DELETE FROM edge_queue WHERE id IN (SELECT id FROM edge_queue ORDER BY id LIMIT $count);";
            evict.Parameters.AddWithValue("$count", excess);
            await evict.ExecuteNonQueryAsync(cancellationToken);
            count -= excess;
        }

        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    public async Task<IReadOnlyList<BufferedTelemetry>> ReadBatchAsync(int maximum, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, payload FROM edge_queue ORDER BY id LIMIT $maximum;";
        command.Parameters.AddWithValue("$maximum", maximum);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<BufferedTelemetry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var reading = JsonSerializer.Deserialize<TelemetryReading>(reader.GetString(1), EdgeJson.Options)
                ?? throw new InvalidDataException("The edge buffer contains unreadable telemetry.");
            result.Add(new BufferedTelemetry(reader.GetInt64(0), reading));
        }

        return result;
    }

    public async Task RemoveAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var parameterNames = ids.Select((_, index) => $"$id{index}").ToArray();
        command.CommandText = $"DELETE FROM edge_queue WHERE id IN ({string.Join(",", parameterNames)});";
        for (var index = 0; index < ids.Count; index++)
        {
            command.Parameters.AddWithValue(parameterNames[index], ids[index]);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM edge_queue;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }
}

public sealed class EdgeGateway : IAsyncDisposable
{
    private readonly SqliteEdgeBuffer _buffer;
    private readonly ICloudTelemetrySink _cloud;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly LocalRuleEvaluator _localRules;
    private readonly List<IAsyncDisposable> _subscriptions = [];

    public EdgeGateway(SqliteEdgeBuffer buffer, ICloudTelemetrySink cloud, LocalRuleEvaluator? localRules = null)
    {
        _buffer = buffer;
        _cloud = cloud;
        _localRules = localRules ?? new LocalRuleEvaluator([]);
    }

    public bool IsCloudReachable { get; private set; } = true;
    public event Func<Alert, Task>? LocalAlertRaised;
    public event Func<TransportMessage, Task>? CommandRelayed;

    public void SetCloudReachable(bool reachable) => IsCloudReachable = reachable;

    public async Task StartAsync(IMessageTransport transport, CancellationToken cancellationToken = default)
    {
        var telemetry = await transport.SubscribeAsync(
            "plants/+/devices/+/telemetry",
            async message =>
            {
                var reading = JsonSerializer.Deserialize<TelemetryReading>(message.Payload, EdgeJson.Options);
                if (reading is not null)
                {
                    await HandleTelemetryAsync(reading);
                }
            },
            cancellationToken);
        var commands = await transport.SubscribeAsync(
            "plants/+/devices/+/commands/#",
            async message =>
            {
                if (CommandRelayed is { } relay)
                {
                    await relay(message);
                }
            },
            cancellationToken);
        _subscriptions.Add(telemetry);
        _subscriptions.Add(commands);
    }

    public async Task HandleTelemetryAsync(TelemetryReading reading, CancellationToken cancellationToken = default)
    {
        var alert = _localRules.Evaluate(reading);
        if (alert is not null && LocalAlertRaised is { } raised)
        {
            await raised(alert);
        }

        await _serial.WaitAsync(cancellationToken);
        try
        {
            if (!IsCloudReachable)
            {
                await _buffer.EnqueueAsync(reading, cancellationToken);
                return;
            }

            if (await _buffer.CountAsync(cancellationToken) > 0)
            {
                await _buffer.EnqueueAsync(reading, cancellationToken);
                await FlushCoreAsync(cancellationToken);
                return;
            }

            try
            {
                var receipt = await _cloud.IngestAsync([reading], cancellationToken);
                if (!receipt.IsCompleteFor(1))
                {
                    IsCloudReachable = false;
                    await _buffer.EnqueueAsync(reading, cancellationToken);
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                IsCloudReachable = false;
                await _buffer.EnqueueAsync(reading, cancellationToken);
            }
        }
        finally
        {
            _serial.Release();
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (!IsCloudReachable)
        {
            return;
        }

        await _serial.WaitAsync(cancellationToken);
        try
        {
            await FlushCoreAsync(cancellationToken);
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task FlushCoreAsync(CancellationToken cancellationToken)
    {
        while (IsCloudReachable)
        {
            var batch = await _buffer.ReadBatchAsync(100, cancellationToken);
            if (batch.Count == 0)
            {
                return;
            }

            try
            {
                var receipt = await _cloud.IngestAsync(batch.Select(item => item.Reading).ToArray(), cancellationToken);
                if (!receipt.IsCompleteFor(batch.Count))
                {
                    IsCloudReachable = false;
                    return;
                }

                await _buffer.RemoveAsync(batch.Select(item => item.Id).ToArray(), cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                IsCloudReachable = false;
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var subscription in _subscriptions)
        {
            await subscription.DisposeAsync();
        }

        _serial.Dispose();
    }
}

public sealed class LocalRuleEvaluator
{
    private readonly IReadOnlyList<RuleDefinition> _rules;
    private readonly RuleEngine _engine = new();
    private readonly AlertManager _alerts = new();
    private readonly Dictionary<string, List<TelemetryReading>> _history = new(StringComparer.Ordinal);

    public LocalRuleEvaluator(IReadOnlyList<RuleDefinition> rules)
    {
        _rules = rules;
    }

    public Alert? Evaluate(TelemetryReading reading)
    {
        if (!_history.TryGetValue(reading.DeviceId, out var history))
        {
            history = [];
            _history[reading.DeviceId] = history;
        }

        history.Add(reading);
        if (history.Count > 300)
        {
            history.RemoveRange(0, history.Count - 300);
        }

        Alert? last = null;
        foreach (var rule in _rules.Where(rule => rule.DeviceId == reading.DeviceId))
        {
            var outcome = _engine.Evaluate(rule, reading, history, reading.DeviceTimestamp);
            last = _alerts.Apply(rule, outcome, reading.DeviceTimestamp, () => Guid.NewGuid().ToString("N")) ?? last;
        }

        return last;
    }
}

public static class EdgeCompression
{
    public static byte[] CompressJson<T>(T value)
    {
        var input = JsonSerializer.SerializeToUtf8Bytes(value, EdgeJson.Options);
        using var destination = new MemoryStream();
        using (var gzip = new GZipStream(destination, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(input);
        }

        return destination.ToArray();
    }
}

internal static class EdgeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}
