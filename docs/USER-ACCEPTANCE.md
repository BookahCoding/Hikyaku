# Real-user acceptance guide (Hikyaku Windows)

Feasible manual checks after a local `scripts/build.ps1` publish. **Never paste real passwords into chat logs or tickets.** This doc does not include endpoints or credentials.

## Prerequisites

1. The app: either the **release zip** (`Hikyaku-win-x64.zip`, self-contained, no .NET install needed) unzipped into a folder your account can write to, or a local build (`artifacts/Hikyaku/Hikyaku.exe` from `scripts/build.ps1`, framework-dependent: needs the .NET 9 Desktop Runtime).
2. For the release zip: Windows SmartScreen may warn before the first start (the exe is not Authenticode-signed).
3. A Hermes **dashboard** you already operate (browser login works). Hikyaku does not install or restart gateway plugins.
4. For the Inbox: at least one scheduled job that has run on a ticked profile (no plugin needed).

## Setting up Hikyaku for someone in Simple view (a second Windows account)

Everything below is **per Windows user**: Settings, the saved password (Windows DPAPI), the Cloudflare Access token and the notification registration are stored for the account you do this in, so repeat it in each person's own Windows account. Do the setup **in Advanced**, then switch that account to Simple at the end. A brand-new install starts in Simple; the first-run screen and Settings still show the address and profile fields until it is set up, so a beginner is never stuck, but you will want to do it for her.

1. Start Hikyaku in her account, open **Settings**, choose **Advanced** (View).
2. Enter the **gateway address**, a label, her **username** and **password**, then **Sign in and discover profiles** (or **Sign in with browser** if the gateway offers it).
3. If your gateway sits behind **Cloudflare Access**, paste the service token (Settings → Cloudflare Access) and **Test connection**.
4. **Profiles shown on this installation: tick only her profile** (never yours). Only ticked profiles appear anywhere in the app.
5. Turn **Notifications** on. Without it she will not get a Windows notification when Hermes needs her approval, and she is the person most likely to miss one.
6. Leave **Dangerous command approvals** on the setting you want for her profile (Manual or Smart is the safe choice). Simple view cannot show or change this, and it affects everyone on that profile.
7. Under **View**, choose **Simple: for everyday chatting**, then **Save**.

What she sees in Simple: **Chats**, **Inbox** and **Needs you** (the questions Hermes needs her to answer), sending and attachments, voice, Read aloud, Stop, Find in chat, renaming, pinning and deleting chats, Edit and Regenerate. Settings shows only **Account** (sign in / sign out), **Appearance** and **View**. If her session expires she can sign in again from Settings → Account. A folder shared with Hermes or an open terminal is always shown to her with a button to stop it.

Simple is **not a lock**: she can switch to Advanced in Settings → View with one click. If you need real separation, give her her own Hermes profile (as planned) and tick only that profile here.

## Startup path

1. Launch `artifacts/Hikyaku/Hikyaku.exe`.
2. If no profiles are selected → onboarding panel → **Open Settings**.
3. Settings:
   - Gateway name (local label)
   - Dashboard URL (`http`/`https` only)
   - Username + password (password stored via **Windows DPAPI** only; leave blank on later saves to keep current)
   - **Sign in and discover profiles** → tick profiles to show → **Save**
   - **Show Windows toasts for allowed profiles** — turning it on registers a notification name for your Windows user (one per-user registry entry, no admin rights); turning it off removes it. Toasts say only the kind of request or the job name and outcome plus the profile, and stay quiet while Hikyaku is in front. Windows can still hold them back (Focus assist). Clicking a toast does not open a chat yet.
4. Main shell: Chats / Inbox / Needs you; light/dark toggle; connection banner shows Connecting / Connected / Reconnecting / Session expired.

## Screens to exercise

| Screen | What to do | Pass looks like |
|---|---|---|
| Chats · New chat | New chat → type → Send / Stop | Streaming reply or honest error; Stop issues interrupt |
| Model / Reasoning / Fast | Buttons above composer | Pending pick before first send; live `config.set` after session exists; expensive model asks confirm (no silent paid switch) |
| History | Open a Recent/search row | Messages load; offline banner only when serving cache |
| Attachment | Attach → Send | Explicit picker only; cards appear; no whole-folder bridge |
| Inbox | Inbox → filters → double-click run | Followed cron outputs when plugin present; separate from Needs you |
| Scheduled jobs | Advanced view → Scheduled → New job → pick a schedule → Save; then Pause, Run now (confirm), Edit, Delete (confirm) | Needs a live gateway with the dashboard cron routes; each action goes to the job's own profile; Simple view hides the page; fixture-tested only so far |
| Capabilities | Capabilities → Skills / Toolsets / MCP servers → Turn on (confirm) / Turn off; MCP Test (confirm) | Needs a live gateway with the dashboard routes; applies to the active profile from the next chat; fixture-tested only so far |
| Insights | Insights → 7 / 30 / 90 days | Needs a live gateway with the analytics route; cost shows "billed" only if the provider reported it; fixture-tested only so far |
| Needs-you filter | Chats → Recent chats → Needs you chip | Only chats waiting on you or with an unread reply; needs a live gateway for waiting status; fixture-tested only so far |
| Chat actions | Chats → Recent chats → right-click a chat → Rename / Pin / Archive / Mark unread / Delete (confirm) | Needs a live gateway with session mutation routes; Archived chip shows archived chats; fixture-tested only so far |
| Follow while streaming | Send a long prompt, watch the reply; scroll up while it streams; use Jump to latest | Reply stays in view at the bottom; scrolling up is respected; the button returns you; needs a live reply to confirm with a real model (the real-window check uses synthetic text) |
| Failed reply | Stop the gateway or drop the network mid-reply | A card says what happened and whether the gateway has your message; the put-back button appears only when it is safe; nothing is resent on its own |
| Needs you | Approvals / questions | Answer / deny without logging secrets |
| Sign-out / restart | Clear password in Settings or remove DPAPI secret + restart | Onboarding or sign-in required again; no cross-profile bleed |

