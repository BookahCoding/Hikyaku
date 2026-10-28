## What and why

<!-- One change per pull request. Link the issue if there is one. -->

## Source for any gateway call

<!-- The Hermes docs page, Hermes Desktop behaviour, or BookahHermes file it follows. "None" if no gateway call changed. -->

## Checklist

- [ ] `./scripts/build.ps1` passes (warnings are errors; both test projects)
- [ ] New behaviour has tests; each new safety rule was mutation-checked (broken on purpose, a test failed, restored)
- [ ] New surfaces are classified in `ExperienceFeatures.cs`; gateway writes are Advanced-only and checked inside the command
- [ ] Writes that cannot be undone or widen what the agent can do are confirmed in words; nothing is resent automatically
- [ ] `./scripts/check-secrets.ps1` is clean: no secrets, personal paths, real hosts or personal data in code, tests or screenshots
- [ ] `CHANGELOG.md` (Unreleased) and affected docs updated
