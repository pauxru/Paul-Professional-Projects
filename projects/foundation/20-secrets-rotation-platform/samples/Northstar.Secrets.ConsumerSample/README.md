# Consumer SDK-style sample

This compile-checked console sample demonstrates the application-side lifecycle:

1. acquire a local demonstration JWT;
2. poll the consumer pull endpoint;
3. receive a versioned `@secret:app/env/name#vN` notice;
4. retrieve and cache the staged value without printing it;
5. acknowledge the rotation so the control plane can promote it.

Run the API first, register a consumer and a matching path policy for subject
`sample-consumer`, then run:

```powershell
dotnet run --project samples\Northstar.Secrets.ConsumerSample -- http://localhost:5020 <consumer-guid>
```

The sample is intentionally small. A production SDK would use workload identity, bounded
cache expiry, backoff, cancellation, telemetry, and secure process-memory handling.