## What automated checks already cover

- `scripts/build.ps1` — Release build, tests, publish
- `scripts/smoke.ps1` — isolated `--smoke` launch marker
- `scripts/ui-acceptance.ps1` — **demo/synthetic** screenshots + toast **observed/not-observed** label (not live login)
- `scripts/check-secrets.ps1` — heuristic secret scan

## Current installer state

A per-user installer (`Hikyaku-Setup-X.Y.Z.exe`, Inno Setup, no administrator rights) and the self-contained zip; no MSI or Store package. The updater's publisher public key is embedded, so signed releases can be installed from Settings → Updates. **No old-to-new update has been installed yet**: that needs a newer signed release than the one installed (see the checklist below). No tokens in the app.

## Live acceptance checklist

**v0.5.0 was published without running this checklist, by the maintainer's choice (2026-10-28):** he uses the app daily on his gateway and reports problems, which are fixed through in-app updates. The items below remain the reference for a full acceptance.

**Honest state:** every automated test and every screenshot so far used a **simulated gateway** or the demo transport. No live Hermes gateway has been contacted by these tests, no real microphone or speaker was used, and no update has been installed from a real release. Tick each item against your real gateway; anything that fails is fixed before anything is published.

Use the installed release build so you test what ships.

1. **Sign-in**: password sign-in and discover profiles; tick one profile; Save. Then sign out and **Sign in with browser** (if your gateway offers it). Then restart the app: still signed in.
2. **Chat**: new chat, send, a streamed reply arrives; Stop mid-reply; Edit and Regenerate a message; Branch; Find in chat (Ctrl+F); open an older gateway chat and Load earlier messages.
3. **Failure**: drop the network (or stop the gateway) mid-reply: a card says what happened; nothing is resent when the connection returns.
4. **Inbox**: the results of your scheduled jobs appear newest first for the profile in the switcher; "All my profiles" shows every ticked one; clicking a result opens its chat; the × hides that job here ("Show them" brings it back); with notifications on and Hikyaku in the background, a new result shows a Windows notification within a few minutes.
5. **Needs you**: trigger an approval (a command that needs approval): it appears in Needs you and, with notifications on and Hikyaku in the background, as a Windows notification. **Allow once** and **Deny** both reach the agent. Answer a question. With the network down, an answer says it could not be sent and the request stays.
6. **Voice**: Mic on, speak, preview, transcribe, insert into the box, send. A missing speech service says so plainly.
7. **Read aloud**: on a reply, Read aloud plays; Stop reading stops; the button returns to "Read aloud" when it ends. Try it with a gateway voice that returns **MP3** as well as WAV.
8. **YOLO**: turn YOLO on in chat A (confirmation shown); open chat B: it shows YOLO off; back in A it shows YOLO on; turn it off. Then turn it on in A, **restart Hikyaku**, reopen A in Simple view, and note what it shows (known gap: YOLO state set before a restart or from another client is not read back yet).
9. **Simple / Advanced**: switch to Simple: only Chats, Inbox and Needs you; Settings shows Account, Appearance and View only. Switch back to Advanced with one click. With approvals Off for the profile, Simple shows the warning and **Ask me first** works.
10. **Second Windows account** (the Simple-view setup above): set it up for that person's own profile only, switch to Simple, sign in, send a chat, receive a Needs-you notification. Confirm your own profile is not visible there.
11. **Advanced pages** you rely on (Scheduled, Capabilities, Skills hub, Settings / Config, Rooms, Insights, Artifacts, Journey): open each; each write asks first and the change shows on the gateway.
12. **Update check**: Settings → Updates → Check for updates sees the published release (after publishing). The first real **install** test needs a newer release than the one installed (for example install v0.5.0, publish v0.5.1, then Download and verify → Install and restart, and confirm the version shows 0.5.1).

## Explicitly still pending

Everything in the live checklist above until it has been run. Live authenticated send you observe yourself; bots/rooms; live microphone + live `/api/audio` acceptance; PKCE browser sign-in (implemented + fixture-tested in Stage 15b; you still need to run one real sign-in); phone push/ntfy. Explicit mic opt-in + fixture voice path is implemented (synthetic UI); Checkpoint 14b corrected draft/scope/dispatcher/WaveIn/AudioApi lifecycle with deterministic fixtures — live voice hardware is still not accepted. Local maths typesetting is implemented (synthetic UI + WPF tests); live chat maths still needs live gateway.
