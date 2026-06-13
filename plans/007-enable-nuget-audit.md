# Plan 007: Re-enable dependency auditing

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the next
> step. If anything in the "STOP conditions" section occurs, stop and report.
> When done, update the status row for this plan in `plans/README.md`.
>
> **Drift check (run first)**:
> `git diff --stat d48750f..HEAD -- winui/FluentAgentBar.csproj winui/NuGet.Config README.md scripts .github`
> If any in-scope file changed since this plan was written, compare the current
> state excerpts below against the live code before proceeding.

## Status

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW
- **Depends on**: `plans/001-winui-verification-baseline.md`
- **Category**: security, dx
- **Planned at**: commit `d48750f`, 2026-06-13

## Why this matters

The project explicitly disables NuGet audit. This is a small desktop utility,
but it reads local credentials and launches external CLIs, so dependency
advisories should be visible during verification. This plan re-enables audit and
defines how to handle advisories without introducing noisy or blocking behavior
blindly.

## Current state

- `winui/FluentAgentBar.csproj:20` has `<NuGetAudit>false</NuGetAudit>`.
- `winui/FluentAgentBar.csproj:24-25` references:
  - `CommunityToolkit.WinUI.Controls.SettingsControls` version `8.2.251219`
  - `Microsoft.WindowsAppSDK` version `2.2.0`
- `winui/NuGet.Config` uses only `https://api.nuget.org/v3/index.json`.

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Restore with audit | `dotnet restore winui\FluentAgentBar.csproj -p:NuGetAudit=true` | exit 0 or only documented non-high advisories |
| Build | `dotnet build winui\FluentAgentBar.csproj -c Debug -p:Platform=x64` | exit 0 |
| Verify | `.\scripts\verify-winui.ps1` | exit 0 |

## Scope

**In scope**:

- `winui/FluentAgentBar.csproj`
- `scripts/verify-winui.ps1`
- `.github/workflows/winui.yml` if created in plan 001
- `README.md` only if verification docs need a note

**Out of scope**:

- Major package migrations unless required by high/critical reachable advisories.
- Changing package sources.
- Rewriting WinUI controls.

## Git workflow

- Branch: `advisor/007-enable-nuget-audit`
- Commit message: `Enable NuGet dependency audit`
- Do not push or open a PR unless the operator instructed it.

## Steps

### Step 1: Remove the blanket audit disable

Delete `<NuGetAudit>false</NuGetAudit>` from `winui/FluentAgentBar.csproj`.

If restore now emits low/moderate advisories only, do not suppress them in this
plan. If restore emits high/critical advisories, continue to Step 2.

**Verify**:
`dotnet restore winui\FluentAgentBar.csproj -p:NuGetAudit=true` exits 0 or shows
only advisory output that is not high/critical.

### Step 2: Address high/critical advisories if present

If NuGet reports high or critical advisories:

- Identify the affected package name and version.
- Prefer a minimal patch/minor update that does not change WinUI/App SDK major
  behavior.
- Run build and tests.
- Do not paste vulnerability exploit details or secret values anywhere.

If the only fix requires a major Windows App SDK migration, stop and report
instead of doing the migration in this plan.

**Verify**:
`dotnet restore winui\FluentAgentBar.csproj -p:NuGetAudit=true` exits 0 without
high/critical advisories.

### Step 3: Include audit in verification

Update `scripts/verify-winui.ps1` so it runs restore/audit before build/test:

```powershell
dotnet restore winui\FluentAgentBar.csproj -p:NuGetAudit=true
```

Then run build and tests as before.

**Verify**:
`.\scripts\verify-winui.ps1` exits 0.

### Step 4: Update CI if present

If `.github/workflows/winui.yml` exists, make sure it uses the same verification
script or includes restore/audit explicitly.

**Verify**:
`rg -n "NuGetAudit|verify-winui|dotnet restore" .github workflows scripts`
shows audit is covered. If `.github` does not exist, skip this verification and
state that in the final summary.

## Test plan

This is tooling-only. Passing restore/audit, build, and tests is the test plan.

## Done criteria

- [ ] `<NuGetAudit>false</NuGetAudit>` is gone.
- [ ] Restore/audit reports no high/critical unresolved advisories.
- [ ] `.\scripts\verify-winui.ps1` includes dependency audit and exits 0.
- [ ] CI includes the same audit if CI exists.
- [ ] `plans/README.md` status row updated.

## STOP conditions

Stop and report if:

- A high/critical advisory can only be fixed by a major Windows App SDK or
  toolkit migration.
- Restore/audit requires network access unavailable in the execution
  environment.
- Package updates break WinUI build in a way that cannot be fixed without UI
  rewrites.

## Maintenance notes

Keep audit in the default verification path so future package changes cannot
silently disable it again.
