using System.Diagnostics;
using Lab.Diagnostics.Measurement;
using Microsoft.Data.Sqlite;

namespace Lab.Scenarios.Incidents;

public sealed class MissingIndexScenario : IIncidentScenario
{
    public string Id => "INC-002";

    public string Name => "Missing database index";

    public async Task<ScenarioReport> RunAsync(ScenarioRunOptions options, CancellationToken cancellationToken)
    {
        var rowCount = options.BoundedRequests(2, 200) * 1_000;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.Timeout);
        var token = budget.Token;
        var session = new MeasurementSession();

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(token);
        await ExecuteAsync(connection, """
            CREATE TABLE CargoEvents (
                Id INTEGER PRIMARY KEY,
                LookupCode TEXT NOT NULL,
                Payload TEXT NOT NULL
            );
            """, token);

        await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token))
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO CargoEvents (Id, LookupCode, Payload) VALUES ($id, $lookupCode, $payload);";
            var id = insert.CreateParameter();
            id.ParameterName = "$id";
            insert.Parameters.Add(id);
            var lookupCode = insert.CreateParameter();
            lookupCode.ParameterName = "$lookupCode";
            insert.Parameters.Add(lookupCode);
            var payload = insert.CreateParameter();
            payload.ParameterName = "$payload";
            insert.Parameters.Add(payload);

            for (var index = 1; index <= rowCount; index++)
            {
                id.Value = index;
                lookupCode.Value = index == rowCount - 7 ? "TARGET-ORDER-8675309" : $"ORDER-{index:D7}";
                payload.Value = $"Synthetic cargo event {index}";
                await insert.ExecuteNonQueryAsync(token);
            }

            await transaction.CommitAsync(token);
        }

        if (options.Mode == ScenarioMode.Fixed)
        {
            await ExecuteAsync(connection, "CREATE INDEX IX_CargoEvents_LookupCode ON CargoEvents (LookupCode);", token);
        }

        var queryPlan = await ExplainPlanAsync(connection, token);
        var latencies = new LatencyHistogram();
        var rowsFound = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, Payload FROM CargoEvents WHERE LookupCode = $lookupCode;";
            command.Parameters.AddWithValue("$lookupCode", "TARGET-ORDER-8675309");
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                rowsFound++;
            }

            stopwatch.Stop();
            latencies.Record(stopwatch.Elapsed);
        }

        var latency = latencies.Snapshot();
        var outcome = session.Complete();
        return new ScenarioReport
        {
            ScenarioId = Id,
            ScenarioName = Name,
            Mode = options.Mode,
            RequestedOperations = rowCount,
            StartedAtUtc = DateTimeOffset.UtcNow,
            ElapsedMilliseconds = outcome.Elapsed.TotalMilliseconds,
            Metrics = new Dictionary<string, object?>
            {
                ["seededRows"] = rowCount,
                ["rowsFoundAcrossFiveQueries"] = rowsFound,
                ["queryP50Milliseconds"] = Math.Round(latency.P50Milliseconds, 3),
                ["queryP95Milliseconds"] = Math.Round(latency.P95Milliseconds, 3),
                ["sqliteQueryPlan"] = queryPlan,
                ["indexCreated"] = options.Mode == ScenarioMode.Fixed
            },
            Evidence =
            [
                "SQLite EXPLAIN QUERY PLAN was captured from the same connection and SQL predicate used for timing.",
                $"Plan: {queryPlan}"
            ],
            Limitations =
            [
                "SQLite query plans demonstrate scan versus index selection; production cardinality estimates and I/O behaviour differ by database engine."
            ]
        };
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> ExplainPlanAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT Id, Payload FROM CargoEvents WHERE LookupCode = $lookupCode;";
        command.Parameters.AddWithValue("$lookupCode", "TARGET-ORDER-8675309");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var parts = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            parts.Add(reader.GetString(3));
        }

        return string.Join(Environment.NewLine, parts);
    }
}
