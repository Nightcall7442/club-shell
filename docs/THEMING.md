# ClubShell — Theming

Status: descriptive. The schema is the `Theme` record in `src/ClubShell.Contracts/Pc/PcInfo.cs`
(mirrored to `crates/protocol` and `packages/contracts-ts`); `ARCHITECTURE.md` §12.4 is the normative
file layout. This document explains how a theme file becomes pixels.

A theme is one JSON file, `C:\ProgramData\ClubShell\themes\<name>.json`. The Shell reads it
(`settings_get_theme`), turns the palette into CSS custom properties on `<html>`, and Tailwind utilities
read those properties. Switching themes never reloads the page.

---

## 1. Theme file schema

`Theme` (C# record, camelCase on the wire):

| Field | Type | Required | Default (`normalizeTheme`) | Meaning |
|-------|------|----------|-----------------------------|---------|
| `version` | `int` | yes | `1` | Schema version. Only `1` exists. |
| `name` | `string` | yes | — | Theme id; **must equal the file name** without `.json`. Bare name only (no `/`, `\`, `.`) — `ShellConfig::validate` and `validate::bare_name` reject anything else. A mismatch is logged by `load_theme` but the file is still used. |
| `displayName` | `string` | yes | — | Human-readable name shown in the picker. |
| `colors` | `ThemeColors` | yes | default palette per key | Eight colours, each `#RRGGBB` or `#RRGGBBAA` (`#RGB` is also accepted by `hexToRgb`). Alpha is ignored; Tailwind supplies opacity. |
| `colors.bg` | hex | yes | `#0C0C0E` | Page background. |
| `colors.surface` | hex | yes | `#161619` | Cards, panels, the sidebar's popovers (`.glass`, `.glass-strong`). |
| `colors.primary` | hex | yes | `#F4F4F5` | Primary actions, focus ring, selection. Text on it is `--c-on-primary`, derived by luminance (dark on light primaries, white otherwise). |
| `colors.accent` | hex | yes | `#7AA2F7` | The one colour in the chrome: bonuses, club-account and booked-seat marks, timer warning, warnings' bar in overlays. Text on it is `--c-on-accent`. |
| `colors.text` | hex | yes | `#FAFAFA` | Primary text. |
| `colors.muted` | hex | yes | `#8E8E96` | Secondary text, scrollbars. |
| `colors.danger` | hex | yes | `#EF4444` | Errors, session timer warning/critical. |
| `colors.success` | hex | yes | `#22C55E` | Success states. |
| `radius` | `int` px | yes | `10` | Base corner radius; Tailwind derives `sm`/`md`/`lg`/`xl`/`2xl` from it. |
| `font` | `string` | yes | `"Inter"` | Font family name; falls back to the bundled `Inter Variable` (`@fontsource-variable/inter`, so the default renders the same on a PC that never had Inter installed), then `system-ui`, `Segoe UI`, `sans-serif`. Empty → default. |
| `backgroundVideo` | `string \| null` | no | `null` | Looping muted video behind the UI. Path relative to the data directory (`themes/assets/…`) or absolute URL. |
| `wallpaper` | `string \| null` | no | `null` | Still image behind the UI (also the video poster). Same path rules. `null` = the plain `bg` backdrop (see §3). |
| `blur` | `int` px | yes | `0` | `0` = flat, opaque panels (the default). Above `0` the theme opts into translucent panels: `applyTheme` sets `data-glass` and `.glass` / `.glass-strong` blur what is behind them by this much. Also softens the veil over a wallpaper. |
| `animations` | `bool` | yes | `true` | `false` sets `data-animations="false"`, which turns off every CSS animation/transition (`animations.css`) and framer-motion durations. |

Unknown keys are ignored on read. Missing optional fields are filled by `normalizeTheme` in
`apps/shell/src/theme/themes.ts`; a malformed colour never blanks the UI (`hexToRgb` returns `null` and
the default colour for that key is used).

Validation on the Rust side (`commands/settings.rs`): a missing or invalid file falls back to the embedded
`config/themes/default.json` (`DEFAULT_THEME_JSON`, `default_theme()`), because the kiosk must always
have a theme. On the Agent side (`PolicyHandlers.SetAsync`, the `settings.set` handler) `settings.set{theme}` is rejected with
`validation` unless the name is ≤ 64 characters, has no invalid file-name characters and is listed in
`ShellSettingsStore.AvailableThemes` (files under `paths.themes` plus the configured theme).

---

## 2. From JSON to CSS

### 2.1 Colours → `--c-*` RGB triplets

`applyTheme(theme)` (`theme/themes.ts`) writes, for each key in `THEME_COLOR_KEYS`
(`bg, surface, primary, accent, text, muted, danger, success`):

```
--c-<key>: R G B        e.g. --c-primary: 59 130 246
```

plus two derived shades: `--c-primary-hover` (`shade(primary, +0.12)`, mixed towards white) and
`--c-primary-active` (`shade(primary, −0.12)`, towards black). Space-separated channels are the trick
that makes Tailwind opacity modifiers work:

```ts
// tailwind.config.ts
const themeColor = (name: string): string => `rgb(var(--c-${name}) / <alpha-value>)`;
colors: { bg: themeColor('bg'), surface: themeColor('surface'), primary: themeColor('primary'),
          accent: themeColor('accent'), text: themeColor('text'), muted: themeColor('muted'),
          danger: themeColor('danger'), success: themeColor('success') }
```

So `bg-primary/50` compiles to `rgb(var(--c-primary) / 0.5)` and follows the active theme. Plain CSS in
`tokens.css` / `animations.css` uses the same variables (`rgb(var(--c-surface) / 0.6)`, glow shadows,
scrollbar colours). `tokens.css` defines the defaults on `:root` so the boot splash is already themed
before the first `applyTheme`.

### 2.2 Radius, font, blur

| Theme field | CSS variable | Tailwind |
|-------------|--------------|----------|
| `radius` | `--radius: <n>px` | `rounded` = `var(--radius)`; `rounded-sm` ×0.5, `rounded-md` ×0.75, `rounded-lg` ×1, `rounded-xl` ×1.5, `rounded-2xl` ×2 |
| `font` | `--font: "<family>"` (quotes stripped from the value) | `font-sans` = `['var(--font)', 'system-ui', 'Segoe UI', 'sans-serif']` |
| `blur` | `--blur: <n>px`, `data-glass` when `> 0` | under `data-glass`, `.glass` uses `blur(var(--blur))` at 55 % surface opacity, `.glass-strong` `blur(calc(var(--blur) * 1.5))` at 90 %; otherwise both are opaque `surface` with a hairline border |

### 2.3 Data attributes

`applyTheme` also sets on `<html>`: `data-theme="<name>"` (for theme-specific CSS hooks),
`data-animations="true|false"`, `data-glass` (only when `blur > 0`), the `dark` class (Tailwind `darkMode: 'class'`; every theme is dark), and
updates `<meta name="theme-color">` to `colors.bg`. `animations.css` contains
`:root[data-animations="false"] *:not(.anim-spin) { animation: none; transition: none }` (spinners keep
spinning) and honours `prefers-reduced-motion` the same way.

### 2.4 Typography and layout tokens (not themeable)

`tokens.css` also defines the fluid type scale (`--fs-base: clamp(16px, 0.9375vw, 24px)`, `--fs-xs` …
`--fs-display`), layout (`--sidebar-w`, `--gutter`, `--gap`, `--card-cover-w`), motion
(`--dur-fast/base/slow`, easings) and elevation (`--hairline`, `--shadow-float` for popovers and modals only,
`--shadow-glow`, which is now a plain 2 px focus ring). These are design
constants, not theme fields; a theme only influences them through the colour variables they reference.

---

## 3. Background assets

`wallpaper` and `backgroundVideo` are rendered by `components/layout/Background.tsx`
(colour → wallpaper with a slow pan → `VideoBackground` → gradient veil).

With neither set (the default theme) the backdrop is the plain `bg` colour: the content carries the screen, not
the backdrop. The lock screen does not use this backdrop when the club has art:
it plays the still images of `shell.json → ads.playlist` (the attract screen's playlist) under a dark veil.

Paths are
resolved by `useResolvedAsset` (`components/media/GameArtwork.tsx`) → `assetUrl(path)` (`lib/tauri.ts`):

* Absolute `https:` / `data:` / `blob:` / `asset:` URLs pass through unchanged (the CSP allows `https:`
  for `img-src` and `media-src`).
* Relative paths are sent to `kiosk_asset_url` (`src-tauri/src/kiosk/commands.rs`), which validates
  them against the data directory (`resolve_asset`: no `..`, no absolute components, first segment must be
  `themes/…` or `cache/media/…`), checks the file exists (`notFound` otherwise) and returns the Tauri
  asset-protocol URL — on Windows `http://asset.localhost/<encodeURIComponent(full path)>`, the same
  shape as `convertFileSrc(path, "asset")`.
* `tauri.conf.json → app.security.assetProtocol.scope.allow` is `C:/ProgramData/ClubShell/themes/**`
  and `C:/ProgramData/ClubShell/cache/media/**`; anything else is refused by WebView2 even if a URL
  were forged.

Conventions: put assets under `C:\ProgramData\ClubShell\themes\assets\` and reference them as
`themes/assets/<file>` (forward slashes; backslashes are accepted and normalised). Recommended formats:
wallpaper JPEG/WebP 1920×1080 (≤ 500 KB), video H.264 MP4 1080p ≤ 30 s loop, muted, ≤ 15 MB.
`VideoBackground` renders nothing when `animations` is `false` or the tab is hidden; the wallpaper
stays.

If `CLUBSHELL_DATA_DIR` is used in development, note that the asset-protocol scope in `tauri.conf.json`
is fixed to `C:/ProgramData/ClubShell`; use `https:` URLs or copy assets there when testing videos under
`tauri dev`.

---

## 4. Built-in themes

Both ship in `config/themes/` (installed to `C:\ProgramData\ClubShell\themes\` by the Agent MSI) and are
also embedded in the frontend (`builtinThemes` in `theme/themes.ts`) and, for `default`, in the Rust
binary (`DEFAULT_THEME_JSON`). `default.json` is mandatory and always resolvable.

### 4.1 `default` — "ClubShell Graphite"

A strict, minimal dark theme: flat graphite panels with hairline borders, no glass, glow, grain or tilt; white
for actions, one muted blue for the few things that must stand out; the game art is the only colour on screen.

| Key | Hex | Role |
|-----|-----|------|
| bg | `#0C0C0E` | neutral near-black background |
| surface | `#161619` | flat graphite panels |
| primary | `#F4F4F5` | white actions (dark text via `--c-on-primary`) |
| accent | `#7AA2F7` | muted blue: bonuses, club account, booked seats, timer warning |
| text | `#FAFAFA` | near-white |
| muted | `#8E8E96` | neutral grey |
| danger | `#EF4444` | red |
| success | `#22C55E` | green |

`radius 10`, `font "Inter"`, `blur 0` (flat panels), `animations true`, no wallpaper (plain backdrop), no video.

### 4.2 `neon` — "Neon Night"

| Key | Hex | Role |
|-----|-----|------|
| bg | `#07060F` | near-black violet |
| surface | `#120F24` | panels |
| primary | `#A855F7` | purple actions |
| accent | `#F0ABFC` | pink highlight |
| text | `#FAF5FF` | lavender-white |
| muted | `#8E85B3` | dusty violet |
| danger | `#FB7185` | rose |
| success | `#34D399` | mint |

`radius 16`, `font "Rajdhani"` (install the font on the PC or the fallback chain applies), `blur 20`,
`animations true`, wallpaper `themes/assets/neon-wallpaper.jpg`, video `themes/assets/neon-loop.mp4`.

---

## 5. Server-provided themes

The server can list themes in `GET /agents/{pcId}/config` → `AgentServerConfig.Themes`, an array of
`ThemeRef { name, url, sha256 }` (`PcInfo.cs`), and can select one through
`AgentServerConfig.Shell.Theme` (`ShellConfigOverride`). The intended flow:

1. The Agent downloads each `ThemeRef.url` (a theme JSON, optionally accompanied by asset files) into
   `themes\<name>.json`, verifying the lower-case hex `sha256`. `LocalCache`
   (`src/ClubShell.Agent/Storage/LocalCache.cs`, `GetOrDownloadAsync(url, sha256, ct)`) is the
   download/verify/atomic-rename primitive for this and for covers/ads.
2. `ShellSettingsStore.AvailableThemes` lists `themes\*.json`, so the new theme appears in
   `settings.get → availableThemes` and in the picker.
3. `ShellConfigOverride.Theme` is merged into `shell.json → theme`; the Shell reads it on the next
   `settings_get`, and the `settings.theme` store subscription reloads the theme (§6).
4. The server `refreshConfig{ themes: true }` command (`ServerCommandType.RefreshConfig`,
   `CommandReceiver.cs`) is the trigger for a re-download.

Current implementation status: the contract, the `LocalCache` primitive, the listing and the picker are in
place; `CommandReceiver` reports `themes` as refreshed but the loop that walks `ThemeRef` entries into
`themes\` is not wired yet (`CommandReceiver.cs` notes that themes have no Agent-side cache and the Shell
re-fetches them on demand). Until it lands, deploy theme files with the installer or by copying them into
`themes\` (see `ROADMAP.md`, v1.1). Files placed there manually are picked up without a restart because
`settings_list_themes` and `settings_get_theme` read the directory on every call.

---

## 6. Live switching

```
ThemePicker (screens/Profile/Settings.tsx)
  └─ useThemeStore.setTheme(name)                       store/theme.ts
       ├─ load(name) → loadTheme(name)                   theme/themes.ts
       │     └─ api.settings.getTheme(name)              Tauri: settings_get_theme → themes\<name>.json
       │        (fallback: builtinThemes[name] → DEFAULT_THEME)
       ├─ apply(theme) → applyTheme(theme)               CSS variables on <html> — visible immediately
       └─ useSettingsStore.setTheme(name)                Tauri: settings_set { theme }
             └─ Agent: settings.set → validates against AvailableThemes, persists shell.json
             └─ Rust: emits kiosk://themeChanged (payload Theme) to every window
                   ├─ main window: events.onKiosk('themeChanged') → theme.apply(p.theme)
                   ├─ overlay window: bootstrapWindow() listener → theme.apply
                   └─ ads windows: same
```

* `setTheme` applies first and persists second; if persisting fails the theme stays applied, the error is
  re-thrown, and `ThemePicker` shows it and reloads the previous theme.
* `store/index.ts` also subscribes to `settings.theme`: when the value changes from elsewhere (server
  override, `sys_set_locale`-style pushes, another window) and differs from `theme.name`, it calls
  `theme.load(name)`. This is what makes a server-selected theme take effect without user action.
* `kiosk://themeChanged` carries the full `Theme` object, so the secondary windows never read the file
  themselves.
* Everything is CSS-variable driven; there is no re-mount, no flash, and running animations continue.

---

## 7. Creating a custom theme

1. Copy `config/themes/default.json` to `C:\ProgramData\ClubShell\themes\<name>.json`. `<name>` is
   lower-case ASCII, no spaces or dots (e.g. `arena-red`).
2. Set `"name": "<name>"` (identical to the file name) and a `displayName`.
3. Pick the eight colours. Start from `bg` and `surface` (surface should be 4–8 % lighter than bg so
   panels separate from the page — at 55 % opacity too, if the theme sets `blur`), then `text`/`muted`, then `primary`/`accent`, then the
   semantic `danger`/`success`.
4. Choose `radius` (8–20 px reads well at 1080p), `blur` (0 = flat panels; keep 0 on weak GPUs), `font` (must be installed on
   the PC; `Inter` and `Segoe UI` are safe), `animations`.
5. Drop `wallpaper` / `backgroundVideo` files into `themes\assets\` and reference them as
   `themes/assets/<file>`, or leave `null`.
6. Validate the JSON (`python -m json.tool <name>.json`). Open the Shell settings → *Theme*: the new
   entry appears in the picker; select it. Check the logs for `theme name does not match its file name` or
   `theme file invalid; using embedded default`.
7. To make it the default for the PC set `"theme": "<name>"` in `shell.json` (or via the server
   override). To ship it with the installer add it to `installer/wix/Components.wxs` next to
   `default.json` / `neon.json`.

Kiosk-specific advice: players sit 60–80 cm from a 24–27" panel under club lighting; avoid pure `#000`
backgrounds (OLED smear, banding on cheap panels) and very saturated `text` colours.

### 7.1 Contrast and accessibility

The UI relies on the palette for every text/background pair, so the theme author owns contrast:

| Pair | Minimum (WCAG 2.1) | Default theme | Neon theme |
|------|--------------------|---------------|------------|
| `text` on `bg` | 4.5:1 (AA body text) | 17.4:1 | 18.8:1 |
| `text` on `surface` | 4.5:1 | 15.7:1 | 17.5:1 |
| `muted` on `surface` | 4.5:1 for hints, 3:1 for decorative | 5.6:1 | 5.5:1 |
| `text` on `primary` (buttons) | 4.5:1 body, 3:1 large/bold | 3.3:1 | 3.7:1 |
| `primary` on `surface` (focus ring, links) | 3:1 (non-text) | 4.7:1 | 4.7:1 |
| `danger` on `bg` (timer warning) | 3:1 (large) | 5.1:1 | 7.5:1 |

Ratios are computed with the WCAG relative-luminance formula; verify your own palette with a checker (e.g. the WebAIM contrast checker) before deploying. Button labels are bold and ≥ 18 px, so 3:1 on `primary` is the bar to clear. Keep
`primary` and `danger` distinguishable for red–green colour blindness (the UI also uses icons and text,
never colour alone, but the session timer is red-coded). The focus ring is `--shadow-glow` built from
`primary`: make sure `primary` is visible against `surface`, since gamepad/keyboard users navigate by it.
`animations: false` is the reduced-motion switch; `blur: 0` improves legibility on low-contrast panels.

---

## 8. Localising theme names

`displayName` is a single string and is rendered as-is by `ThemeSwatch` (`Settings.tsx`), so pick a name
that works in Russian, Uzbek and English (brand names, "Neon", "Arena") rather than a sentence. The i18n
bundles carry `settings.themeDefault` ("ClubShell Default") and `settings.themeNeon` ("Neon Night") for the
two built-ins; they are currently unused by the picker and exist so a future picker can prefer
`t('settings.theme<Pascal name>')` when the key exists and fall back to `displayName`. If you add a custom
theme and want translated names today, add `settings.theme<Name>` keys to
`C:\ProgramData\ClubShell\locales\<locale>.json` (the `kiosk_i18n_bundle` override mechanism, see
`TAURI_SHELL.md` §2.5) so the strings are ready when the picker consumes them.
