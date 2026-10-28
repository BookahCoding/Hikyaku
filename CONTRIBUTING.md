# Contributing to Hikyaku

Thank you for helping. Bug reports, testing against real gateways, documentation fixes and code are all welcome.

## Before you start

- For anything larger than a small fix, open an issue first so we can agree on the approach.
- Security problems: follow [SECURITY.md](SECURITY.md), not a public issue.
- Be kind: [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) applies everywhere in the project.

## Building and testing

You need Windows and the .NET 9 SDK.

```powershell
./scripts/build.ps1           # build with warnings as errors, run both test projects, publish
./scripts/smoke.ps1           # launch smoke test of the published app (isolated data folder)
./scripts/ui-acceptance.ps1   # drives the real window in demo mode; read its REPORT.md and screenshots
./scripts/check-secrets.ps1   # must print "clean" before you commit
```

`ui-acceptance.ps1` and `smoke.ps1` run the **published** exe, so if the build failed they test an old one: read the build
output first.

## Rules the code follows

- **Never invent gateway endpoints.** Every call follows a documented Hermes route or the pinned Android client
  (BookahHermes, pinned commit in [docs/ANDROID-PARITY.md](docs/ANDROID-PARITY.md)); say which in the pull request. For a feature that exists in
  Hermes Desktop too, follow Desktop's behaviour.
- **Simple and Advanced.** Every new page, tab, setting, chip, palette entry or shortcut is classified in
  `src/Hikyaku.Core/ExperienceFeatures.cs` and is Advanced-only unless it is part of everyday chatting. A new write to the
  gateway is Advanced-only, and the command checks this itself, not only the button's visibility. The deliberate
  exceptions available in Simple are everyday chat actions (sending, edit / regenerate, the chat menu's rename / pin /
  mark read / delete), answering Needs-you requests, and actions that reduce risk (turning YOLO off, "Ask me first",
  stop sharing a folder, close the terminal). A risk that is already in effect must stay visible in Simple.
- **Gateway writes are confirmed in words when they cannot be undone or widen what the agent can do.** With no
  confirmation, or a "no", nothing is sent. A write only ever goes to the profile its row was loaded for.
- **Nothing is resent automatically** after a failure or a disconnect.
- **Tests first.** New behaviour gets a test against real-shaped payloads, and each safety rule gets a mutation check: break
  the rule, confirm a test fails, restore it.
- **No secrets or personal data** in code, tests, fixtures, screenshots or commit messages. Use `*.example` hosts and
  synthetic text. Never put a secret-looking assignment in a test (`token = "..."`).

## Pull requests

- Keep each pull request to one change, with tests, and update the docs it affects (`CHANGELOG.md` under *Unreleased*).
- CI must be green: it builds, runs both test projects and the secret scan.
- By contributing you agree your contribution is released under the project's [MIT licence](LICENSE).
