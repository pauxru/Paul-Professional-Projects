using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Iiot.Domain;

namespace Iiot.EdgeGateway;

/// <summary>
/// Production-facing adapter. It groups batches per credential and uses gzip; the in-process sink
/// used by tests exercises the same receipt and retry contract without an external service.
/// </summary>
public sealed class HttpCloudTelemetrySink(
    HttpClient client,
    Func<string, string> deviceKeyResolver) : ICloudTelemetrySink
{
    public async Task<CloudIngestionReceipt> IngestAsync(IReadOnlyList<TelemetryReading> readings, CancellationToken cancellationToken = default)
    {
        var accepted = 0;
        var duplicates = 0;
        var rejected = 0;
        foreach (var group in readings.GroupBy(item => item.DeviceId))
        {
            var compressed = EdgeCompression.CompressJson(group.ToArray());
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/telemetry")
            {
                Content = new ByteArrayContent(compressed)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Content.Headers.ContentEncoding.Add("gzip");
            request.Headers.Add("X-Device-Id", group.Key);
            request.Headers.Add("X-Device-Key", deviceKeyResolver(group.Key));
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Cloud ingestion returned {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var receipt = JsonSerializer.Deserialize<CloudIngestionReceipt>(body, EdgeJson.Options)
                ?? throw new InvalidDataException("Cloud ingestion returned an empty receipt.");
            accepted += receipt.Accepted;
            duplicates += receipt.Duplicates;
            rejected += receipt.Rejected;
        }

        return new CloudIngestionReceipt(accepted, duplicates, rejected);
    }
}
