namespace ExampleBank.Ledger.Api.Contracts;

/// <summary>Body for capturing a hold; the hold id comes from the route, idempotency from the header.</summary>
public sealed record CaptureHoldBody(Guid DestinationAccountId, long? CaptureMinor = null, string? Reference = null);

/// <summary>Body for minting a development JWT (local/demo only).</summary>
public sealed record DevTokenBody(string Subject, string[] Scopes);
