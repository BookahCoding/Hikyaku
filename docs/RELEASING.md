# Releasing Hikyaku

A push of a tag `vMAJOR.MINOR.PATCH` (for example `v0.7.3`) runs `.github/workflows/release.yml` on `windows-latest`. The workflow checks out that tag, builds the self-contained zip and the per-user installer, signs the zip with the repository secret, checks the signature against the public key in `UpdateTrust.PublisherPublicKeySpkiBase64`, and publishes a normal GitHub Release (not a draft, not a pre-release, marked latest). The in-app updater reads that release.

The private key is not stored in the repository. The workflow writes it to a temp file, never prints it, and deletes the file even when a later step fails.

## One-time setup

1. Open the repository on GitHub.
2. Go to **Settings → Secrets and variables → Actions**.
3. Choose **New repository secret**.
4. Name it exactly `HIKYAKU_RELEASE_KEY_PEM`.
5. Paste the full PEM of the publisher private key (the same key whose public half is already compiled into the app). Include the begin and end lines. Do not wrap it in quotes.

The workflow stops with a clear error if that secret is missing or is not a PEM private key, and it does not publish a release. If the secret is a different key, signature verification fails and nothing is published.

## Cut a release

1. Bump `<Version>` in `Directory.Build.props` to the same number.
2. Move the `CHANGELOG.md` Unreleased notes under that version.
3. Add plain-text notes at `docs/release-notes/vX.Y.Z.txt` (the updater shows them; keep them short, with `-` lists).
4. Commit and push `main`.
5. Tag that commit and push the tag: `git tag vX.Y.Z` then `git push origin vX.Y.Z`.

The tag push starts the Release workflow. When it finishes, `https://github.com/BookahCoding/Hikyaku/releases/latest` has `Hikyaku-Setup-X.Y.Z.exe`, `Hikyaku-win-x64.zip`, and `Hikyaku-win-x64.zip.sig`.

## Publish a tag that already exists

`v0.7.2` was pushed before this workflow existed, so the tag push did not build it. After this workflow is on `main` and the secret is saved:

1. Open **Actions → Release → Run workflow**.
2. Use the `main` branch.
3. Set the tag input to `v0.7.2`.

Or, from a machine logged into GitHub: `gh workflow run release.yml --ref main -f tag=v0.7.2`.

The run checks out the tag you typed, not the latest commit. `v0.7.2` already has `docs/release-notes/v0.7.2.txt`.
