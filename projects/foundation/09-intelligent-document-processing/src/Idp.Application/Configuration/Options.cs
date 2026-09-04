using System.ComponentModel.DataAnnotations;

namespace Idp.Application.Configuration;

/// <summary>Confidence thresholds that drive the auto-approve / review / reject routing.</summary>
public sealed class PipelineOptions
{
    public const string SectionName = "Pipeline";

    /// <summary>Document confidence at or above which a clean document is auto-approved.</summary>
    [Range(0, 1)] public double AutoApproveThreshold { get; set; } = 0.85;

    /// <summary>Document confidence below which a document is auto-rejected outright.</summary>
    [Range(0, 1)] public double RejectThreshold { get; set; } = 0.30;

    /// <summary>Auto-export documents that reach AutoApproved without a human step.</summary>
    public bool AutoExportOnApprove { get; set; } = true;
}

public sealed class ValidationOptions
{
    public const string SectionName = "Validation";

    public decimal ArithmeticAbsoluteTolerance { get; set; } = 0.02m;
    public decimal ArithmeticRelativeTolerance { get; set; } = 0.01m;
    public decimal QuantityVariancePercent { get; set; } = 0.05m;
    public decimal PriceVariancePercent { get; set; } = 0.05m;
    [Range(0, 1)] public double SupplierMatchThreshold { get; set; } = 0.86;
}

public sealed class ReviewOptions
{
    public const string SectionName = "Review";

    [Range(1, 1440)] public int ClaimLeaseMinutes { get; set; } = 30;
    [Range(1, 720)] public int SlaHours { get; set; } = 24;
}

public sealed class ExportOptions
{
    public const string SectionName = "Export";

    [Range(1, 10)] public int MaxAttempts { get; set; } = 3;
    public string Format { get; set; } = "Json";
    public string OutboxPath { get; set; } = "artifacts/outbox";
    public string DeadLetterPath { get; set; } = "artifacts/deadletter";

    /// <summary>Simulated ERP transient-failure probability [0,1] used only by the local simulator.</summary>
    [Range(0, 1)] public double SimulatedFailureRate { get; set; } = 0.0;
    public string ErpEndpoint { get; set; } = "http://localhost:5009/simulated-erp/invoices";
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string Provider { get; set; } = "FileSystem";
    public string RootPath { get; set; } = "artifacts/object-store";
}

public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    [Range(1024, 104857600)] public long MaxUploadBytes { get; set; } = 5 * 1024 * 1024;
    public string[] AllowedContentTypes { get; set; } =
    {
        "text/plain", "text/csv", "application/json",
        "application/vnd.idp.ocr+json", "application/octet-stream"
    };
    public string[] AllowedExtensions { get; set; } = { ".txt", ".csv", ".json", ".ocr.json" };
    public bool DropFolderEnabled { get; set; } = false;
    public string DropFolderPath { get; set; } = "artifacts/drop";
}

public sealed class ClassifierOptions
{
    public const string SectionName = "Classifier";

    /// <summary>"Rules" (default, deterministic) or "Llm" (optional, never used by default).</summary>
    public string Provider { get; set; } = "Rules";
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required] public string Issuer { get; set; } = "idp-dev";
    [Required] public string Audience { get; set; } = "idp-api";
    [Required, MinLength(16)] public string SigningKey { get; set; } =
        "dev-only-not-a-real-secret-change-me-0123456789";
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public string Provider { get; set; } = "Sqlite";
    public string ConnectionString { get; set; } = "Data Source=idp.db";
}
