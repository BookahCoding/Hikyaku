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

The original WPF `Theme/Tokens.xaml` mapping used the Ukiyo-e light/dark tables below (plus Geist fonts under `Assets/Fonts`, SIL OFL 1.1). Familiar ChatGPT-style UX remains required; do not invent a third “Japanese” palette.

## Current desktop theme contract

All eleven saved families retain their ids and primary accents. Every family now uses neutral major surfaces;
artwork and Tokyo influences are expressed through two control accents instead of tinted page backgrounds.
The source tables below document historical palettes. `ThemeCatalog.cs` defines the current application palettes;
`Theme/Tokens.xaml` supplies the same neutral Washi & Ink dark defaults before saved preferences are applied.

| Surface | Light | Dark |
|---|---|---|
| Background | `#F7F7F5` | `#000000` |
| Surface | `#FAFAF8` | `#111111` |
| Sidebar | `#F0F0EE` | `#080808` |
| Raised | `#F3F3F1` | `#171717` |
| Third level | `#EDEDEB` | `#202020` |
| Well | `#F0F0EE` | `#0C0C0C` |

| Family | Secondary, Light | Secondary, Dark |
|---|---|---|
| Washi & Ink | `#245A86` | `#73B6E3` |
| Great Wave | `#9B4D25` | `#E9AA72` |
| Red Fuji | `#285A83` | `#83B8E6` |
| Pine & Mist | `#79447F` | `#CB9FDF` |
| Chrysanthemums | `#326341` | `#8EC7A4` |
| Peonies | `#246A59` | `#7BD0BB` |
| Kirifuri Falls | `#8B412C` | `#F4AA7D` |
| Thunderstorm | `#76530A` | `#83BAFF` |
| Irises | `#38613E` | `#99D2A8` |
| Shibuya Night | `#00677B` | `#22E3FF` |
| Akihabara | `#5A2DD0` | `#A080FF` |

Primary fills identify main actions and scrollbar thumbs. Secondary fills identify alternate actions, selected tabs and
checked boxes; radio indicators, selected navigation, links, dropdown chevrons and focus/selection details also use the pair.
Both filled roles use their matching `OnAccent`/`OnSecondary` labels. All ink, status and accent colours are checked at 4.5:1
against every major surface, and both filled label pairs meet 4.5:1. Great Wave's light warning was deepened to `#80600C`
to preserve contrast on neutral raised surfaces. System appearance, reduced motion and high contrast remain supported.

Settings places Light / Dark / System above the gallery, which has no nested scroll viewer. Routed wheel events scroll over
theme tiles, blank content and value controls without changing selections or values. Scrollable editors keep their own wheel
until a boundary. Shared viewers then forward boundary wheel events to an available ancestor. Intentional value-control
semantics and horizontal wheel use are preserved outside the settings-specific handler. Vertical lists constrain horizontal measurement so long descriptions wrap within their viewport. Scrollbars have 14-pixel tracks,
transparent hit-testable page regions, accent thumbs and both vertical/horizontal template parts.

## Historical source palettes

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
| `irises` | Irises | Iris violet / lavender paper / aubergine ink (Hokusai's *Irises and grasshopper*). The light ground is lavender, and both accents are violet rather than Peonies' rose or Great Wave's blue. |

Catalog + contrast ratios (≥4.5 text/bg) live in `ThemeCatalog` (Core); WPF applies via `ThemeService`. Synthetic UI acceptance captures every family × light/dark.

## Tokyo neon — two accent colours

Port source: [Herald / Hikyaku for Android, commit 5227112f](https://github.com/BookahCoding/Hikyaku-for-Android/blob/5227112f0bfccdcd5528c3f69aa474ee069e899c/shared/designsystem/src/commonMain/kotlin/dev/hermeskotlin/designsystem/Themes.kt), MIT.
Existing theme families and saved ids remain available. The two new families are city-inspired rather than woodblock-inspired.

| Theme | Light accents | Dark accents | Ground |
|---|---|---|---|
| Shibuya Night (`shibuya-night`) | `#A9136A` / `#00677B` | `#FF7CC0` / `#22E3FF` | Violet paper / near-black violet |
| Akihabara (`akihabara`) | `#06703A` / `#5A2DD0` | `#2BE37A` / `#A080FF` | Cool paper / black-blue |

Primary actions use the first colour. Selected navigation text, Inbox unread markers, code-copy links, and displayed web-link
text use the second. Link text remains non-clickable as before. All brush references update with the live theme preview.
The gallery shows both accents in both modes. Light variants deepen the neon hues for readable text on pale surfaces;
Dark variants use saturated sign colours, with no animated glow. System appearance resolves through the existing setting.

`ThemeSurfaces.Secondary` and `OnSecondary` remain optional for compatibility; external palettes without them resolve to their primary accent and label
colour. Every current catalog family supplies both roles. Tests check both neon accents, all text and status colours against all six surfaces at ≥4.5:1, and both accent label
pairs at ≥4.5:1. WPF tests cover brush application, both gallery colours, fallback to an older theme, and live link recolouring.

## Irises — ninth family

Inspiration: Hokusai, *Irises and grasshopper*, c. 1833–34, a colour woodblock print in the ukiyo-e tradition;
[Fitzwilliam Museum, P.191-1946](https://data.fitzmuseum.cam.ac.uk/id/object/182600).
These are original, interpretive UI colours, not measured artwork colours. No museum image is bundled or fetched by the app.

| Role | Light | Dark |
|---|---|---|
| Background | `#DACCEB` | `#251730` |
| Surface | `#F5EFFA` | `#2C1D38` |
| Ink | `#24152F` | `#F6EFFB` |
| Muted ink | `#51405E` | `#C4ACD4` |
| Accent | `#5D347F` | `#C0A0F0` |
| On accent | `#FFFFFF` | `#21142C` |

The gallery, saved theme id, and synthetic UI acceptance loop use the catalog automatically. No new settings control or gateway
call is needed. Tests require at least 4.5:1 for primary, secondary, muted, accent and status text on all six surfaces, and
for button labels on their accent fill. Separation tests compare the light background and both accents to every earlier family;
RGB distances are a regression guard, not a claim of perceptual uniformity or a substitute for Windows visual acceptance.
