# UI and reliability review — 2026-11-02

Reviewed the current Windows client after syncing main with GitHub at `197725c`.
The focus is everyday chat UI, state isolation, and recovery from delayed or canceled operations.

## Findings addressed

- **Draft isolation:** the shared composer retained text when selecting another conversation. Keep in-memory drafts with their conversation and reject stale disk restores.
- **Delayed sends:** awaiting local draft persistence before claiming a turn allowed another send or a conversation switch to race with visible message insertion. Claim the send before asynchronous work and retain origin ownership.
- **Delayed steering:** accepted or rejected steering responses could add messages to, or replace the composer of, the newly selected conversation. Apply responses to the originating conversation.
- **History cancellation:** cancellation skipped loading-state cleanup, permanently disabling the earlier-message button. Restore the state so the user can retry.
- **History bounds:** fixed-size requests could exceed the remaining loaded-row budget. Request only the remaining rows.

## Scope and validation

Conversation drafts are isolated in memory while the app is open. Disk recovery retains the existing one-draft-per-profile format; this change does not introduce per-chat persistence across restarts. Navigation preserves the saved recovery text, and a delayed restore stays with its untouched originating chat.

These are corrections to existing behavior; no gateway endpoints or authentication behavior change. Regression tests reproduce delayed operations with synthetic fixtures. The full build, both test suites, published-app smoke check, synthetic UI acceptance, and secret scan are required before delivery.

Synthetic gateway tests do not validate a live gateway connection. Release packaging, publication, and installation are outside this change.

## Verified results

- Regression tests were run before implementation and failed on the original behavior, including the two disk-recovery edge cases caught by independent review.
- `scripts/build.ps1`: Release build and publish succeeded; 0 warnings, 0 errors; core 992/992 and WPF 393/393 tests passed, none skipped.
- `scripts/smoke.ps1`: startup passed against the freshly published executable with isolated data.
- `scripts/ui-acceptance.ps1`: real-window synthetic acceptance passed; 100 controls, 22 theme variants, 0 horizontal overflow descendants. Composer, Simple view, and earlier-history screenshots were visually inspected.
- `scripts/check-secrets.ps1`: clean (heuristic check); `git diff --check`: clean.
- Independent code review approved the final draft, delayed-response, and history changes with no remaining critical or important findings.
