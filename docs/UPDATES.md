# GitHub Releases updater (Stage 15c)

**Status:** implemented and fixture-tested. **The publisher public key is embedded** (`UpdateTrust.PublisherPublicKeySpkiBase64`, generated 2026-10-27; the private key is on the owner's PC outside the repo and must be backed up by the owner). A package signed with that key verifies against the compiled-in key, and the same package fails for another version (checked with the release tool). Release packages are **self-contained** (about 68 MB zipped, no .NET runtime to install). The app version is **0.5.0** (`Directory.Build.props`; the first public release, numbered 0.5 while it is tested on a real gateway). A real old-to-new install of a signed GitHub release has **not** been performed yet: that needs a newer signed release than the installed one (v0.5.0 from the installer, then v0.5.1). The zip also carries the licence and third-party notices in a `licenses` folder.

## What the user sees (Settings → Updates)
1. **Check for updates.** Anonymous `GET /repos/BookahCoding/Hikyaku/releases/latest`. Also run **once at start-up** (5 s after the window opens, 20 s timeout, silent on any failure; Settings → Updates → "Check for a new version when Hikyaku starts", on by default since 0.5.0): if a newer release exists, an "Update to x.y.z" button appears in the top line (both views since 0.6.1) and opens this section with a fresh check. The start-up check never downloads anything.
2. Current version, latest version and plain-text release notes (control characters stripped, 4000 characters max).
3. **Download and verify** — enabled only when a publisher key is embedded. Explicit click; cancellable.
4. **Install and restart** — a second, separate confirmation (default answer No). No downgrades are ever offered.
5. **Open release page** — opens the GitHub release page for that tag in the browser (checked against this repo and tag).

## Trust model (why a hash is not enough)
A SHA-256 published next to a download proves nothing: whoever can replace the zip can replace the hash. The updater
therefore trusts only a **project signing key**: ECDSA P-256 + SHA-256, detached signature `Hikyaku-win-x64.zip.sig`
(base64, 64-byte IEEE-P1363). The public key is compiled into the app; the private key is generated and kept by the
owner offline and never enters this repository.

The signed message is `hikyaku-release-v1 \n platform=win-x64 \n version=X.Y.Z \n sha256=<zip hash>`. Because the version and
platform are signed, a validly signed *older* package cannot be presented as a newer release (rollback / mix-up).

**Fail closed:** no embedded key (or an unusable one) → nothing is downloaded and nothing is run. Bad, malformed or
mismatched signature → the download is deleted and never extracted or executed.

## Download and extraction safety
- HTTPS only; hosts limited to `api.github.com`, `github.com`, `objects.githubusercontent.com`,
  `release-assets.githubusercontent.com`; redirects followed manually, each hop re-checked, at most 5; no credentials sent.
- Asset URLs must be exactly `https://github.com/<repo>/releases/download/<tag>/<name>`; the JSON's claim alone is not trusted.
- Only strict `vMAJOR.MINOR.PATCH` stable releases (no draft/pre-release) with both the zip and the signature asset.
- Size bounds: 150 MB package, 4 KB signature, 512 KB API answer; partial files removed on every failure.
- Extraction: rejects `..`, rooted/UNC/drive/stream (`:`) names, reserved device names, trailing dot/space,
  duplicates, missing root `Hikyaku.exe`, too many entries, and bounds the *bytes actually written* (not declared sizes).
  It extracts to a temporary folder and renames, so a failure leaves nothing.

## Install (transactional, no standing backups)
The app starts the **already-verified new build** from the staging folder as a small helper (`--apply-update`), then exits.
The helper waits for the old process to end (60 s, then refuses), moves each file it replaces into a transient
`.update-old` folder, copies the new files, and on any failure restores everything; on success the transient folder is
deleted. This is **not** an app backup: nothing is retained, in line with the no-automatic-backups rule. A crash
mid-apply is recovered at the next apply. Files in the install folder that the new package does not contain are left alone.
`%APPDATA%\Hikyaku` (settings, DPAPI secrets, caches) is never touched. If the install folder is not writable (e.g.
Program Files without elevation) the app says so and points at the release page instead of attempting it.
Prepared packages live in `%LOCALAPPDATA%\Hikyaku\updates` and are deleted at the next normal start; a prepared update is never installed automatically.

## Owner procedure (you hold the key)
1. Once: `dotnet run --project tools/Hikyaku.ReleaseTool -- keygen --out <path OUTSIDE the repo>.pem` (refuses to overwrite or to write inside a checkout).
   Back the key up yourself. Paste the printed **public** key into `UpdateTrust.PublisherPublicKeySpkiBase64`, rebuild, and ship that build once by hand.
2. Per release: bump `<Version>` in `Directory.Build.props`, commit, and push a tag `vX.Y.Z`. The Release workflow builds `Hikyaku-win-x64.zip`, `Hikyaku-win-x64.zip.sig`, and `Hikyaku-Setup-X.Y.Z.exe`, verifies the signature against the embedded public key (it must print `Ok`), and publishes a normal GitHub Release. Setup is in [RELEASING.md](RELEASING.md). The same `package.ps1 -Version vX.Y.Z -KeyPath <key>.pem -Installer` command still works on a PC that has the key, if you need to build by hand.
3. The release must be a normal release: drafts and pre-releases are ignored by the updater. Assets must be named exactly `Hikyaku-win-x64.zip` and `Hikyaku-win-x64.zip.sig` (the setup exe is for first installs; the updater ignores it). Release notes are shown as plain text in the app (4000 characters max): use plain sentences and `-` lists, no Markdown. Put them in `docs/release-notes/vX.Y.Z.txt` so the workflow attaches them.
4. Losing the key means shipping a new build that embeds a new public key manually. There is no remote revocation.

## Not done / honest limits
- **Private repositories:** a private repo answers 401/403/404 to the anonymous check and is reported as such. The DPAPI-sealed token path (never embedded, never required) is a later stage.
- **No real release has been installed.** Covered by synthetic keys/zips/handlers in tests and by `scripts/update-rehearsal.ps1`, which runs only the real exe's helper mode on temp folders and writes the staging marker itself (it is *not* proof that a signed release installs).
- Authenticode / SmartScreen reputation is separate and optional (e.g. a free OSS signing programme could be added later); it would not replace this key.
- A same-user local attacker who can write the staging folder after verification is out of scope (they could already write the install folder).
- Settings → Updates is not covered by a WPF automation test; the state machine (`UpdateFlow`) and every Core step are.
- The first install is the per-user Inno Setup installer (`installer/Hikyaku.iss`, into `%LOCALAPPDATA%\Programs\Hikyaku`, no administrator rights) or the zip by hand. No MSI/MSIX or all-users install: a copy under Program Files could not be updated in place.
