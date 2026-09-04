# Compatibility Matrix

This matrix distinguishes the runnable simulated code from common adjacent .NET Framework-era dependencies that an actual Northstar discovery would verify.

| Legacy dependency/API/pattern | Observed or discovery item | Modern replacement | Effort | Risk | Blocking? |
|---|---|---|---:|---|---|
| `DatabaseHelper` / raw `SqliteConnection` | Observed: `DatabaseHelper.cs` | EF Core repositories and explicit configurations | M | High | Yes |
| Concatenated SQL | Observed: `DatabaseHelper.cs:115` | Parameterized LINQ/EF queries | S | Critical | Yes |
| `ConfigurationManager` static access | Simulated by `AppSettings.cs` + XML | `IOptions<T>`, data annotations, startup validation | S | Medium | Yes |
| MVC fat controller | Observed: `ClaimsController.cs` | Minimal endpoint + application use case + domain service | M | High | Yes |
| `new`/service locator style construction | Observed static/global dependency path | Constructor injection and application-owned ports | M | Medium | Yes |
| `.Result`, `.Wait()`, `Thread.Sleep` | Observed: controller/calculator | `async`/`await`, `CancellationToken`, bounded resilience policy | M | Medium | Yes |
| ASP.NET Session workflow | Observed: `ClaimsController.cs` | Persisted aggregate state and version | M | High | Yes |
| Static `Dictionary` cache | Observed: `DatabaseHelper.cs:8` | Explicit cache port or no cache until measured | S | Medium | No |
| Direct `System.IO` attachments | Observed: controller | `IDocumentStore`; local adapter then Azure Blob adapter | M | High | Yes |
| Swallowed `Exception` / `Console.WriteLine` | Observed: controller | `ILogger`, exception handler, ProblemDetails, telemetry | S | High | Yes |
| `System.Web.HttpContext.Current` | Discovery check for real Framework source | Endpoint parameters / `IHttpContextAccessor` only at adapter edge | S | Medium | No |
| WebForms / ViewState | Discovery check; not simulated | No direct equivalent; replace screen behavior with API/Razor/SPA contracts | L | Medium | Potentially |
| `HttpModules` | Discovery check | Ordered ASP.NET Core middleware | S | Medium | No |
| `Global.asax` | Discovery check | `Program.cs` composition root | S | Low | No |
| `System.Web.Routing` | Discovery check | Endpoint routing / route groups | S | Low | No |
| `.NET Framework 4.8` runtime | Host constraint / likely source baseline | `net10.0` target with staged compatibility testing | L | High | Yes |
| `web.config` transforms | Discovery check | `appsettings.*`, environment variables, deployment secret store | M | Medium | No |
| Forms/Windows authentication | Discovery check | JWT bearer/OIDC with policy authorization | M | High | Yes |
| File share documents | Simulated local files | `IDocumentStore`; Azure Blob adapter documented | M | High | Yes |
| SQL Server stored procedures | Discovery check | Keep parameterized procedure initially or migrate deliberate rules to application/domain | M | Medium | No |
| Dapper/hand-written mapper | Discovery alternative | EF Core target for aggregate consistency and migrations | S | Low | No |
| In-process scheduled work | Discovery check | Background service/outbox worker with retry and dead-letter plan | M | Medium | Potentially |

## Compatibility gates
1. Do not route a write slice until a target unique key and concurrency behavior are validated.
2. Do not import attachments until a manifest count/checksum is reconciled.
3. Do not remove legacy access until facade telemetry shows no unsupported URLs.
4. Treat any discovery of WebForms postback dependencies, COM, Windows-only auth, or opaque stored procedures as a scope/risk re-estimation trigger.
