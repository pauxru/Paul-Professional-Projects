# Five-Minute Demo Script

1. Start the sample app: `dotnet run --project src\Lab.SampleApp --launch-profile http`.
2. Open `/docs`; issue a Development token and show a protected paginated order query.
3. Point out correlation/security headers and `/health/ready`.
4. Run `INC-001` in broken and fixed mode. Open the two evidence Markdown files and compare SQL command counts.
5. Run `INC-002` and show the actual SQLite `SCAN` versus `SEARCH ... USING INDEX` plan.
6. Run `INC-008` and `INC-010`; compare downstream and origin-call amplification.
7. Show one `[Trait("Category", "Incident")]` test and `docs/methodology.md`.
8. Close by stating the limits: local synthetic simulations, no Docker verification, and no production-client claims.
