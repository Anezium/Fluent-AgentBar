# Plan 002: Make WinUI canonical and quarantine legacy C++

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the next
> step. If anything in the "STOP conditions" section occurs, stop and report.
> When done, update the status row for this plan in `plans/README.md`.
>
> **Drift check (run first)**:
> `git diff --stat d48750f..HEAD -- AGENTS.md README.md build.ps1 docs src winui`
> If any in-scope file changed since this plan was written, compare the current
> state excerpts below against the live code before proceeding.

## Status

- **Priority**: P1
- **Effort**: M
- **Risk**: MED
- **Depends on**: `plans/001-winui-verification-baseline.md`
- **Category**: migration, architecture, dx
- **Planned at**: commit `d48750f`, 2026-06-13

## Why this matters

The repo has two implementations: a large legacy C++ app in `src/main.cpp` and a
new WinUI app in `winui/`. Several instructions still tell agents to read or
reuse legacy code, which keeps new work dependent on a path the maintainer wants
to leave behind. This plan makes WinUI the only active product path and turns
legacy C++ into an explicitly quarantined reference.

## Current state

- `README.md:1` names the product "Fluent AgentBar"; `winui/FluentAgentBar.csproj`
  is the active project.
- `AGENTS.md:9` tells UI implementers to reuse palette and drawing helpers in
  `src/main.cpp`.
- `docs/design/fluent-windows.md:3` still says "Codex SWBar Windows".
- `docs/PORTING.md:5` describes the original C++ porting goal and
  `%APPDATA%\Codex SWBar Windows`.
- `build.ps1:13-18` builds the legacy C++ executable and parser tests.
- `src/main.cpp` is about 216 KB and contains old Win32 app logic, including its
  own single-instance mutex and secondary taskbar enumeration.

Design constraints to preserve:

- UI must still follow `docs/design/fluent-windows.md`: Windows 11 Fluent,
  Segoe UI typography, subtle 1px strokes, Acrylic-like flyouts, Mica-like
  settings, 4/8/12/16 spacing.
- Do not redesign UI in this plan.

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Verify WinUI | `.\scripts\verify-winui.ps1` | exit 0 |
| Check stale project names | `rg -n "CodexSwbarWinUI|Codex-SWBar-Windows|namespace CodexSwbarWinUI" docs README.md AGENTS.md .claude` | no active-doc matches outside archived legacy docs |
| Check legacy references | `rg -n "src/main.cpp|build.ps1" README.md AGENTS.md docs scripts .github` | only explicitly marked legacy/archive references |

## Scope

**In scope**:

- `AGENTS.md`
- `README.md`
- `docs/design/fluent-windows.md`
- `docs/PORTING.md`
- `docs/specs/*.md`
- `build.ps1`
- `scripts/verify-winui.ps1`
- Optional: `docs/legacy/` or `legacy/` metadata
- Optional: `src/README.md` if `src/` remains

**Out of scope**:

- Any behavior change in `winui/*.cs` or XAML.
- Deleting `src/` unless the operator explicitly approves deletion after this
  plan starts.
- Porting missing C++ features. Missing parity gets its own plan, not hidden in
  this one.

## Git workflow

- Branch: `advisor/002-winui-canonical-quarantine-legacy`
- Commit message: `Make WinUI the canonical implementation`
- Do not push or open a PR unless the operator instructed it.

## Steps

### Step 1: Replace legacy design dependency in agent instructions

Update `AGENTS.md` so UI work no longer depends on `src/main.cpp`. Replace the
current instruction to reuse `src/main.cpp` palette/drawing helpers with:

- Read `docs/design/fluent-windows.md` before UI changes.
- Reuse WinUI theme resources, existing XAML styles, and existing WinUI brush
  helpers such as `ProviderIcons`, `TaskbarTheme`, and the current XAML patterns.
- Treat `src/main.cpp` as legacy reference only, not a source of truth.

**Verify**:
`rg -n "src/main.cpp|legacy reference|docs/design/fluent-windows.md" AGENTS.md`
shows no active instruction to reuse C++ helpers.

### Step 2: Mark C++ as legacy in README and tooling

Update `README.md` to state:

- Active app: `winui/FluentAgentBar.csproj`.
- Active verification: `.\scripts\verify-winui.ps1`.
- `build.ps1` and `src/` are legacy C++ and should not be used for new WinUI
  validation.

If `build.ps1` remains, add a top comment inside it:

```powershell
# Legacy C++ build only. Do not use this as the verification command for WinUI changes.
```

Do not change its behavior in this plan.

**Verify**:
`rg -n "Legacy C\\+\\+|verify-winui|FluentAgentBar.csproj" README.md build.ps1`
shows the new wording.

### Step 3: Archive or label old specs

Create `docs/legacy/README.md` or add a clear header to `docs/PORTING.md` and
older specs saying they describe historical porting work and may contain old
project names. Then update the active specs to point at
`winui\FluentAgentBar.csproj` where they are still relevant.

Do not rewrite product behavior in old specs unless the current WinUI code and
README already prove the new behavior.

**Verify**:
`rg -n "CodexSwbarWinUI|Codex-SWBar-Windows|namespace CodexSwbarWinUI" docs README.md AGENTS.md`
has no matches in active docs; matches under `docs/legacy` are acceptable only
if clearly labeled legacy.

### Step 4: Create a legacy retirement checklist

Add `docs/legacy-retirement.md` with a checklist:

- WinUI verification baseline exists and passes.
- WinUI has single-instance behavior.
- WinUI has multi-taskbar behavior or the README no longer promises it.
- Config migration from `Codex SWBar Windows` to `Fluent AgentBar` is tested.
- No active docs or scripts require `src/main.cpp`.
- Decision point: delete `src/` and `build.ps1`, or move them to a separate
  archive branch/repository.

**Verify**:
`rg -n "single-instance|multi-taskbar|src/main.cpp|delete" docs/legacy-retirement.md`
shows the checklist.

### Step 5: Run WinUI verification

Run the WinUI baseline from plan 001.

**Verify**:
`.\scripts\verify-winui.ps1` exits 0.

## Test plan

This is a docs/tooling migration. The test is that WinUI verification still
passes and no active instructions point future agents at C++ as authoritative.

## Done criteria

- [ ] `.\scripts\verify-winui.ps1` exits 0.
- [ ] `AGENTS.md` no longer tells agents to reuse `src/main.cpp`.
- [ ] Active docs use `FluentAgentBar.csproj`, not `CodexSwbarWinUI.csproj`.
- [ ] Legacy docs are clearly labeled or moved under `docs/legacy/`.
- [ ] `build.ps1` is labeled legacy if it remains.
- [ ] `plans/README.md` status row updated.

## STOP conditions

Stop and report if:

- The operator wants immediate deletion of `src/` but there is no passing WinUI
  verification baseline yet.
- Any active release or packaging process still depends on `build.ps1`.
- You discover a current WinUI feature is only documented in legacy specs and
  cannot be restated accurately from the WinUI code.

## Maintenance notes

After this lands, reviewers should reject new WinUI changes that cite
`src/main.cpp` as the implementation pattern. If legacy deletion is later
approved, use `docs/legacy-retirement.md` as the gate.
