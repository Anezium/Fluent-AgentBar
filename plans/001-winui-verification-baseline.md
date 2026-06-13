# Plan 001: Establish a WinUI verification baseline

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the next
> step. If anything in the "STOP conditions" section occurs, stop and report.
> When done, update the status row for this plan in `plans/README.md`.
>
> **Drift check (run first)**:
> `git diff --stat d48750f..HEAD -- README.md build.ps1 winui`
> If any in-scope file changed since this plan was written, compare the current
> state excerpts below against the live code before proceeding.

## Status

- **Priority**: P1
- **Effort**: M
- **Risk**: LOW
- **Depends on**: none
- **Category**: tests, dx
- **Planned at**: commit `d48750f`, 2026-06-13

## Why this matters

The active app is the WinUI project, but the only test suite in the repository is
for the legacy C++ parser. The WinUI code now owns config migration, Codex JSON
RPC parsing, Claude usage parsing, local token scans, cost calculation, and
Windows window behavior. Future work should not use `src/main.cpp` as a safety
net; WinUI needs its own build and test command first.

## Current state

- `README.md:24` documents the WinUI build command:

```powershell
dotnet build winui\FluentAgentBar.csproj
```

- `build.ps1:13-18` builds `src\main.cpp` and `src\parser_tests.cpp`, producing
  `CodexSWBarWindows.exe` and `parser_tests.exe`. This is legacy-only.
- `src/parser_tests.cpp` includes `main.cpp` directly and tests C++ JSON helpers.
- No C# test project exists under `winui/`, and no CI workflow exists.
- `winui/FluentAgentBar.csproj:4-21` targets
  `net8.0-windows10.0.19041.0`, enables nullable reference types, and builds a
  self-contained WinUI x64 app.

Repo conventions to match:

- C# files use file-scoped namespaces such as `namespace FluentAgentBar;`.
- Code favors explicit defensive parsing and `Debug.WriteLine` for non-fatal
  errors, for example in `winui/TokenStatsService.cs`.
- Do not redesign UI. UI work must follow `docs/design/fluent-windows.md`.

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Build WinUI | `dotnet build winui\FluentAgentBar.csproj -c Debug -p:Platform=x64` | exit 0 |
| Test WinUI after this plan | `dotnet test winui\FluentAgentBar.Tests\FluentAgentBar.Tests.csproj -c Debug` | exit 0, all tests pass |
| Full verification after this plan | `.\scripts\verify-winui.ps1` | exit 0, runs build and tests |

## Scope

**In scope**:

- `winui/FluentAgentBar.csproj`
- `winui/FluentAgentBar.Tests/` (create)
- `winui/Properties/AssemblyInfo.cs` or equivalent test visibility file (create
  only if needed)
- `scripts/verify-winui.ps1` (create)
- `README.md`
- `.github/workflows/winui.yml` (create if this repo uses GitHub Actions)

**Out of scope**:

- `src/main.cpp`, `src/parser_tests.cpp`, and the legacy C++ implementation.
- UI layout changes.
- Functional changes to config, usage fetching, token scanning, or windowing.

## Git workflow

- Branch: `advisor/001-winui-verification-baseline`
- Commit message: `Add WinUI verification baseline`
- Do not push or open a PR unless the operator instructed it.

## Steps

### Step 1: Add a C# test project

Create `winui/FluentAgentBar.Tests/FluentAgentBar.Tests.csproj` using .NET 8 and
a common test framework already familiar to .NET agents. Prefer xUnit unless the
repo owner has a different standard. The test project should reference
`..\FluentAgentBar.csproj`.

