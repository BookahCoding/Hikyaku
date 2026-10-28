# Prototype validation evidence (2026-10-25)

Commands run on this machine; results not fabricated.

## Structural (`design/validate.ps1`)

```
RESULT: ALL PASS
```

Logged in `validate-results.txt`: local assets resolve, no CDN/http(s) asset URLs, no `fetch`/`WebSocket` in `app.js`, all required views and scenario chips present, checkpoint vs push separated, reduced-motion CSS present, synthetic labelling present.

## Interaction smoke (`node design/interaction-smoke.mjs`)

```
PASS 23 / FAIL 0
RESULT: ALL PASS
```

Logged in `interaction-smoke-results.txt`. Exercised: Advanced toggle, folder grant → identity path, diff panel, Save checkpoint toast (“not pushed”), Push toast (“no network”), terminal opt-in, profile hide, Needs-you approve, connection error, reconnect.

## Screenshot

`screenshot-welcome.png` — Edge headless (`msedge --headless=new --screenshot`) against `file:///…/design/index.html` at 1280×800. Shows SYNTHETIC MOCKUP banner, Simple mode, Needs-you badge, scenario rail, Welcome copy.

## Protected paths

`verify-hermes.ps1` SHA256 after work: `6F101309A494DC67E405B8BC5D97C7DA30476A6605BDF46647926B850153896B` (unchanged).  
`.superpowers/sdd` existing file hashes unchanged; no new files written under `.superpowers` for this checkpoint.
