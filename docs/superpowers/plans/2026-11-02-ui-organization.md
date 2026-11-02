# App-wide UI organization implementation plan

> For agentic workers: use superpowers:subagent-driven-development for implementation and independent review.

**Goal:** Make all app screens readable, navigable and consistent, with organized Inbox and predictable settings scrolling.
**Architecture:** Keep existing WPF views and command boundaries. Fix shared styles/scroll templates, add local Inbox projections, and restructure presentation without changing gateway operations.
**Tech stack:** WPF, .NET 9, xUnit, synthetic gateway and real-window acceptance.
**Spec:** docs/superpowers/specs/2026-11-02-ui-navigation-design.md

## Global constraints

Preserve named controls, Simple/Advanced rules, confirmation behavior, profile/gateway scope, reduced motion/high contrast and existing theme IDs. Never access verify-hermes.ps1 or modify .superpowers.

## Review focus

- Wheel input over nested controls/blank areas, including direction and end-of-content behavior.
- Narrow windows, large text and long content must not hide primary actions or create horizontal overflow.
- Filter/search/read state must remain scoped to the loaded profile and gateway.
- New empty custom folders and canceled prompts must behave predictably and survive reload.
- Progressive disclosure must keep active-risk status and actions that reduce risk visible.

## Tasks and ownership

- [x] Merge PR17; validate and tag signed 0.7.6 release source.
- [x] Verify published assets/signature and copy the verified release ZIP to versioned Nextcloud delivery history.
- [x] Settings/theme worker: regression tests for wheel/mode discovery, shared accented controls and scrollbars, neutral dual-accent palettes, settings layouts and contrast checks.
- [x] Inbox worker: tests first for local filters/search/counts, empty named folder creation/persistence, scope guards; expose JobInboxSearch/Filter, SetJobInboxFilterCommand, CreateInboxFolderCommand, summary and empty states.
- [x] Workspace pages worker: preserve Inbox block; restructure Projects, Scheduled, Capabilities and remaining screens; meaningful layout/keyboard checks; report automation changes.
- [x] Main: wire Inbox UI to the new projections, add discoverable folder/search/filter controls, review every synthetic screen.
- [x] Independent whole-diff review; resolve important findings and rerun affected checks.
- [x] Full build, both suites, smoke, UI acceptance and screenshot inspection, secret scan; versioned signed release and verified delivery after successful checks.

## Execution record

2026-11-02: Root causes confirmed: nested gallery ScrollViewer swallows wheel; mode controls below gallery; quiet thumb opacity 0.35 and zero-height page tracks; Projects oversized top-docked controls starve browsing area. User selected both automatic Inbox filters and custom folders. Parallel ownership separates styles/settings, inbox logic, and MainWindow page layouts; main edits Inbox XAML only after workspace worker releases MainWindow.

Local implementation, review and validation completed. Release delivery is tracked by the GitHub Release workflow and the current local handoff.
