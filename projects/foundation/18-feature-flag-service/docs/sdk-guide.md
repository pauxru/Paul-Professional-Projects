# .NET SDK Integration Guide

## Install/reference
The SDK project is included in this repository. A consuming solution can reference it directly while evaluating the source, or package `FeatureFlags.Sdk` in a real distribution pipeline.

```powershell
dotnet add MyService reference ..\FeatureFlags.Sdk\FeatureFlags.Sdk.csproj
```

## Register with dependency injection
`AddFeatureFlags` binds and validates options, creates one process-wide client, starts bootstrap/SSE/polling with a hosted service, and registers `IFeatureGate`.

```csharp
using FeatureFlags.Sdk;

builder.Services.AddFeatureFlags(options =>
{
    options.ApiBaseUrl = "https://flags.example.internal";
    options.ProjectKey = "payments";
    options.EnvironmentKey = "production";
    options.SdkKey = Environment.GetEnvironmentVariable("FEATURE_FLAGS_SDK_KEY")!;
    options.PollIntervalSeconds = 30;
    options.FlushIntervalSeconds = 15;
    options.EventBufferCapacity = 1_000;
    options.OfflineStoragePath = Path.Combine(AppContext.BaseDirectory, "flags-cache.json");
});
```

Equivalent configuration binding:
```json
{
  "FeatureFlags": {
    "ApiBaseUrl": "http://localhost:5018",
    "ProjectKey": "acme",
    "EnvironmentKey": "dev",
    "SdkKey": "client-dev-acme-public-demo"
  }
}
```

## Evaluate locally
Variation calls do not perform network I/O. `Detail` APIs make rollout/audit diagnostics visible.

```csharp
using FeatureFlags.Domain;
using FeatureFlags.Sdk;

var context = EvaluationContext.Create("account-42", new { country = "KE", plan = "pro" });
var detail = client.BoolVariationDetail("new-checkout", context, defaultValue: false);
if (detail.Value) EnableNewCheckout();
logger.LogInformation("Flag variation {Variation}; reason {Reason}", detail.VariationIndex, detail.Reason);

var timeout = client.NumberVariation("checkout-timeout-seconds", context, 15m);
var copy = client.StringVariation("pricing-copy", context, "Simple pricing");
var config = client.JsonVariation("server-config", context, JsonSerializer.SerializeToElement(new { mode = "safe" }));
```

## Private attributes
Declare private attributes when you construct a context. The local evaluator can use them; SDK events contain only context key/flag/variation and never include the attribute map.

```csharp
var attributes = new Dictionary<string, JsonElement>
{
    ["email"] = JsonSerializer.SerializeToElement("person@example.invalid"),
    ["country"] = JsonSerializer.SerializeToElement("KE")
};
var context = new EvaluationContext("account-42", "user", attributes, new HashSet<string> { "email" });
```

## Gate an endpoint
```csharp
app.MapGet("/beta", () => Results.Ok("enabled"))
   .RequireFeatureFlag("new-checkout");
```
The endpoint filter looks for `X-Feature-Context` (then authenticated name, then `anonymous`) and returns 404 while the feature is off. The `samples/DemoApp` also checks `maintenance-mode` to return a 503 kill-switch response.

## Events and offline behavior
Call `Track` for custom metrics; events batch until interval/flush and retry transient failures. If a server is unavailable, the client loads `OfflineStoragePath`; if no file exists, variation methods return your supplied default with `Error(ClientNotReady)` and never throw.

```csharp
client.Track("conversion", context, numericValue: 1m);
await client.FlushAsync(stoppingToken); // useful before controlled shutdown
```

## Operational guidance
Use client SDK keys only in apps where all returned client-side rules may be observed. Use server keys only in trusted services. Rotate keys through an external secret store in production; do not put keys in browser bundles unless the flag is explicitly safe for client exposure.
