# Plan 008: Reconcile stale specs and user docs

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the next
> step. If anything in the "STOP conditions" section occurs, stop and report.
> When done, update the status row for this plan in `plans/README.md`.
>
> **Drift check (run first)**:
> `git diff --stat d48750f..HEAD -- README.md AGENTS.md docs .claude config.example.json winui/AppConfigStore.cs winui/FluentAgentBar.csproj`
> If any in-scope file changed since this plan was written, compare the current
> state excerpts below against the live code before proceeding.

## Status

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW
- **Depends on**: `plans/002-winui-canonical-quarantine-legacy.md`
- **Category**: docs, dx
- **Planned at**: commit `d48750f`, 2026-06-13

## Why this matters

The repository has been renamed and ported, but several docs still point to old
paths, old project names, and old config directories. This misleads humans and
agents into running commands that cannot work. After WinUI is canonical, docs
should describe the current product and isolate historical specs clearly.

## Current state

- `README.md:24` correctly uses `dotnet build winui\FluentAgentBar.csproj`.
- `docs/specs/winui-real-usage-backend.md:3-4`,
  `docs/specs/claude-usage-and-token-stats.md:3`, and other specs still mention
  `winui/CodexSwbarWinUI.csproj`.
- `docs/specs/winui-taskbar-blend-spec.md:21` contains
  `namespace CodexSwbarWinUI;`.
- `docs/PORTING.md:63` and `docs/PORTING.md:93` still describe
  `%APPDATA%\Codex SWBar Windows`.
- `.claude/settings.local.json:4` contains an old local path
  `e:/Tools/Codex-SWBar-Windows`.
- `winui/AppConfigStore.cs:22-23` shows the current config folder is
  `Fluent AgentBar`, with legacy migration from `Codex SWBar Windows`.

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Search stale names | `rg -n "CodexSwbarWinUI|Codex-SWBar-Windows|namespace CodexSwbarWinUI" README.md AGENTS.md docs .claude config.example.json` | no active-doc matches |
| Search current project | `rg -n "FluentAgentBar.csproj|Fluent AgentBar" README.md docs AGENTS.md` | active docs use current names |
| Verify | `.\scripts\verify-winui.ps1` | exit 0 |

## Scope

**In scope**:

- `README.md`
- `AGENTS.md`
- `docs/design/fluent-windows.md`
- `docs/PORTING.md`
- `docs/specs/*.md`
- `.claude/settings.local.json` if it remains in the repo
- `config.example.json`

**Out of scope**:

- Runtime code changes.
- Config schema changes.
- Deleting legacy docs without the archive/quarantine work from plan 002.

## Git workflow

- Branch: `advisor/008-reconcile-docs-and-specs`
- Commit message: `Reconcile Fluent AgentBar docs`
- Do not push or open a PR unless the operator instructed it.

## Steps

### Step 1: Decide active vs historical docs

For every file under `docs/specs/`, decide one of:

- Active spec: update names/commands to current WinUI project.
- Historical spec: move under `docs/legacy/` or add a top banner:

```markdown
> Historical porting note. This file describes an earlier Codex SWBar Windows
> phase and is not the source of truth for current Fluent AgentBar work.
```

Do not leave ambiguous docs with old commands in active paths.

**Verify**:
`rg -n "CodexSwbarWinUI|Codex-SWBar-Windows|namespace CodexSwbarWinUI" docs/specs docs/PORTING.md`
only returns matches in files clearly marked historical.

### Step 2: Update current setup/build docs

Make `README.md` and active docs agree on:

- Product name: Fluent AgentBar.
- Project file: `winui\FluentAgentBar.csproj`.
- Config path: `%APPDATA%\Fluent AgentBar\config.json`.
- Legacy migration: old `%APPDATA%\Codex SWBar Windows\config.json` may be
  copied on first run, but it is not the active path.
- Verification command: `.\scripts\verify-winui.ps1`.

**Verify**:
`rg -n "FluentAgentBar.csproj|%APPDATA%\\\\Fluent AgentBar|verify-winui" README.md docs`
shows the current terms in active docs.

### Step 3: Clean local-agent metadata if committed

If `.claude/settings.local.json` is intended to be committed, update its old
path to this repository or remove the stale permission entry. If it is purely
local state, add it to `.gitignore` and remove it from tracking in a separate
operator-approved cleanup. Do not delete tracked files without approval.

**Verify**:
`rg -n "Codex-SWBar-Windows" .claude .gitignore` shows no stale active path.

### Step 4: Update design doc naming without changing design rules

In `docs/design/fluent-windows.md`, replace old product naming with
`Fluent AgentBar`. Preserve the design rules: Windows 11 Fluent, Mica-like
settings, Acrylic-like flyouts, subtle borders, Segoe UI, compact spacing.

**Verify**:
`rg -n "Fluent AgentBar|Codex SWBar Windows" docs/design/fluent-windows.md`
shows only current naming unless an explicit historical note is present.

### Step 5: Run verification

Docs-only changes should not alter build output, but run verification anyway.

**Verify**:
`.\scripts\verify-winui.ps1` exits 0.

## Test plan

This is docs-only. Verification is a stale-name search plus the WinUI build/test
baseline.

## Done criteria

- [ ] Active docs use `Fluent AgentBar` and `FluentAgentBar.csproj`.
- [ ] Historical specs are clearly marked or moved under `docs/legacy/`.
- [ ] Active docs no longer tell agents to build `CodexSwbarWinUI.csproj`.
- [ ] Config docs distinguish current path from legacy migration path.
- [ ] `.\scripts\verify-winui.ps1` exits 0.
- [ ] `plans/README.md` status row updated.

## STOP conditions

Stop and report if:

- It is unclear whether a spec is active or historical.
- Cleaning `.claude/settings.local.json` would require deleting a tracked file
  without operator approval.
- Docs reveal a product promise not implemented in WinUI and not already covered
  by another plan.

## Maintenance notes

After this plan, new specs should start from the current WinUI project name and
verification command. Avoid writing implementation instructions that depend on
legacy C++ files.
