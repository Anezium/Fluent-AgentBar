# Plan 005: Refresh provider profiles concurrently

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the next
> step. If anything in the "STOP conditions" section occurs, stop and report.
> When done, update the status row for this plan in `plans/README.md`.
>
> **Drift check (run first)**:
> `git diff --stat d48750f..HEAD -- winui/UsageService.cs winui/ClaudeUsageService.cs winui/MockUsageData.cs winui/FluentAgentBar.Tests`
> If any in-scope file changed since this plan was written, compare the current
> state excerpts below against the live code before proceeding.

## Status

- **Priority**: P2
- **Effort**: M
- **Risk**: LOW-MED
- **Depends on**: `plans/001-winui-verification-baseline.md`
- **Category**: perf
- **Planned at**: commit `d48750f`, 2026-06-13

## Why this matters

Every enabled profile refresh is currently awaited sequentially. A Codex profile
can take up to 10 seconds before timing out, and Claude may perform network I/O.
With multiple profiles, one slow account delays the whole flyout and widget.
Fetching profiles concurrently with bounded parallelism keeps the UI fresher
without changing display semantics.

## Current state

- `winui/UsageService.cs:10` sets `ProfileTimeout` to 10 seconds.
- `winui/UsageService.cs:126-129` loops over Codex profiles and awaits each
  `FetchCodexProfileAsync` before starting the next.
- `winui/UsageService.cs:137-140` does the same for Claude profiles.
- `winui/UsageService.cs:95-111` serializes whole refreshes with `_fetchLock`;
  keep that lock so manual refreshes do not overlap periodic refreshes.
- `winui/ClaudeUsageService.cs` already has per-config-dir state and internal
  rate limiting.

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Test | `dotnet test winui\FluentAgentBar.Tests\FluentAgentBar.Tests.csproj -c Debug` | exit 0 |
| Verify | `.\scripts\verify-winui.ps1` | exit 0 |

## Scope

**In scope**:

- `winui/UsageService.cs`
- Optional small internal abstraction for profile fetch delegates
- `winui/FluentAgentBar.Tests/`

**Out of scope**:

- Changing the Codex JSON RPC protocol.
- Changing Claude endpoint behavior or rate limit constants.
- UI layout changes.
- Removing `_fetchLock`.

## Git workflow

- Branch: `advisor/005-concurrent-profile-refresh`
- Commit message: `Refresh profiles concurrently`
- Do not push or open a PR unless the operator instructed it.

## Steps

### Step 1: Preserve output ordering while running fetches concurrently

In `FetchProvidersAsync`, keep profile order as configured. Replace sequential
loops with task creation and `Task.WhenAll`, for example:

- Build ordered lists of enabled Codex and Claude profiles.
- Start one fetch task per profile.
- Await all tasks per provider.
- Convert results back to `ProviderUsage("Codex", "C", codexProfiles)` and
  `ProviderUsage("Claude", "A", claudeProfiles)` in original order.

If desired, add a small bounded concurrency helper with a limit such as 4 to
avoid launching too many child processes.

**Verify**:
`dotnet build winui\FluentAgentBar.csproj -c Debug -p:Platform=x64` exits 0.

### Step 2: Keep failure isolation

Ensure one profile failure still returns an unavailable `ProfileUsage` and does
not fail the whole provider. The existing `FetchCodexProfileAsync` already
catches most exceptions and returns `Unavailable(profile)`; preserve that
behavior.

For Claude, if `_claudeUsageService.FetchAsync(...)` throws outside its existing
catch paths, wrap each profile task so only that profile becomes unavailable.
Use the profile label and Claude accent color.

**Verify**:
Add tests for a helper if you extracted one. Run
`dotnet test winui\FluentAgentBar.Tests\FluentAgentBar.Tests.csproj -c Debug`.

### Step 3: Make the concurrency testable without launching real processes

If `FetchProvidersAsync` is too tightly coupled to real process/network calls,
introduce internal delegates or an internal constructor overload for tests:

- Codex fetch delegate: `(ProfileConfig, CancellationToken) => Task<ProfileUsage>`
- Claude fetch delegate: `(ProfileConfig, CancellationToken) => Task<ProfileUsage>`

The production constructor uses the real methods. Tests can use delayed fake
tasks to prove two profiles refresh concurrently and results preserve order.

Do not expose this as public API.

**Verify**:
Write tests with two fake profiles that each delay 200 ms. The total refresh
should complete substantially under 400 ms with concurrency. Avoid brittle exact
timing; use a generous threshold such as under 350 ms on a normal machine.

### Step 4: Run full verification

**Verify**:
`.\scripts\verify-winui.ps1` exits 0.

## Test plan

- Unit-test ordered concurrent aggregation using fake fetch delegates.
- Unit-test one failed fake profile returning unavailable while another succeeds.
- Do not launch `codex`, call Claude APIs, or read credentials in tests.

## Done criteria

- [ ] Multiple profiles start refresh work concurrently.
- [ ] Provider/profile ordering stays stable.
- [ ] A failure in one profile does not fail other profiles.
- [ ] Existing `_fetchLock` still prevents overlapping whole refreshes.
- [ ] `.\scripts\verify-winui.ps1` exits 0.
- [ ] `plans/README.md` status row updated.

## STOP conditions

Stop and report if:

- Making refresh concurrent requires changing public records or XAML bindings.
- Tests require real `codex app-server` or real Claude credentials.
- Claude rate limiting would be bypassed by the change.

## Maintenance notes

If profile counts grow large, revisit the concurrency limit. Reviewers should
check cancellation behavior: app shutdown must still kill active Codex child
processes and not leave background refresh tasks running.
