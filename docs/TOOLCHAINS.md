# Verified toolchains — projects 31–50

Every entry below was **verified by actually compiling and running something** on this
machine on the date of writing. Do not re-audit; do not assume anything not listed here
exists.

| Toolchain | Version | How to invoke |
|---|---|---|
| .NET SDK | 10.0.400 | `dotnet` (on PATH). Target `net10.0`. |
| Node / npm | 24.18.0 / 11.16.0 | `node`, `npm` (on PATH) |
| Python | 3.12.10 | **Full path only** (see below) |
| Go | 1.23.6 | `%USERPROFILE%\toolchains\go\bin\go.exe` |
| Java (Temurin JDK) | 21.0.12.1 | `%USERPROFILE%\toolchains\jdk\bin\java.exe` |
| Maven | 3.9.9 | `%USERPROFILE%\toolchains\maven\bin\mvn.cmd` |
| Rust / Cargo | 1.98.0 (MSVC ABI) | `%USERPROFILE%\toolchains\cargo\bin\cargo.exe` |
| MSVC C/C++ | 19.51 (VS 18 Enterprise) | via `vcvars64.bat` (see below) |
| CMake | 4.3.1 | on PATH after `vcvars64.bat` |
| Ninja | bundled with VS | on PATH after `vcvars64.bat` |

---

## Python

On a stock Windows install the bare command `python` may resolve to the **Microsoft Store
stub**, which appears to work and then fails partway through. Invoke the real interpreter by
its full path instead:

```
%LOCALAPPDATA%\Programs\Python\Python312\python.exe
```

The `test.ps1` scripts in the Python projects hard-code that path for exactly this reason:
a harness that silently picks up the wrong interpreter produces a failure that looks like a
bug in the code under test. Change the `$py` line at the top of the script if your
interpreter lives elsewhere.

Create the venv **inside your project folder**:

```powershell
& "$env:LOCALAPPDATA\Programs\Python\Python312\python.exe" -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
```

**Never create a venv under `%TEMP%`** — 8.3 short-path redirection (`RUKWAR~1`) breaks
`ensurepip` and you get a venv with no `pip`.

`pytest` and `numpy` are the only third-party packages any project needs.

---

## Go

```powershell
$env:GOROOT = "$env:USERPROFILE\toolchains\go"
$env:GOPATH = "$env:USERPROFILE\toolchains\gopath"
$env:Path   = "$env:USERPROFILE\toolchains\go\bin;$env:Path"
go build ./...
go test ./... -race
```

`proxy.golang.org` is reachable; `go get` works. Verified: `go test` green on a real module.

---

## Java + Maven

```powershell
$env:JAVA_HOME = "$env:USERPROFILE\toolchains\jdk"
$env:Path      = "$env:USERPROFILE\toolchains\jdk\bin;$env:USERPROFILE\toolchains\maven\bin;$env:Path"
mvn -B test
```

Maven Central is reachable — verified by resolving `junit-jupiter:5.11.3` and
`maven-surefire-plugin:3.5.2` and running a green JUnit 5 test.

Use `maven.compiler.release=21`. Prefer plain Maven + JUnit 5; **do not** assume Gradle,
Spring Boot starters that need network-heavy BOMs, or an application server.

---

## Rust

Rust uses the **MSVC ABI**, so the MSVC linker must be on PATH. That means Rust builds must
run **inside a `vcvars64.bat` environment** (see next section).

```powershell
$env:CARGO_HOME  = "$env:USERPROFILE\toolchains\cargo"
$env:RUSTUP_HOME = "$env:USERPROFILE\toolchains\rustup"
$env:Path        = "$env:CARGO_HOME\bin;$env:Path"
cargo test --release
```

Verified: `cargo new` + `cargo run` links and executes correctly. crates.io is reachable.

---

## C / C++ (MSVC)

`cl.exe` is **not on PATH by default**. You must initialise the environment and stay in the
**same process**:

```powershell
& $env:ComSpec /c 'call "C:\Program Files\Microsoft Visual Studio\18\Enterprise\VC\Auxiliary\Build\vcvars64.bat" >nul && cd /d C:\path\to\project && cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release && cmake --build build && ctest --test-dir build --output-on-failure'
```

Running `vcvars64.bat` in one PowerShell call and `cl` in another **will not work** — the
PATH/LIB/INCLUDE changes do not survive.

Verified: CMake 4.3.1 + Ninja + MSVC 19.51 configured, built and passed a `ctest` run.

Use C++20. Prefer header-only or vendored test frameworks (or write a tiny assertion
harness yourself) over anything requiring a package manager — **vcpkg and Conan are not
installed**.

For Python bindings, prefer `ctypes`/`cffi` against a plain C ABI shared library
(`.dll`) over pybind11 — no extra dependency, and it demonstrates the ABI boundary
explicitly.

---

## NuGet (.NET) — important

`nuget.org` is **disabled**; an internal `azure-default` proxy feed is used instead.

**Always** `dotnet add package <Name>` with **no `--version`**. Pinning a version that the
proxy has not mirrored fails the restore.

**The proxy is a real, working feed — not an empty one.** Re-verified 2026-02: `dotnet new
xunit` restores and `dotnet test` executes; `dotnet add package Mono.Cecil` fetched 0.11.6
fresh. Treat third-party packages as *available* and probe before designing around their
absence. An earlier note in this file claimed otherwise and cost project 31 a redesign onto
`System.Reflection.Metadata` that was never necessary.

`Microsoft.AspNetCore.RateLimiting` does **not** resolve as a package — rate limiting is in
the shared framework, use `builder.Services.AddRateLimiter(...)` directly.

---

## What does NOT exist

- **Docker / containers** — nothing container-based can be executed. Dockerfiles and compose
  files may exist but must be labelled `UNVERIFIED`.
- **No database or broker servers** — no Postgres, SQL Server, Redis, Kafka, RabbitMQ.
  Use SQLite, embedded/in-process stores, or something you implement yourself.
- **No cloud credentials, no Azure/AWS access.**
- **No LLM or embedding API access**, and no pretrained-model downloads. AI projects must
  use deterministic local simulators and say so loudly.
- **No vcpkg, no Conan, no Gradle.**
- Downloads: use `curl.exe`, **not** `Invoke-WebRequest` — the latter is ~1000x slower here
  because of the progress-bar bug (measured: 4.9 MB in 10 min vs 78 MB in 1.1 s).
