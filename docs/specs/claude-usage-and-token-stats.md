# Spec: Real Claude usage + token consumption stats (WinUI app)

Scope: `winui/` project only (`dotnet build winui/CodexSwbarWinUI.csproj` must pass, 0 warnings ideally).
Style: match existing code (file-scoped namespaces, explicit types, defensive try/catch with `Debug.WriteLine`).
UI rules: `AGENTS.md` + `docs/design/fluent-windows.md` (Fluent 2, no new colors outside existing palette).

## Context

`winui/UsageService.cs` fetches Codex usage via `codex app-server` per profile. The Claude provider is
currently a hardcoded mock (see `FetchProvidersAsync`, the `config.Claude.Enabled` block returning a
`ProfileUsage("Default", "", "", 0, 0, false, …)`). `ProfileUsage` records live in `winui/MockUsageData.cs`
and already carry `Label, Email, Plan, RemainingPercent, WeeklyPercent, IsAvailable, AccentColor` plus
computed `DetailText` (redacted email). Do not display raw emails anywhere.

## Part 1 — ClaudeUsageService (new file `winui/ClaudeUsageService.cs`)

Fetch real Claude Code usage via the OAuth token that `claude login` stores locally.

Credentials:
- Path: `%USERPROFILE%\.claude\.credentials.json`; honor `CLAUDE_CONFIG_DIR` env var (credentials file
  lives under that dir instead) — check env var first.
- JSON shape: object containing `claudeAiOauth` with `accessToken`, `refreshToken`, `expiresAt` (ms epoch),
  `scopes`. Parse case-insensitively and defensively (key casing may vary).
- If the file is missing → Claude provider shows `IsAvailable=false` (graceful, no errors surfaced).

Usage endpoint:
- `GET https://api.anthropic.com/api/oauth/usage`
- Headers: `Authorization: Bearer {accessToken}`, `anthropic-beta: oauth-2025-04-20`,
  `User-Agent: claude-code/2.0 (external, swbar)` (any claude-code-like UA), `Accept: application/json`.
- Response fields: `five_hour` and `seven_day` objects, each with `utilization` and `resets_at` (ISO8601).
  `utilization` may be 0..1 or 0..100 depending on rollout — normalize: if value <= 1.0 treat as fraction.
  Map: `RemainingPercent = clamp(100 - utilization%, 0, 100)` for `five_hour`,
  `WeeklyPercent` likewise from `seven_day`. Optional fields `seven_day_opus`, `seven_day_sonnet` exist; ignore for now.

Rate limiting (critical — this endpoint 429s aggressively):
- Cache the last successful result in memory and never call the endpoint more often than every 5 minutes,
  regardless of the app refresh interval.
- On 429 or any failure: keep returning the cached value (mark stale internally), exponential backoff
  (5 → 10 → 20 min, cap 30 min). Honor `retry-after` header when present.

Token refresh:
- If `expiresAt` passed, POST `https://platform.claude.com/v1/oauth/token` with JSON
  `{"grant_type":"refresh_token","refresh_token":…,"client_id":"9d1c250a-e61b-44d9-88ed-5944d1962f5e"}`.
- Use the refreshed access token in memory only. Do NOT rewrite `.credentials.json` (it belongs to the
  Claude CLI; clobbering it risks logging the user out).

Integration:
- In `UsageService.FetchProvidersAsync`, replace the mock Claude block with a call into
  `ClaudeUsageService`. Result: `ProfileUsage(Label: "Claude Code", Email: "", Plan: plan-if-known else "",
  RemainingPercent, WeeklyPercent, IsAvailable, MockUsageData.ClaudeAccentColor)`.
- `UsageService` owns one `ClaudeUsageService` instance (so the cache survives across refreshes); dispose
  its HttpClient in `UsageService.Dispose`.

## Part 2 — TokenStatsService (new file `winui/TokenStatsService.cs`)

Local token consumption calculator (à la CodexBar/ccusage), aggregated for "today" (local date).

Codex side:
- For each enabled profile in config, scan `{expanded CODEX_HOME}\sessions\` and `archived_sessions\`
  (date-partitioned `YYYY\MM\DD\*.jsonl`; only walk today's folder + any root-level files modified today).
- Each session JSONL contains cumulative token counters: look for the LAST line per file having
  `total_token_usage` (fallback `last_token_usage` / `info.total_token_usage`) with fields
  `input_tokens`, `cached_input_tokens` (or `cache_read_input_tokens`), `output_tokens`.
  Use the last cumulative value per file (do not sum every line — they are cumulative snapshots).
- Sum across files/profiles.

Claude side:
- Scan `%USERPROFILE%\.claude\projects\**\*.jsonl` (honor `CLAUDE_CONFIG_DIR`), only files modified today.
- Per line: `timestamp` (filter to today), `message.usage` with `input_tokens`, `output_tokens`,
  `cache_creation_input_tokens`, `cache_read_input_tokens`; dedup key = `message.id` + `requestId`
  (skip lines missing usage). Sum.

Model & API:
- `public sealed record TokenStats(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheCreationTokens)`
  with computed `string Summary` like `"Today 1.2M in · 85K out"` (format: <1000 raw, K with one decimal
  under 1M, M with one decimal above; cache counts folded into "in").
- `TokenStatsService.ComputeAsync(AppConfig config)` → `(TokenStats? codex, TokenStats? claude)`, fully
  off the UI thread, all IO wrapped so a single bad file never throws out.
- Run it inside `UsageService.FetchAndPublishAsync` (parallel to provider fetch is fine) and attach to the
  published data: add `TokenStats? Tokens` init-property to `ProviderUsage` and a computed
  `string TokensText => Tokens?.Summary ?? ""` + `bool HasTokens`.

UI (minimal, Fluent-consistent):
- In `winui/FlyoutWindow.xaml` provider card, under the header grid, add one `TextBlock`
  (`CaptionTextBlockStyle`, `TextFillColorSecondaryBrush`) bound `{x:Bind TokensText}`,
  `Visibility="{x:Bind HasTokens}"` (bool→Visibility: use x:Bind with a property returning Visibility
  instead if simpler — match existing binding style; no new converters unless needed).

## Acceptance

- `dotnet build winui/CodexSwbarWinUI.csproj` passes.
- With a logged-in Claude CLI on the machine, the flyout shows real 5h/weekly percentages for Claude.
- Without Claude credentials, Claude row shows `--` (IsAvailable=false), no crash, no log spam.
- Token line appears for providers whose local session files exist; absent otherwise.
- No raw email shown anywhere; no credentials written to disk; no endpoint polled faster than every 5 min.
- Commit the work in logical commits on `main`.
