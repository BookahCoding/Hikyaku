# Changelog

All notable changes to Hikyaku are listed here. Versions follow [semantic versioning](https://semver.org/); release tags
are `vMAJOR.MINOR.PATCH`.

## Unreleased

- Unsent drafts are no longer lost when two saves overlap (typing quickly, or switching chats while a save is still writing).

## 0.6.1 — updates in Simple view

- Settings → Updates and the "Update to x.y.z" button are offered in Simple view too (the maintainer's choice), so
  someone using Simple view can keep Hikyaku up to date.

## 0.6.0 — voice chat

Plain-text release notes: [docs/release-notes/v0.6.0.txt](docs/release-notes/v0.6.0.txt).

- Voice chat, like Hermes Desktop's: the headset button in the chat box starts a hands-free conversation with the open chat's
  agent, in both views. It listens until you pause (about 1.25 s, or the profile's `voice.silence_duration`), sends what
  you said as your message, speaks the reply while it is still being written (the gateway's speak-stream, or the whole
  reply at once on gateways without it), then listens again. Talk over it to cut in. Say "stop" (or "goodbye", "never
  mind", or the profile's `voice.stop_phrases`), press Stop or Esc, or switch chats to end it; three silent listens end
  it too. When Hermes needs an approval or an answer it stops and asks you to check the screen: nothing is approved by
  voice. Code and attached files are never read out. A microphone Windows keeps from desktop apps is reported with where
  to switch it on.
- Design and plan: docs/superpowers/specs/2026-10-28-voice-chat-design.md, docs/superpowers/plans/2026-10-28-voice-chat.md.

## 0.5.2 — Settings redesigned, two fixes

Plain-text release notes: [docs/release-notes/v0.5.2.txt](docs/release-notes/v0.5.2.txt).

- Fixed: after saving Settings (for example switching Simple to Advanced) the open chat was no longer selected, so Send,
  + and the microphone were greyed out and New chat did not help. Refreshing the chat list no longer drops the selection.
- Fixed: after a restart or an update the browser sign-in could be lost and had to be done again. The app's connections
  renewed the shared sign-in at the same moment with the same one-use refresh token; renewal is now one at a time, and a
  refused renewal no longer forgets a sign-in that another renewal had just saved.

- Settings laid out like Claude Desktop's: sections on the left (General, Appearance, Account, and in Advanced Profiles,
  Safety, Connection, Updates), one page each, in plain words; Save and Cancel always in reach. Simple view lists only the
  sections it shows.
- Themes are picked from a gallery that shows each theme's light and dark colours side by side.
- The update button opens Settings straight on Updates.

## 0.5.1 — first fixes from daily use

Plain-text release notes: [docs/release-notes/v0.5.1.txt](docs/release-notes/v0.5.1.txt).

- Read aloud made no sound on a real PC: playback now goes through Windows' AudioGraph from memory (no temporary audio
  file), plays MP3, WAV, OGG/Opus, FLAC and AAC, and says why when a clip cannot be played.
- Paste a picture (screenshot or copied image) or copied files into the chat box with Ctrl+V, or drop files on the chat:
  they become attachments, as with the + button. Copied text still pastes as text.
- Settings: the dangerous-command approval mode (Manual / Smart / Off) loads as Settings opens, for the profile in use;
  before, it stayed greyed out until another profile was picked and back.

## 0.5.0 — first public test release

First public release of the Windows client for a Hermes Agent gateway, numbered 0.5 because it is still being tested
on a real gateway (fixes ship as in-app updates). Plain-text release notes:
[docs/release-notes/v0.5.0.txt](docs/release-notes/v0.5.0.txt).

### Install and updates
- `Hikyaku-Setup-0.5.0.exe`: a per-user installer (no administrator rights) into `%LOCALAPPDATA%\Programs\Hikyaku`, with a
  Start menu entry, an optional desktop icon and an uninstaller that leaves settings and sign-in alone.
- At start-up Hikyaku asks GitHub once whether a newer version exists (Settings → Updates can turn it off) and shows an
  "Update to x.y.z" button; nothing is downloaded until you click.

### Features
- Chats: streaming replies, TeX maths, attachments, voice dictation, read aloud, edit / regenerate, branch, find in chat,
  steer / queue / stop-and-send while a reply runs, `/btw` and `/bg` side tasks, tool steps in replies, history paging,
  failed-reply cards (nothing is resent automatically), reading position that follows a streaming reply.
- Inbox (results of the scheduled jobs of the ticked profiles, from the dashboard; no plugin needed) and Needs you
  (approvals, questions, secret prompts), Windows notifications.
- Simple and Advanced views; Advanced adds scheduled jobs, capabilities (skills, toolsets, MCP servers), the skills hub,
  typed settings and `config.yaml` editors, MCP add / edit, insights, hosted rooms, artifacts, Journey, Bots, and local
  projects (folder sharing, Git checkpoints and push, local terminal).
- Sign-in with password or browser (PKCE), Cloudflare Access service tokens, per-installation profile allow-list.
- Signed in-app updates (ECDSA P-256, version-bound) from GitHub Releases; self-contained release package.
- Six Hokusai-inspired themes (including Chrysanthemums and Peonies from his Large Flowers series), light / dark, text size, keyboard shortcuts and command palette.

### Changed after the first live test (the maintainer, on a real gateway)
- The connection survives a visit to Settings and starts with the app (pages such as Capabilities were empty).
- A profile switcher in the sidebar: New chat, the chat list and every page use the chosen profile (it starts on the
  gateway's "default" profile; before, the alphabetically first profile id was used).
- Claude-Desktop-style chat box: + and a microphone icon, a model menu (Advanced) and one send / stop button; model,
  reasoning, fast, YOLO, new, stop and branch are slash commands with suggestions while typing "/".
- Messages: your messages in a bubble on the right, replies as plain text across the column, actions as icons on hover.
- Lists open on a single click; the chat is renamed when the gateway names it (no more "Unknown gateway event").
- Short plain status line in both views; the app icon and sidebar mark are the Android app's; icons in the sidebar;
  drop-downs open at once and sharp (no fuzzy slide); buttons fade on hover and pages fade in briefly.

### Inbox rebuilt (second live test)
- herald-push lets only a device registered for its push notifications follow jobs, which a PC cannot be yet, so the
  Inbox now reads the dashboard's own scheduled-job results: it follows the profile switcher (or "All my profiles"),
  never shows a profile that is not ticked, opens a result as its chat, can hide a job on this PC, and notifies about new
  results while notifications are on.

### Inbox folders (third live test)
- The Inbox opens on folders like the Android app: Failed, groups you make ("Move to group", kept on this PC), then
  each job; "All results" shows one list. A script-only job's run is an output document, not a chat: it is marked
  "Script output" and expands in place (opening it as a chat answered 404). Agent runs still open as their chat.

### What the agent does, shown in the reply (Claude-style)
- Advanced: the model's reasoning in a collapsed block ("Thinking…", then "Thought for 12s"; reopened chats keep it),
  tool steps that appear as they start and fill in their result, time and a tick or cross, the agent's plan as a
  checklist, notices such as "Context was compressed", and a live line saying what runs now. Steps and plan come
  before the answer they led to.
- Simple: only a plain activity line ("Thinking…", "Working on it…", "Waiting for the model…"), no tool names.
- Both views: a warning when the gateway flags a tool's output as high risk (for example text trying to give the
  agent orders). When a turn ends, fails or is stopped, nothing is left showing as running.

### Fixed in the release review
- YOLO state is kept per chat; previously turning it on in one chat showed it on in every chat, and turning it off
  elsewhere hid a chat that still had it on.
- A dropped connection or full disk during an update check or download no longer closes the app.
- A Needs-you answer that cannot be sent now says so, keeps the request listed and puts a typed answer back.
- Settings: sign out on a gateway timeout, Test Cloudflare Access and saving the profile choice can no longer close the app.

### Known limits
- Used on one real gateway by the maintainer; the full live checklist (docs/USER-ACCEPTANCE.md) was skipped by choice
  for this test release, and problems are fixed through updates.
- Not yet: toast click-through, pop-out windows, gateway-side message queue, live voice conversation, reading YOLO state
  that was set from another client.
