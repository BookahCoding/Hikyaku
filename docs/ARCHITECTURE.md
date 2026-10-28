# Architecture

## Decision: lean new remote client (WPF / .NET 9), not an extension of Hermes Desktop
Toolchain: .NET 9 Windows Desktop SDK. Own allowlist and Hikyaku identity as first-class design.

| Option | For | Against |
|---|---|---|
| Extend Hermes Desktop | Reuse UI/protocol | Foreign stack, licence/release coupling |
| Lean new client (chosen) | Native DPAPI, small surface, own UX | Re-implements chat UI; tracks protocol itself |

## Protocol pin (source-backed)
Sibling **BookahHermes** (Herald) revision **`5f00986b2a582e480592fd680131d501d57038ef`** (MIT).  
Dashboard “remote gateway” (not OpenAI `/v1/chat/completions`):

1. `POST /auth/password-login` → session cookies  
2. `POST /api/auth/ws-ticket` → single-use ticket  
3. WebSocket `/api/ws` with `Sec-WebSocket-Protocol: hermes-gateway-v1, hermes-gateway-ticket.<ticket>` (ticket never in URL)  
4. `gateway.ready` → `client.capabilities {server_requests:true}`  
5. `session.create` / `prompt.submit`; stream `message.delta` … `message.complete`; cancel via `session.interrupt`  
6. Profiles: `GET /api/profiles`

Primary references: `shared/core/.../AuthApi.kt`, `GatewayConnection.kt`, `JsonRpcClient.kt`, `RpcTransport.kt`, `ChatSession.kt`, `Profiles.kt`, `docs/hermes-protocol-research.md`.

## Layout
- `src/Hikyaku.Core`: `DashboardAuth`, `JsonRpcClient`, `HermesDashboardTransport`, `FakeRpcTransport`, `NotificationGate`/`NotificationSink`, profile selection, coalescer, reconnect policy. Legacy `HttpGatewayTransport` retained for old SSE unit tests only.
- `src/Hikyaku.App`: WPF shell, DPAPI secrets, settings (sign-in + discover profiles).
- `tests/Hikyaku.Tests`: xUnit including Hermes wire fixtures + loopback auth.
- `docs/ANDROID-PARITY.md`, `docs/THEME-REFERENCE.md`.

## Profile allowlist
Identity `(GatewayId, ProfileId)`. Fail closed until load; empty → onboarding; new profiles opt-in only. All surfaces must call `NotificationGate.Allow`. Preferences are local display filtering, not server authorization.

## Secrets
Password in DPAPI under `%APPDATA%\Hikyaku\secrets` (or `HIKYAKU_DATA_DIR`). No secrets in URLs, logs, or git. Username may live in `settings.json`.

## Streaming
`TokenCoalescer` (50 ms). Reconnect retries only before first token (no duplicate partial output). Cancellation → `session.interrupt`.
