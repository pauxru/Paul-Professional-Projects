using Idp.Application.Configuration;
using Idp.Application.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Idp.Infrastructure.Ingestion;

/// <summary>
/// Background drop-folder watcher: files copied into the configured folder are ingested through the
/// same pipeline as HTTP uploads, then moved to a <c>processed</c> subfolder. Disabled by default
/// (<c>Ingestion:DropFolderEnabled=false</c>); it polls rather than relying on OS file events so it
/// behaves consistently across platforms.
/// </summary>
public sealed class DropFolderWatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IngestionOptions _options;
    private readonly ILogger<DropFolderWatcher> _logger;

    public DropFolderWatcher(
        IServiceScopeFactory scopeFactory,
        IOptions<IngestionOptions> options,
        ILogger<DropFolderWatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.DropFolderEnabled)
        {
            _logger.LogInformation("Drop-folder watcher disabled.");
            return;
        }

        var root = Path.GetFullPath(_options.DropFolderPath);
        var processed = Path.Combine(root, "processed");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(processed);
        _logger.LogInformation("Drop-folder watcher polling {Root}", root);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root))
                {
                    await ProcessFileAsync(file, processed, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Drop-folder polling error.");
            }

            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }

    private async Task ProcessFileAsync(string file, string processed, CancellationToken ct)
    {
        var name = Path.GetFileName(file);
        try
        {
            var bytes = await File.ReadAllBytesAsync(file, ct);
            var contentType = name.EndsWith(".ocr.json", StringComparison.OrdinalIgnoreCase)
                ? "application/vnd.idp.ocr+json"
                : name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                    ? "text/csv"
                    : "text/plain";

            using var scope = _scopeFactory.CreateScope();
            var intake = scope.ServiceProvider.GetRequiredService<DocumentIntakeService>();
            var correlationId = Guid.NewGuid().ToString("N");
            await intake.IngestAsync(name, contentType, bytes, correlationId, "drop-folder", ct);

            File.Move(file, Path.Combine(processed, name), overwrite: true);
            _logger.LogInformation("Ingested dropped file {Name}", name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ingest dropped file {Name}", name);
        }
    }
}
