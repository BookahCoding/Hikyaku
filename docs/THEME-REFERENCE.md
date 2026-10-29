# Theme reference (source-backed; applied to WPF in Checkpoint 3)

**Pin:** BookahHermes `5f00986b2a582e480592fd680131d501d57038ef`  
**License:** MIT (Copyright 2026 Giton22) — see sibling `LICENSE`. Attribution required if tokens/assets are copied.  
**Sources inspected (public only):**
- `shared/designsystem/.../ThemeColors.kt` (`Palette`, `Neutrals`, light/dark/black maps)
- `shared/designsystem/.../AccentPalette.kt` (Blue…Teal + **Ukiyo-e**)
- `shared/designsystem/.../HermesTheme.kt` (radii, typography roles, Geist / Geist Mono)
- `shared/designsystem/.../UkiyoeThemeTest.kt` (contrast contract)
- `shared/designsystem/.../ShishaMark.kt` (brand vermilion / paper)
- Fonts under `shared/designsystem/src/androidMain/res/font/` (`geist_*.ttf`, `geist_mono_*.ttf`)

WPF `Theme/Tokens.xaml` now maps the Ukiyo-e light/dark tables below (plus Geist fonts under `Assets/Fonts`, SIL OFL 1.1). Familiar ChatGPT-style UX remains required; do not invent a third “Japanese” palette.

## Default “modern” neutrals (AccentPalette non-Ukiyo-e)

| Token | Dark | Light |
|---|---|---|
| bg | `#0A0A0C` | `#F7F7F9` |
| surface | `#16161A` | `#FFFFFF` |
| surface2 | `#1C1C22` | `#F1F1F5` |
| surface3 | `#232329` | `#E9E9EF` |
| well | `#121215` | `#F3F3F7` |
| thumb | `#2C2C33` | (light surface) |
| sheet / elevated | `#18181D` | light surface |
| text | `#F4F4F6` | `#0A0A0C` |
| text2 | `#D4D4D8` | `#27272A` |
| text4 | `#8A8A94` | `#6B6B75` |
| text5 | `#6B6B75` | `#8E8E98` |
| danger | `#F85149` | `#CF222E` |
| success | `#3FB950` | `#1A7F37` |
| warning | `#F5A524` | `#B76E00` |
| line | white @ 7% / 12% | `#0A0A1E` @ 8% / 14% |

Pure black grounds: bg `#000000`; surfaces `#111114` / `#17171C` / `#1E1E24`; well `#0C0C0E`; sheet `#141418`.

## Herald Blue accent (default theme)

| Role | Light | Dark / black |
|---|---|---|
| accent | `#2F6BF5` | `#2F6BF5` |
| onAccent | white | white |
| accent text | `#1F56D6` | `#7AA5FF` |
| soft | blue @ 10% | `#4A84FE` @ 14% |

Other presets (hex from `AccentPalette.kt`): Violet `#7C3AED`/`#A78BFA`, Green `#137538`/`#3FB950`, Orange `#B04700`/`#F0883E`, Pink `#B0327E`/`#F778BA`, Teal `#0C6A84`/`#22D3EE`.

## Ukiyo-e accent (full surface override)

Measured woodblock-inspired set; contrast enforced in `UkiyoeThemeTest`.

| Role | Light | Dark | Black |
|---|---|---|---|
| bg | `#F4EDD9` (washi) | `#0E1424` (indigo night) | `#000000` |
| surface | `#FAF5E3` | `#151C30` | `#0B0E18` |
| surface2 | `#EFE6CF` | `#1B2440` | `#101528` |
| surface3 | `#E6DCC2` | `#232D4D` | `#171E38` |
| well | `#F0E8D3` | `#111829` | `#080B12` |
| text / ink | `#2B2822` | `#F4EDD9` | (same as dark) |
| text2 | `#403B31` | `#DCD3BC` | |
| text4 / text5 | `#6A5F50` / `#8C8170` | `#A8A08C` / `#7F7B6C` | |
| accent | `#A63F2D` vermilion | `#E5927E` coral | coral |
| onAccent | white | `#0A0A0C` | |
| danger / success / warning (light) | `#8A1C45` / `#3F6A4B` / `#855608` | `#F4739B` / `#7DBE8A` / `#E3B458` | |
| Shisha mark paper / vermilion | `#F4EDD9` / `#A63F2D` | | |

## Typography & radii (HermesTheme.kt)

- Fonts: **Geist** (UI), **Geist Mono** (code). Roles: display 32/35 semibold, title 22/26, heading 16/21, body 15/22.5, bodySmall 13.5/19.5, label 13/17 medium, caption 12/16, code 12.5/19.5, eyebrow 11/14, wordmark 56/52.
- Radii: 6 / 10 / 14 / 20 / 26 / 999 dp.
- Color scheme transition: 180 ms.

## Artwork

No separate Ukiyo-e raster asset path was required for token extraction; brand mark colours live in `ShishaMark.kt`. Launcher/themed icons exist under the Android app module — copy only with MIT attribution when integrating.

## Applied in Checkpoint 3

- Dark indigo-night Ukiyo-e default; light washi via in-app toggle (`ThemeService`).
- Local Geist / Geist Mono only (no font CDN). Token license copy: `docs/THIRD-PARTY-HERALD-LICENSE.txt`; font license: `docs/THIRD-PARTY-GEIST-LICENSE.txt`.
- Interactive acceptance of contrast/hover/focus still benefits from a human pass; automated suite does not claim WCAG screenshots.

## Checkpoint 7 — four Hokusai families

Shipped as approximate token families (not measured artworks), each with light + dark; appearance mode light/dark/system; OS reduced-motion → zero panel duration; high-contrast strengthens borders.

| Id | Display name | Character |
|---|---|---|
| `washi-ink` | Washi & Ink | Warm paper / sumi / indigo (prior Ukiyo-e mapping) |
| `great-wave` | Great Wave | Prussian / pale sky / cream |
| `red-fuji` | Red Fuji | Rust / cream / deep blue |
| `pine-mist` | Pine & Mist | Moss / slate / paper |
| `chrysanthemum` | Chrysanthemums | Golden blooms / leaf green / pale paper (Stage 43; Hokusai's Large Flowers series, "Chrysanthemums and Horsefly") |
| `peony` | Peonies | Rose peonies / deep leaves / blush paper (Stage 43; Large Flowers series, "Peonies and Butterfly") |
| `kirifuri` | Kirifuri Falls | Foam white / deep waterfall teal / moss (A Tour of the Waterfalls: Kirifuri Waterfall at Kurokami Mountain). Saturated teal paper, not Great Wave's pale sky or Pine & Mist's sage. |
| `thunderstorm` | Thunderstorm | Clear blue sky / storm indigo / lightning gold (Thirty-six Views: Thunderstorm Beneath the Summit). The sky is a real blue, and the bolt is pale gold, not Chrysanthemums' flower gold. |

Catalog + contrast ratios (≥4.5 text/bg) live in `ThemeCatalog` (Core); WPF applies via `ThemeService`. Synthetic UI acceptance captures every family × light/dark.
