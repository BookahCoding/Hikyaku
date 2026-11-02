# Hikyaku project instructions

## Project and references
- Native Windows WPF client for an existing Hermes dashboard gateway; .NET 9.
- `src/Hikyaku.Core`: protocol and domain logic. `src/Hikyaku.App`: WPF UI and view models.
- Tests: `tests/Hikyaku.Tests` (core), `tests/Hikyaku.App.Tests` (WPF and simulated gateway).
- Read `CONTRIBUTING.md`, `docs/ARCHITECTURE.md`, and relevant parity documents before changing behavior. Prefer Hermes Desktop UX; use the pinned Android reference for wire contracts. Never invent endpoints.

## Conventions and safety
- Classify new UI in `ExperienceFeatures.cs`; gateway administration is Advanced-only and commands enforce this independently of visibility.
- Confirm irreversible or capability-widening gateway writes; target the profile the row was loaded for. Never automatically resend after failure or disconnect.
- Profile identity is `(GatewayId, ProfileId)`; allowlists fail closed and newly discovered profiles require opt-in.
- Use synthetic fixtures and `*.example` hosts. Never commit credentials or personal gateway data.
- Do not read, execute, modify, stage, or delete `verify-hermes.ps1`; preserve `.superpowers` reference assets.
- Add regression tests before behavior fixes. Update `CHANGELOG.md` under Unreleased.

## Validation
- `./scripts/build.ps1`: build with warnings as errors, run both test projects, publish to `artifacts/Hikyaku`.
- Targeted checks: `dotnet test tests/Hikyaku.Tests` and `dotnet test tests/Hikyaku.App.Tests` with `--filter` as appropriate.
- `./scripts/smoke.ps1` and `./scripts/ui-acceptance.ps1` test the published executable: only run them after a successful fresh publish.
- `./scripts/check-secrets.ps1` must report clean before committing. Stage explicit paths.
- Builds and fixtures do not replace live gateway acceptance. No release or visibility changes without explicit user instruction.
