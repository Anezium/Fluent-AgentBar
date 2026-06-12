# Windows Fluent Design System

Codex SWBar Windows should feel like a native Windows 11 / Fluent 2 taskbar utility, not like a generic web dashboard.

## Visual Target

The closest references are:

- Windows 11 Quick Settings flyout
- Windows 11 Settings app cards
- Fluent Flyout-style Acrylic panels
- Modern Windows command surfaces

Do not create generic glassmorphism, macOS-style blur, neon/cyberpunk UI, mobile app UI, Bootstrap-looking controls, or Tailwind dashboard cards.

## Materials

Use these surface types:

1. Taskbar pill / compact presence
   - Mica-like dark translucent-tinted rectangle
   - Rounded but not over-rounded
   - Subtle 1px border
   - Calm hover and pressed states
   - Readable at small taskbar sizes

2. Flyouts / transient command surfaces
   - Acrylic-like material when enabled
   - Background blur through DWM/accent composition
   - Slight tint and thin border
   - Soft, native-feeling elevation
   - Should feel close to Windows 11 Quick Settings

3. Settings window
   - Mica-like app base
   - Slightly elevated cards
   - Quiet, dense layout
   - No marketing hero typography

4. Cards and rows
   - Background slightly lighter than the base
   - Border low contrast
   - Hover slightly brighter
   - Active/focused state via accent border or subtle accent fill

## Typography

Use:

- Segoe UI Variable when available
- Segoe UI / system fallback
- Segoe Fluent Icons for icon buttons

Text rules:

- Taskbar pill labels: 12-14px equivalent at 96 DPI
- Flyout titles: 14-16px equivalent
- Body: 12-13px equivalent
- Secondary metadata: 11-12px equivalent
- Avoid oversized headings
- Avoid marketing website typography

## Geometry

Use Windows 11-like geometry:

- Flyout/main panel radius: 12-16px
- Cards/buttons radius: 6-10px
- Tiny pills/chips radius: fully rounded only when appropriate
- Border width: 1px
- Spacing scale: 4 / 8 / 12 / 16 / 20 / 24px
- Dense but breathable layout

## Color Tokens

The native C++ implementation uses `COLORREF` values in `src/main.cpp`. Keep them aligned with these roles:

- App base: dark `Rgb(32, 32, 36)` / light `Rgb(243, 243, 243)`
- Surface: dark `Rgb(43, 43, 48)` / light `Rgb(251, 251, 251)`
- Surface hover: slightly brighter than surface
- Surface active: subtle accent tint
- Border subtle: low-contrast 1px line
- Text primary: near-white or near-black, never pure black as a design default
- Text secondary: muted but readable
- Accent: calm Windows blue, roughly `Rgb(96, 205, 255)` in dark mode
- Danger and success are reserved for status, not decoration

Prefer adding named roles to `FluentPalette` over scattering hardcoded colors through paint functions.

## Motion

Motion must be subtle:

- 120-180ms transitions where available
- Ease-out feel
- Fade plus slight scale/translate for future animated flyouts
- No bouncy animation
- No exaggerated hover movement

## Components

### Main Account Taskbar Pill

The primary Codex account appears as a compact horizontal taskbar rectangle:

- App icon or initials on the left
- Profile name
- Status/plan or weekly summary
- Primary usage percent
- Two thin progress bars when both daily/weekly quota data exists
- Single thin progress bar when only one quota exists
- Must remain readable in constrained taskbar space

### Account / Usage Flyout

The flyout should be an Acrylic-style command surface:

- Provider/account list
- Active or healthy accounts indicated with subtle status color
- Usage rows grouped by provider
- Refresh / config / settings actions at the bottom
- Keyboard escape closes the flyout
- Compact enough to feel like a taskbar flyout, not a dashboard page

### Usage Bars

Progress bars should be thin, calm, and native-looking:

- 3-6px height
- Rounded track
- Solid accent fill
- No rainbow gradients
- No oversized labels inside the bar

## Strict Visual Anti-Patterns

Never use:

- Pure black backgrounds as a design default
- Bright blue everywhere
- Heavy shadows
- Thick borders
- Big colorful gradients
- macOS traffic-light window controls
- Over-rounded cards everywhere
- Generic dashboard layout
- Bootstrap-looking buttons
- Mobile bottom navigation
- Random icons without alignment
- Inconsistent spacing
- Text smaller than 11px equivalent at 96 DPI
