using Lakehouse.Infrastructure.Sources;

namespace Lakehouse.Api.Configuration;

/// <summary>Strongly-typed configuration for the lakehouse host, bound from the "Lakehouse" section.</summary>
public sealed class LakehouseOptions
{
    public string LakeRoot { get; set; } = "_data/lake";
    public string ServingDbPath { get; set; } = "_data/serving/serving.db";
    public bool SeedOnStartup { get; set; } = true;
    public GeneratorSettings Generator { get; set; } = new();
    public AuthSettings Auth { get; set; } = new();
}

/// <summary>Synthetic source-generator knobs (bound from config so tests can shrink the dataset).</summary>
public sealed class GeneratorSettings
{
    public int Seed { get; set; } = 42;
    public int Customers { get; set; } = 200;
    public int Products { get; set; } = 80;
    public int Orders { get; set; } = 3000;
    public int Sessions { get; set; } = 2000;
    public int Days { get; set; } = 90;
    public double DefectRate { get; set; } = 0.03;
    public double LateArrivalRate { get; set; } = 0.06;

    public GeneratorOptions ToGeneratorOptions() => new()
    {
        Seed = Seed,
        Customers = Customers,
        Products = Products,
        Orders = Orders,
        Sessions = Sessions,
        Days = Days,
        DefectRate = DefectRate,
        LateArrivalRate = LateArrivalRate
    };
}

/// <summary>JWT settings. The signing key is a dev secret only — never commit a production key.</summary>
public sealed class AuthSettings
{
    public string Issuer { get; set; } = "lakehouse";
    public string Audience { get; set; } = "lakehouse-clients";
    public string SigningKey { get; set; } = "dev-only-signing-key-change-me-please-32bytes!";
}
