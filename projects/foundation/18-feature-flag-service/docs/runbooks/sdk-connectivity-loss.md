# SDK Connectivity Loss Runbook

## Symptom
A consumer cannot reach the control plane, SSE disconnects repeatedly, or the SDK reports `Error(ClientNotReady)`.

## Expected behavior
The SDK reads the persisted last-known-good ruleset before networking. Existing cached flags continue evaluating locally. SSE reconnects with exponential backoff while an independent poll retries at the configured interval. Evaluation/custom events remain buffered up to capacity; overflow increments `DroppedEventCount`.

## Triage
1. Inspect consumer logs for SDK bootstrap, SSE, or conditional-fetch errors. Do not log SDK keys or private attributes.
2. Check `/health/live` and `/health/ready` on the API and validate DNS/TLS/proxy reachability in the deployment environment.
3. Check the configured offline cache path is writable and contains a valid ruleset snapshot.
4. Inspect the drop counter. If analytics loss matters, restore connectivity before buffer capacity is exhausted.

## Recovery and safety
Do not change application code to remote-evaluate each request during an outage. Restore API reachability; verify a successful ETag fetch / SSE connection; then confirm the expected configuration version through normal observability. If no cache exists, callers must handle supplied defaults and the `ClientNotReady` reason explicitly.
