# App-wide navigation and organization design

User brief: make Inbox organized with BOTH automatic filters and named custom folders; fix settings wheel behavior and Light/Dark discoverability; improve dull and dense Capabilities/Scheduled; make Projects understandable; check every other screen for the same issues. Two accent colors must be used across actual controls on neutral major surfaces.

## Result

- Inbox has search, All/Unread/Failed/Script filters with counts, clear New folder and Move to folder actions, persistent empty custom folders, and distinct empty states. Existing job folders and profile scope remain.
- Settings mode controls come before the theme gallery. Wheel events over the gallery and blank page areas scroll the page. Scrollbars have visible accent-colored thumbs and usable tracks.
- A shared control system applies the primary accent to main actions and the secondary to alternate actions, selected tabs, focus/selection and checked indicators. Major surfaces stay neutral; contrast and Windows accessibility preferences remain supported.
- Scheduled jobs use readable cards with labelled schedule/next/last information. Capabilities uses cards, strong section titles, legible descriptions and grouped actions.
- Projects opens with a plain explanation and a clear folder action; file browsing and previews remain usable, with checkpoint/publish/worktree tools grouped progressively. Sharing and terminal safety status and risk-reduction actions remain visible.
- Bots, Needs you, Rooms, Artifacts, Journey, Insights, voice and terminal controls receive the same readability, wrapping, selection and scrolling audit. Empty states explain the next action. No arbitrary server features are invented.

## Constraints

Native WPF/.NET 9; retain existing command contracts and named automation controls. Preserve Simple/Advanced classification and profile/gateway ownership. No new dependencies, endpoints, automatic sends, grants or confirmations removed. Do not read or modify verify-hermes.ps1 or .superpowers. Screenshots are synthetic; no live gateway data is used.

## Delivery

First merge and publish the already reviewed reliability fixes as signed 0.7.6 for Settings Updates. UI work is isolated on feat/ui-organization-and-navigation and is independently reviewed, regression-tested, built, rendered and checked before its own versioned release.
