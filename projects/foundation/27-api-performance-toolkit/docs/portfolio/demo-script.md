# Demo script

A 60-second walk-through you can talk over during an interview or send as a screencast.

## Setup (off-camera)

- Repo cloned.
- `dotnet build -c Release` done once.
- Two PowerShell windows open in the repo root.

## Slide 1 — the pitch (5 s)

> "I built a load-testing toolkit that handles the three things homegrown load tools
> get wrong: open vs closed load model, coordinated omission, and statistical significance
> in comparisons."

## Slide 2 — build + test (10 s)

```powershell
dotnet build -c Release
dotnet test  -c Release
```

Point out: **72 tests pass, zero external infrastructure**.

## Slide 3 — start the sample API (5 s)

Window 1:

```powershell
$env:ASPNETCORE_URLS='http://127.0.0.1:5027'
dotnet .\src\SampleApi\bin\Release\net10.0\SampleApi.dll
```

> "It's a small orders/catalogue API on SQLite with six deliberately-broken modes I can
> flip on with an HTTP header."

## Slide 4 — the pathological baseline (15 s)

Window 2:

```powershell
Invoke-WebRequest -Method Delete -Uri http://127.0.0.1:5027/admin/pathology | Out-Null
dotnet .\src\LoadRunner.Cli\bin\Release\net10.0\loadrun.dll `
    run .\scenarios\case-study-baseline.json --results results --out results
```

Read out the summary:

- Service p95 = ~65 ms.
- Intended p95 = ~140 ms.

> "The gap between the two — 75 ms of queueing — is exactly what coordinated omission would
> have hidden if I'd used a naive closed-model harness."

## Slide 5 — the optimised candidate (15 s)

```powershell
Invoke-WebRequest -Method Delete -Uri http://127.0.0.1:5027/admin/pathology | Out-Null
dotnet .\src\LoadRunner.Cli\bin\Release\net10.0\loadrun.dll `
    run .\scenarios\case-study-optimised.json --results results --out results
```

Read out:

- Service p95 = ~24 ms.
- Intended p95 = ~70 ms.

## Slide 6 — the significance test (10 s)

```powershell
dotnet .\src\LoadRunner.Cli\bin\Release\net10.0\loadrun.dll `
    compare (Get-ChildItem results\case-study-baseline-*.json | Select -Last 1).FullName `
            (Get-ChildItem results\case-study-optimised-*.json | Select -Last 1).FullName `
            --out results
```

Show the last lines of the printed markdown:

- Mann–Whitney U p-value < 0.0001, verdict **Improved**.
- Bootstrap 95 % CI on median Δ **entirely below zero**, verdict **Improved**.

> "The exit code is 0 because the candidate is genuinely faster. If I'd made it slower
> by even a little in a statistically detectable way, the exit code would be 1 and my
> CI job would fail. That's the point."
