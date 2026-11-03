# Screenshot gallery

A look at Hikyaku **v0.7.7**, captured from the Windows WPF app on 3 November 2026.
Every screen uses synthetic demo content. These are app captures, not mockups, and do not show private chats,
credentials, or a live gateway. Click any image to open it at full size.

[Simple and Advanced](#simple-and-advanced) · [App screens](#app-screens) · [All themes](#all-themes) · [Back to README](../README.md)

## Simple and Advanced

Switch in **Settings → View**. A new installation starts in Simple. Both views offer the same themes;
Advanced adds gateway management and technical controls. The screenshots below show the same example conversation.

| Simple · Washi & Ink Light | Advanced · Washi & Ink Light |
|---|---|
| [![Simple mode: compact navigation and an everyday chat](images/simple-view.png)](images/simple-view.png) | [![Advanced mode: the same chat with gateway pages and model controls](images/advanced-view.png)](images/advanced-view.png) |

Simple is a view, not an access-control boundary. See [Simple and Advanced in the README](../README.md#simple-and-advanced) for details.

## App screens

Inbox is available in both views; the capture here uses Advanced navigation. The gateway administration and
project screens below are Advanced features. Appearance settings are available in both views.

| Inbox | Scheduled jobs · Advanced |
|---|---|
| Search, counted filters, and an opened scheduled result. | Job schedules, paused runs, and delivery status. |
| [![Inbox with All, Unread, Failed and Script filters and a sample morning summary](images/inbox.png)](images/inbox.png) | [![Scheduled jobs with morning summary, backup check and weekly digest cards](images/scheduled-jobs.png)](images/scheduled-jobs.png) |

| Capabilities · Advanced | Skills hub · Advanced |
|---|---|
| Skills, toolsets, MCP servers, settings, and config. | Browse skills and see which are installed. |
| [![Capabilities with enabled web search and disabled notes skills](images/capabilities-advanced.png)](images/capabilities-advanced.png) | [![Skills hub with sample release, PDF and meeting-note skills](images/skills-hub.png)](images/skills-hub.png) |

| Projects · Advanced | Appearance · both views |
|---|---|
| Start with a folder on this PC, then browse files and save checkpoints. | Light, Dark, System, and paired theme previews. |
| [![Projects page before choosing a local folder](images/projects.png)](images/projects.png) | [![Appearance settings with light and dark controls above the theme gallery](images/appearance.png)](images/appearance.png) |

| Rooms · Advanced | Artifacts · Advanced |
|---|---|
| Hosted rooms, participants, and room conversations. | Browse and preview files from agent replies. |
| [![Rooms page with synthetic participants and messages](images/rooms.png)](images/rooms.png) | [![Artifacts page listing synthetic files and preview actions](images/artifacts.png)](images/artifacts.png) |

### Journey · Advanced

A timeline of the skills and memories the profile has picked up.

[![Journey timeline with synthetic skills and a memory](images/journey.png)](images/journey.png)

## All themes

Eleven themes, each with light and dark variants. **System** follows the Windows appearance setting.
The same example chat and Advanced layout are used throughout so you can compare the accents and surfaces.
Choose a theme in **Settings → Appearance**; it applies to Simple too.

### Washi & Ink

| Light | Dark |
|---|---|
| [![Washi & Ink Light](images/themes/washi-ink-light.png)](images/themes/washi-ink-light.png) | [![Washi & Ink Dark](images/themes/washi-ink-dark.png)](images/themes/washi-ink-dark.png) |

### Great Wave

| Light | Dark |
|---|---|
| [![Great Wave Light](images/themes/great-wave-light.png)](images/themes/great-wave-light.png) | [![Great Wave Dark](images/themes/great-wave-dark.png)](images/themes/great-wave-dark.png) |

### Red Fuji

| Light | Dark |
|---|---|
| [![Red Fuji Light](images/themes/red-fuji-light.png)](images/themes/red-fuji-light.png) | [![Red Fuji Dark](images/themes/red-fuji-dark.png)](images/themes/red-fuji-dark.png) |

### Pine & Mist

| Light | Dark |
|---|---|
| [![Pine & Mist Light](images/themes/pine-mist-light.png)](images/themes/pine-mist-light.png) | [![Pine & Mist Dark](images/themes/pine-mist-dark.png)](images/themes/pine-mist-dark.png) |

### Chrysanthemums

| Light | Dark |
|---|---|
| [![Chrysanthemums Light](images/themes/chrysanthemum-light.png)](images/themes/chrysanthemum-light.png) | [![Chrysanthemums Dark](images/themes/chrysanthemum-dark.png)](images/themes/chrysanthemum-dark.png) |

### Peonies

| Light | Dark |
|---|---|
| [![Peonies Light](images/themes/peony-light.png)](images/themes/peony-light.png) | [![Peonies Dark](images/themes/peony-dark.png)](images/themes/peony-dark.png) |

### Kirifuri Falls

| Light | Dark |
|---|---|
| [![Kirifuri Falls Light](images/themes/kirifuri-light.png)](images/themes/kirifuri-light.png) | [![Kirifuri Falls Dark](images/themes/kirifuri-dark.png)](images/themes/kirifuri-dark.png) |

### Thunderstorm

| Light | Dark |
|---|---|
| [![Thunderstorm Light](images/themes/thunderstorm-light.png)](images/themes/thunderstorm-light.png) | [![Thunderstorm Dark](images/themes/thunderstorm-dark.png)](images/themes/thunderstorm-dark.png) |

### Irises

| Light | Dark |
|---|---|
| [![Irises Light](images/themes/irises-light.png)](images/themes/irises-light.png) | [![Irises Dark](images/themes/irises-dark.png)](images/themes/irises-dark.png) |

### Shibuya Night

| Light | Dark |
|---|---|
| [![Shibuya Night Light](images/themes/shibuya-night-light.png)](images/themes/shibuya-night-light.png) | [![Shibuya Night Dark](images/themes/shibuya-night-dark.png)](images/themes/shibuya-night-dark.png) |

### Akihabara

| Light | Dark |
|---|---|
| [![Akihabara Light](images/themes/akihabara-light.png)](images/themes/akihabara-light.png) | [![Akihabara Dark](images/themes/akihabara-dark.png)](images/themes/akihabara-dark.png) |

## Capture notes

These images were rendered by the app at desktop size using its demo transport and synthetic fixtures.
Chat and theme captures use an example Kyoto conversation; other pages use the built-in UI acceptance fixtures.
The local capture data and temporary harness stay under ignored `artifacts/`.

To inspect the built-in screen captures on Windows, first publish the current source, then run:

```powershell
dotnet publish src/Hikyaku.App -c Release -r win-x64 --self-contained false -o artifacts/Hikyaku -nologo
./scripts/ui-acceptance.ps1 -OutDir artifacts/screenshots
```

The script saves PNGs and `REPORT.md` under `artifacts/screenshots/ui-acceptance`. Review images before publishing:
layout-test chats are intentionally repetitive, theme captures may include test drawers, and temporary project paths
can contain a Windows account name. Use a fresh isolated demo chat for a public theme comparison. Never publish
personal settings, live gateway data, or the entire capture output folder.