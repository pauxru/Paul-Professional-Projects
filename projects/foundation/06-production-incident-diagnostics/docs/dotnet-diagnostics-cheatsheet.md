# .NET Diagnostics Cheatsheet

> Host note: `dotnet-counters`, `dotnet-trace`, `dotnet-dump`, and `dotnet-gcdump` were **not installed on the build host**. Commands below are operational guidance, not a claim that these tools were run for this repository.

## Find a process
```powershell
dotnet-counters ps
Get-Process dotnet
```
Use the actual process ID; do not kill processes by name during an incident.

## Live counters
```powershell
dotnet-counters monitor --process-id <pid> System.Runtime Microsoft.AspNetCore.Hosting
dotnet-counters collect --process-id <pid> --counters System.Runtime --format json --output counters.json
```
Useful `System.Runtime` signals include CPU usage, allocation rate, GC heap size, Gen 0/1/2 collections, time in GC, exception rate, ThreadPool thread count, queue length, and completed work items. Treat counter names as tool/runtime-version dependent; inspect `dotnet-counters list` on the target host.

### ThreadPool starvation signature
- Request duration rises while CPU may remain low or moderate.
- ThreadPool queue length grows and available workers fall or workers climb slowly.
- Stacks show synchronous waits (`.Wait`, `.Result`, blocking I/O) on request work.
- Increasing minimum threads can mask symptoms; remove the blocking dependency rather than making that the primary fix.

### GC pressure signature
- Allocation rate and Gen 0 collections rise with traffic.
- Gen 2 collections, heap size after full GC, or LOH size trend upward when retention exists.
- Pause time becomes visible in request tails.
- A stable allocation rate with an increasing post-GC heap suggests a root/retention investigation, not merely object churn.

## EventPipe trace
```powershell
dotnet-trace collect --process-id <pid> --providers Microsoft-DotNETCore-SampleProfiler,Microsoft-Windows-DotNETRuntime:0x1C000080018:5 --format NetTrace --output incident.nettrace
dotnet-trace report incident.nettrace topN --topN 20
```
EventPipe is the runtime event pipeline used by these tools. Providers and keywords determine cost; begin with a short, scoped collection. Store traces as potentially sensitive artifacts.

## GC dump
```powershell
dotnet-gcdump collect --process-id <pid> --output incident.gcdump
dotnet-gcdump report incident.gcdump
```
Use a GC dump to identify managed object type growth and retained paths. It is generally narrower than a full dump but still must be access-controlled.

## Full dump
```powershell
dotnet-dump collect --process-id <pid> --type Full --output incident.dmp
dotnet-dump analyze incident.dmp
```
In `dotnet-dump analyze`, SOS commands commonly begin with:
```text
clrstack
threads
dumpheap -stat
gcroot <object-address>
syncblk
```
`clrstack` investigates managed call stacks, `threads` lists managed threads, `dumpheap -stat` summarizes managed types, `gcroot` finds paths retaining an object, and `syncblk` can reveal monitor contention. Exact availability can vary by runtime and dump type.

## When to choose what
| Need | Lower-cost first choice | Escalate when |
|---|---|---|
| Latency / retry chain | OpenTelemetry trace or EventPipe trace | Stack causality remains unclear |
| ThreadPool starvation | Counters + short trace | Deadlock or blocked native call is suspected |
| Managed memory growth | Counters + GC dump | Native leak/crash analysis needs a full dump |
| Crash | Dump plus logs/traces around crash | Reproduction fails or native analysis is needed |

## Safety checklist
- Scope capture to one canary/process and a short window.
- Confirm free disk, artifact encryption, and access controls first.
- Never publish request bodies, bearer tokens, connection strings, or customer identifiers in an incident artifact.
- Record provider configuration and timestamps with each artifact.
- Remove or rotate temporary diagnostic access after the incident.