Because the app classes are mostly `internal`, add test visibility in the main
project by creating a small file such as `winui/Properties/AssemblyInfo.cs`:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("FluentAgentBar.Tests")]
```

Do not make production classes public just for tests.

**Verify**:
`dotnet test winui\FluentAgentBar.Tests\FluentAgentBar.Tests.csproj -c Debug`
exits 0. At this stage it may report one placeholder test.

### Step 2: Add characterization tests for pure WinUI logic

Add focused tests for logic that does not require a live WinUI window:

- `AppConfigStore.NormalizeProvider`: `"claude"` stays `claude`; unknown,
  blank, and null values become `codex`.
- `AppConfigStore.ProfilePathLabel`: paths under `%APPDATA%` and
  `%USERPROFILE%` are shortened without losing the suffix.
- `TokenStats.FormatTokenCount`: raw, K, and M formatting.
- Token price/model normalization behavior through public or internal API. If
  private methods block direct testing, test through `TokenStatsService` with
  temporary JSONL files rather than changing method visibility broadly.
- `ProfileUsage.DetailText`: email is redacted and raw email is not returned.

Use temporary directories/files for token tests. Do not read the real user's
`%USERPROFILE%\.codex` or `%USERPROFILE%\.claude` in tests.

**Verify**:
`dotnet test winui\FluentAgentBar.Tests\FluentAgentBar.Tests.csproj -c Debug`
exits 0 with the new tests passing.

### Step 3: Add a WinUI verification script

Create `scripts/verify-winui.ps1`:

- Set `$ErrorActionPreference = "Stop"`.
- Run `dotnet build winui\FluentAgentBar.csproj -c Debug -p:Platform=x64`.
- Run `dotnet test winui\FluentAgentBar.Tests\FluentAgentBar.Tests.csproj -c Debug`.
- Do not call `.\build.ps1`; that is the legacy C++ path.

**Verify**:
`.\scripts\verify-winui.ps1` exits 0.

### Step 4: Document the new baseline

Update `README.md` so the build section includes:

- Build: `dotnet build winui\FluentAgentBar.csproj -c Debug -p:Platform=x64`
- Test: `dotnet test winui\FluentAgentBar.Tests\FluentAgentBar.Tests.csproj -c Debug`
- Verify: `.\scripts\verify-winui.ps1`

Mention that `build.ps1` is legacy C++ only and is not the validation command
for new WinUI work.

**Verify**:
`rg -n "verify-winui|FluentAgentBar.Tests|build.ps1" README.md` shows the new
instructions and marks `build.ps1` as legacy.

### Step 5: Add CI if repository host supports it

If this repository is intended for GitHub, create `.github/workflows/winui.yml`
that runs on pull requests and pushes:

- `actions/checkout`
- `actions/setup-dotnet` with .NET 8
- `dotnet build winui\FluentAgentBar.csproj -c Debug -p:Platform=x64`
- `dotnet test winui\FluentAgentBar.Tests\FluentAgentBar.Tests.csproj -c Debug`

If no GitHub remote exists, skip the workflow and note that in the final summary.

**Verify**:
If workflow added, `rg -n "dotnet build|dotnet test|FluentAgentBar" .github/workflows/winui.yml`
shows both commands.

## Test plan

- New tests live under `winui/FluentAgentBar.Tests/`.
- Cover config normalization, token formatting/pricing behavior, and email
  redaction.
- Tests must use temporary directories for any file I/O.

## Done criteria

- [ ] `dotnet build winui\FluentAgentBar.csproj -c Debug -p:Platform=x64` exits 0.
- [ ] `dotnet test winui\FluentAgentBar.Tests\FluentAgentBar.Tests.csproj -c Debug` exits 0.
- [ ] `.\scripts\verify-winui.ps1` exits 0.
- [ ] README documents WinUI build, test, and verify commands.
- [ ] New verification does not depend on `src/main.cpp` or `.\build.ps1`.
- [ ] `plans/README.md` status row updated.

## STOP conditions

Stop and report if:

- The WinUI project cannot be referenced by a test project because of Windows
  App SDK constraints that require a different test architecture.
- The first build fails before your changes for reasons unrelated to this plan.
- Testing requires launching real WinUI windows or touching user credential
  directories.

## Maintenance notes

Every future plan should use `.\scripts\verify-winui.ps1` as the default gate.
The C++ parser tests may remain for historical code while it exists, but they
must not be treated as proof that the WinUI app works.
