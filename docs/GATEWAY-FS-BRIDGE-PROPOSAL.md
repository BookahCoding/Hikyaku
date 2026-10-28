# Gateway filesystem bridge — proposal only (NOT deployed)

**Status:** documented proposal for a future decision. **NOT deployed** in Checkpoint 9.  
No new Hermes/gateway tool, plugin, or RPC method is registered by Hikyaku in this release.

## Why this exists

Checkpoint 9 delivers a **local UI** on the PC running Hikyaku: explicit folder picker, revocable grants, browse/preview, secret-aware share review into the existing attachment queue, and read-only Git Changes. That is **not** agent-autonomous filesystem access on the gateway host.

If a later product goal is “agent tools may read/write the user’s granted Windows folder,” that requires an **explicit new gateway tool/plugin** with its own security review. Until that ships, agents must not be told they have a local FS tool.

## Exact proposal (for review — do not implement here)

| Item | Proposal |
|---|---|
| Name | `hikyaku.local_fs` (provisional) |
| Host | Only the Windows machine running Hikyaku, never implied on a remote gateway OS account |
| Authz | Requires an active `LocalFolderGrant` for the selected profile; fail closed on revoke/path change |
| Ops | `list`, `read_text`, `write`/`delete` only with the same per-path overwrite/delete confirmations as the UI |
| Non-goals | Shell/PTY, whole-drive grant, silent upload, bypass of secret share review |
| Deployment gate | Separate checkpoint + human security sign-off; plugin install is out of band |

## What Checkpoint 9 ships instead

- Local Projects nav + Files / Preview / Changes (read-only Git)
- Attachment/share flow with content review (selecting a folder never uploads)
- Honest copy: local processing ≠ local-only model; outbound share is deliberate

Stop: do not register tools or change live gateway profiles/plugins based on this document alone.
