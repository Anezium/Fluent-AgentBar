# Agent Instructions

## UI Design Rules

Before modifying any UI, read:

- docs/design/fluent-windows.md

All UI work must follow the Windows Fluent design system described there.

When implementing UI:

1. Reuse WinUI theme resources, existing XAML styles, existing WinUI brush helpers such as `ProviderIcons` and `TaskbarTheme`, and the current XAML spacing/radius patterns. Treat `src/main.cpp` as legacy reference only, not a source of truth.
2. Do not introduce random colors, shadows, border radii, font sizes, or gradients.
3. Do not create generic glassmorphism. Main app/settings surfaces should feel Mica-like; flyouts should feel Acrylic-like.
4. Prefer Windows 11 Fluent 2 patterns: subtle borders, Segoe UI typography, compact spacing, calm accent fills, and dense but breathable command surfaces.
5. If a task asks for a new component, first map it to one of the existing patterns in `docs/design/fluent-windows.md`.
6. After implementation, review the UI against the design checklist and fix obvious drift.

## UI Review Checklist

Before finishing a UI task, verify:

- Does it look like a Windows 11 utility/flyout?
- Are colors routed through the shared Fluent palette or documented design tokens?
- Are borders subtle 1px strokes?
- Are shadows and DWM backdrops soft and native-feeling?
- Is spacing consistent with 4 / 8 / 12 / 16 / 20 / 24px increments?
- Is typography Segoe UI Variable / Segoe UI?
- Are flyouts using Acrylic-like blur/translucency when enabled?
- Does the component avoid macOS, mobile, Bootstrap, Tailwind-default, neon, or generic dashboard aesthetics?
