# Historical Spec: Multi-provider profiles, per-profile Claude accounts, USD cost calculation

Legacy reference only. This spec records a past implementation task and may not
match the current WinUI code exactly.

Scope: `winui/` project. `dotnet build winui/FluentAgentBar.csproj` must pass (0 warnings).
Style: match existing code. **Do NOT redesign `SettingsWindow.xaml` / `SettingsWindow.xaml.cs` UI** — the
frontend pass happens separately. You may make minimal mechanical edits there only to keep the build
green after the schema change (rename property references, adapt handlers).

## Part 1 — Config schema v2 (`winui/AppConfigStore.cs`)

Replace the codex-only profile model with provider-aware profiles:

```json
{
  "refreshIntervalSeconds": 300,
  "flyoutStyle": "acrylic",
  "profiles": [
    { "provider": "codex",  "label": "Main",     "home": "%APPDATA%\\Fluent AgentBar\\profiles\\main", "enabled": true },
    { "provider": "claude", "label": "Personal", "home": "%USERPROFILE%\\.claude",                          "enabled": true }
  ]
}
```

- New `ProfileConfig { string Provider; string Label; string Home; bool Enabled; }`.
  `Provider` normalized to `"codex"` or `"claude"` (default `"codex"`).
  `Home` semantics: CODEX_HOME for codex profiles, CLAUDE_CONFIG_DIR for claude profiles.
- `AppConfig.Profiles` (List<ProfileConfig>) replaces `CodexProfiles` + `Claude`.
- **Migration in `Load()`/`Normalize()`**: if the JSON contains legacy `codexProfiles`, map each to a
  codex `ProfileConfig`. If legacy `claude.enabled == true`, append one claude profile
  (label `"Personal"`, home `%USERPROFILE%\.claude`, enabled). Write back the migrated shape on next Save.
  Keep reading both shapes (deserialize into a private DTO with all legacy fields, then map).
- Helper `DefaultHomeForLabel(string provider, string label)`:
  - codex → `{ConfigDirectory}\profiles\{slug}` (existing slug logic, keep `CodexProfileHomeForLabel`
    behavior; rename or delegate)
  - claude → first claude profile defaults to `%USERPROFILE%\.claude` (reuses the user's existing CLI
    login); additional claude profiles → `{ConfigDirectory}\profiles\claude-{slug}`.
- Normalize: at least one profile (default codex "Main"); empty labels/homes fixed as today.

## Part 2 — Per-profile usage fetching (`winui/UsageService.cs`, `winui/ClaudeUsageService.cs`)

- `FetchProvidersAsync`: group enabled profiles by provider. All codex profiles → one `ProviderUsage("Codex", …)`
  card (existing app-server fetch, CODEX_HOME = profile home). All claude profiles → one
  `ProviderUsage("Claude", …)` card with one `ProfileUsage` per claude profile (Label = profile label).
- `ClaudeUsageService`: parametrize by config directory — `FetchAsync(string configDir, CancellationToken)`.
  Credentials at `{configDir}\.credentials.json`. Keep the 5-min cache, backoff and in-memory refresh,
  but **per configDir** (e.g. ConcurrentDictionary<string, state>). `CLAUDE_CONFIG_DIR` env var still wins
  over `%USERPROFILE%\.claude` only for the legacy default-path helper; per-profile dirs come from config.

## Part 3 — Profile login helper (new file `winui/ProfileLoginService.cs`)

Static helper the UI will call: `ProfileLoginService.StartLogin(ProfileConfig profile)`.

- codex: current behavior — run `codex login` with env `CODEX_HOME={home}`, hidden window is fine
  (it opens the browser itself).
- claude: run `claude /login` **in a visible console window** (interactive TUI: theme prompt, login
  method selection) with env `CLAUDE_CONFIG_DIR={home}`. Use `cmd.exe /K` so output stays visible, e.g.
  `cmd /K "set CLAUDE_CONFIG_DIR={home} && claude /login"` via UseShellExecute=false CreateNoWindow=false
  (or ProcessStartInfo.Environment + `cmd /K claude /login`). Create the home directory first.
- Return bool started + error message out-param or throw; keep it simple, UI shows the dialog.

## Part 4 — USD cost calculation (`winui/TokenStatsService.cs`)

Extend token scanning with per-model cost:

- New static pricing table (USD per 1M tokens): `(input, output, cacheRead, cacheWrite)` matched by
  normalized model-name prefix (strip `openai/` prefix, date suffixes, lowercase):

  | prefix | in | out | cacheRead | cacheWrite |
  |---|---|---|---|---|
  | `claude-fable-5`, `claude-mythos-5` | 10.00 | 50.00 | 1.00 | 12.50 |
  | `claude-opus-4` | 5.00 | 25.00 | 0.50 | 6.25 |
  | `claude-sonnet-5` through 2026-08-31 | 2.00 | 10.00 | 0.20 | 2.50 |
  | `claude-sonnet-5` from 2026-09-01 | 3.00 | 15.00 | 0.30 | 3.75 |
  | `claude-sonnet-4` | 3.00 | 15.00 | 0.30 | 3.75 |
  | `claude-haiku-4` | 1.00 | 5.00 | 0.10 | 1.25 |
  | `gpt-5.5`, `gpt-5-5` | 5.00 | 30.00 | 0.50 | 0 |
  | `gpt-5.4`, `gpt-5-4` | 2.50 | 15.00 | 0.25 | 0 |
  | `gpt-5.1`, `gpt-5-1` | 1.25 | 10.00 | 0.125 | 0 |
  | `gpt-5` (fallback) | 1.25 | 10.00 | 0.125 | 0 |
  | `codex-mini` | 1.50 | 6.00 | 0.375 | 0 |

  Keep the table in one obvious place with a comment pointing at
  `https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json` as the
  source to refresh from. Unknown model → tokens counted, cost contribution 0.

- Claude lines: `message.model` is on each line — price each deduped usage directly.
  Cost = in*input + out*output + cacheRead*cacheRead + cacheCreation*cacheWrite (per-token = table/1e6).
- Codex sessions: counters are cumulative. Track the current model from the most recent line carrying a
  `model` field (e.g. `turn_context` events); on each `total_token_usage` snapshot compute the **delta**
  vs the previous snapshot in the same file and price the delta with the current model
  (clamp negative deltas to 0). Sum deltas; this replaces "last snapshot only" for cost, while the token
  totals can keep using the final snapshot.
- **Also scan the default Codex home** `%USERPROFILE%\.codex` (sessions + archived_sessions) in addition
  to profile homes, skipping duplicates when a profile home equals it (compare full expanded paths,
  OrdinalIgnoreCase). This is why Codex tokens currently show nothing: the user's real sessions live there.
- Claude token scan: also include per-profile claude homes from config (`{home}\projects`), dedup dirs.
- `TokenStats` record gains `double CostUsd`; `Summary` becomes
  `"Today {in} in · {out} out · ${cost:0.00}"` (omit the cost segment when CostUsd == 0).

## Acceptance

- Build passes; legacy config.json migrates losslessly (verify by writing a unit-style check or manual test).
- Flyout shows: Codex card (profiles as before), Claude card with one row per claude profile.
- Token line shows for Codex (default ~/.codex sessions found) with non-zero cost when model known.
- Existing Claude CLI login at `%USERPROFILE%\.claude` works as the default claude profile without re-login.
- No raw emails; no credential writes; SettingsWindow only mechanically adapted.
- Leave changes uncommitted in the working tree (committing fails in your sandbox); summarize what changed.
