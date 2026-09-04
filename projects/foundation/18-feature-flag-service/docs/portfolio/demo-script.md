# Demo Script

1. Run `dotnet run --project src\FeatureFlags.Api` and open `http://localhost:5018/`.
2. Obtain a development token through the UI and choose the seeded `dev` environment.
3. Show the `new-checkout` client-side boolean, `pricing-copy` string, `checkout-timeout-seconds` number, and server-only `search-config` JSON flag.
4. Use targeting preview for a `country=KE` context; explain `SegmentMatch`.
5. Set the dev kill switch off; show reason `Off`, then restore it.
6. Open audit history and show before/after/diff plus one-click revert.
7. Create a production approval request, review it as a distinct actor, then apply it.
8. Start `samples\DemoApp`, call `/checkout` with `X-Feature-Context`, and explain local SDK evaluation/SSE refresh.
9. Disconnect the API after a cache exists and show the consumer retains last-known-good behavior.
10. Show `docs/bucketing-verification.md` and test results as verification evidence.
