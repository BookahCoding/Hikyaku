# Scoped folder bridge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** While Hikyaku is open, the agent can list, read, and write inside folders this profile allowed on this PC, and can delete a file or a folder only after a yes on this PC.

**Architecture:** `FolderBridgeStore` remembers the profile's folders, key-file allows, and the Advanced switch. `FolderBridgeGate` decides and performs the file work on this PC. The Hermes plugin `hikyaku-files` only queues the call in memory; `FolderBridgeLoop` polls it with `command.dispatch`, asks on this PC, and posts the result back. Projects git keeps the existing one-folder grant.

**Tech Stack:** C# / .NET 9, WPF, xUnit; Python 3.11+ unittest for the plugin.

**Spec:** `docs/superpowers/specs/2026-10-28-folder-bridge-design.md`

## Global Constraints

- Channel: in-memory queue in the plugin process. Verbs on command `hikyaku-files`, via `command.dispatch`: `pending`, `hold <id>`, `answer <id> <json>`. Tool name `hikyaku_files`. No Hermes source change. If the tool and the command do not share a process, stop and ask.
- Command platforms: `pending`, `hold`, and `answer` accept only `desktop` and `tui`. The tool accepts any platform.
- Ops: `list`, `read`, `write`, `delete_file`, `delete_folder`. No shell, no move.
- Errors: `not_open`, `denied`, `outside`, `blocked`, `missing`, `cancelled`, `invalid`. JSON `{"ok": false, "error": "<code>"}`.
- Lease: 10 seconds from create or the last `hold`. Poll every 1 second while connected. One `pending` claim at a time. No hold in time → `not_open`, and the app does not touch the disk.
- A key-named file uses `SensitivePathPolicy.IsSensitiveLeaf`. That block applies to read and write only. Delete still asks, including for a blocked name.
- No kind or size cap once access is allowed. Read returns `{"ok": true, "encoding": "utf-8", "text": "..."}` when the bytes are UTF-8, otherwise `{"ok": true, "encoding": "base64", "data": "..."}`. Write takes `text` (UTF-8) or `data` (base64), one of them.
- `LocalFolderGrantStore` and `PathBoundary.TryResolve` stay as they are (git still refuses a drive, the user folder, and a network path). The bridge has its own list in `folder-bridge.json`.
- Asks (folder picker, network allow, key-file allow, delete) show in Simple and Advanced. The switch is Advanced-only: new `ExperienceFeature.SettingsFolderKeyFiles`, not added to `SimpleFeatures`.
- Simple `RiskNotice` names a bridge folder when this profile has one. Stop sharing clears this profile's bridge folders and key-file allows. Projects removes one at a time.
- Commits: explicit paths. No `Claude-Session` line. No `Co-Authored-By` trailer. Gates after Task 5. `verify-hermes.ps1` and `.superpowers` stay untouched.
- SSH configured in Hermes is not read, wrapped, or changed.

## Review Focus

1. Yes on a folder delete removes the folder and the files inside it; No, cancel, or a dropped lease leaves them — test in Task 2 and Task 4.
2. A link inside the folder that points outside is refused, and the outside file is not read or written — test in Task 2.
3. `notes.txt` whose text contains "password" is readable; `id_rsa` in the same folder is not, until that file is allowed or the switch is on — test in Task 2.
4. The answer posted for one request is not returned as the result of another — test in Task 3.
5. The lease expires while a dialog is open: the tool returns `not_open` and the app does not write — test in Task 4.

---

### Task 1: FolderBridgeStore

**Files:** Create `src/Hikyaku.Core/FolderBridgeStore.cs`; Test `tests/Hikyaku.Tests/Checkpoint50FolderBridgeTests.cs`.

**Interfaces — Produces:** `public sealed record BridgeFolder(string CanonicalRoot, bool Network, DateTimeOffset GrantedAtUtc)`; `public sealed record BridgeKeyFile(string CanonicalPath, DateTimeOffset GrantedAtUtc)`; `public sealed record BridgeProfile(IReadOnlyList<BridgeFolder> Folders, IReadOnlyList<BridgeKeyFile> KeyFiles, bool KeyFilesOpen)`; `public sealed class FolderBridgeStore(IPreferenceStore store) { Task<BridgeProfile> GetAsync(ProfileKey key, CancellationToken ct = default); Task AddFolderAsync(ProfileKey key, string canonicalRoot, bool network, CancellationToken ct = default); Task RemoveFolderAsync(ProfileKey key, string canonicalRoot, CancellationToken ct = default); Task AllowKeyFileAsync(ProfileKey key, string canonicalPath, CancellationToken ct = default); Task RevokeKeyFileAsync(ProfileKey key, string canonicalPath, CancellationToken ct = default); Task SetKeyFilesOpenAsync(ProfileKey key, bool open, CancellationToken ct = default); }`. JSON file later bound as `folder-bridge.json`. A second add of the same root replaces nothing and does not duplicate. `KeyFilesOpen` defaults false.

- [ ] **Step 1: Write the failing test.** Two profiles. Add a folder to A, reload, A still has it and B does not. Add a second folder; both remain. `RemoveFolderAsync` drops one. Allow a key file, reload, revoke it. `SetKeyFilesOpenAsync(true)` survives reload; `false` clears the flag and leaves the per-file allow. A drive-root string and a `\\server\share` string are stored when passed to `AddFolderAsync` (the store does not refuse them). Corrupt JSON loads as an empty profile.
- [ ] **Step 2: Run** `dotnet test tests/Hikyaku.Tests --filter Checkpoint50FolderBridge` **and confirm the new tests fail.**
- [ ] **Step 3: Implement the store.** Do not call `IsWholeVolumeOrHome`.
- [ ] **Step 4: Run the same filter and confirm the new tests pass.**
- [ ] **Step 5: Commit** the two files above.

### Task 2: FolderBridgeGate

**Files:** Create `src/Hikyaku.Core/FolderBridgeGate.cs`; Test as Task 1.

**Interfaces — Consumes:** `BridgeProfile`, `SensitivePathPolicy.IsSensitiveLeaf`. **Produces:** `public enum BridgeAsk { None, ChooseFolder, AllowNetwork, AllowKeyFile, ConfirmDelete }`; `public sealed record BridgeDecision(BridgeAsk Ask, string Path, string Detail)`; `public sealed record BridgeResult(bool Ok, string Error, string Encoding, string Text, byte[]? Bytes, IReadOnlyList<(string Name, bool Dir)>? Entries)`; `public static class FolderBridgeGate { public static BridgeDecision Decide(BridgeProfile access, string op, string path); public static BridgeResult Apply(BridgeProfile access, string op, string path, byte[]? write, bool deleteYes); }`.

`Decide` before any disk change. `Apply` performs it and is called only when `Ask` is `None`, or after the app has stored the new allow and `Decide` is `None`.

- [ ] **Step 1: Write the failing test** against a temp directory:
  - A file of any bytes, including a 2 MB payload and a `.png` header, reads back equal and writes back equal. `write` creates a missing parent inside the root.
  - `notes.txt` containing the word password: `Decide` is `None` for `read`.
  - `id_rsa` in the folder: `Decide` for `read` and `write` is `AllowKeyFile` until `AllowKeyFile` is on the profile or `KeyFilesOpen` is true; then `None`.
  - `delete_file` and `delete_folder`: `Decide` is `ConfirmDelete`. Detail for a folder contains "everything inside". `Apply(..., deleteYes: false)` leaves the tree. `Apply(..., deleteYes: true)` on a folder removes the folder and a nested file. `id_rsa` delete is `ConfirmDelete`, not `AllowKeyFile`.
  - A junction or symlink inside the root whose target is outside: `Apply` for `read` and `write` returns `outside` and the outside file is unchanged. Skip the assertion when the process cannot create a link, and say so in the test output.
  - A path not under any root: local path → `ChooseFolder`; a UNC path → `AllowNetwork`.
  - A root that does not exist: `Apply` returns `missing`.
  - `path:stream` (alternate data stream) returns `invalid` and creates no file.
- [ ] **Step 2–4: fail, implement, pass.** Resolver for a local root follows the final target and requires it stay under the root. A network root is prefix-checked after `GetFullPath`; do not call `PathBoundary.TryResolve` (it refuses UNC).
- [ ] **Step 5: Commit** `FolderBridgeGate.cs` and the test file.

### Task 3: Plugin `hikyaku-files`

**Files:** Create `hermes-plugin/hikyaku-files/plugin.yaml`, `__init__.py`, `bridge.py`, `tests/test_bridge.py`, `README.md`.

**Interfaces — Produces:** tool `hikyaku_files` (`op`, `path`, optional `text`, optional `data`); command `hikyaku-files`. Module-level queue. `handle_tool(params, profile=None)` blocks until `answer` or the lease dies. `handle_command(raw_args, platform=None)` serves `pending`, `hold`, `answer`.

- [ ] **Step 1: Write the failing unittest.**
  - `pending` with no request → `{"ok": true, "pending": false}`.
  - Start a tool call on a thread; `pending` returns its `id`, `profile`, `op`, `path`. A second call stays queued. `answer` for the first id unblocks only that call with that JSON. The second `pending` then returns the other id.
  - `hold` unknown id → `invalid`. After the lease (test with a 0.2s lease injected, not the 10s default) and no `hold`, the tool returns `not_open`.
  - `platform` `telegram` on `pending` → `{"ok": false, "error": "invalid"}` and does not claim. `desktop` and `tui` claim. Tool with `platform` unused: a call still queues when the command is `desktop`.
  - The tool and the command never call `open`, `Path.read_bytes`, or `os.remove`. Patch those and assert they were not called.
  - `op` `shell` → `invalid`, nothing queued.
- [ ] **Step 2: Run** `python -m unittest discover -s hermes-plugin/hikyaku-files/tests` **and confirm failure.**
- [ ] **Step 3: Implement.** `plugin.yaml` mirrors `hikyaku-cron` (name `hikyaku-files`, `requires_hermes: ">=0.21.5"`, no disk, no hooks). README gives the install line `hermes plugins install BookahCoding/Hikyaku/hermes-plugin/hikyaku-files --enable`. Profile on the tool comes from the `profile` keyword, else `"default"`. `answer` splits as verb, id, and the remainder of the string (the JSON).
- [ ] **Step 4: Re-run unittest and confirm pass.**
- [ ] **Step 5: Commit** the plugin directory.

### Task 4: FolderBridgeLoop

**Files:** Create `src/Hikyaku.Core/FolderBridgeLoop.cs`; Test as Task 1.

**Interfaces — Consumes:** Tasks 1–2; `IJsonRpcPeer.RequestAsync("command.dispatch", {name, arg})`; `InboxApi.NotACommand` (4018). **Produces:** `public interface IFolderBridgePrompts { Task<string?> ChooseFolderAsync(string askedPath, CancellationToken ct); Task<bool> AllowNetworkAsync(string path, CancellationToken ct); Task<bool> AllowKeyFileAsync(string path, CancellationToken ct); Task<bool> ConfirmDeleteAsync(string path, bool folder, CancellationToken ct); }`; `public sealed class FolderBridgeLoop(Func<IJsonRpcPeer?> rpc, FolderBridgeStore store, Func<ProfileKey?> profile, IFolderBridgePrompts prompts) { Task TickAsync(CancellationToken ct); }`.

`TickAsync` does at most one claim. `arg` is `pending`, `hold <id>`, or `answer <id> <json>`. The JSON is the tool result. Before `Apply`, call `hold`. If `hold` is not `ok`, do not `Apply` and do not `answer`.

- [ ] **Step 1: Write the failing test** with a fake peer and fake prompts.
  - No client: `TickAsync` does not dispatch.
  - `pending: false`: no prompt.
  - Path outside, `ChooseFolderAsync` returns a temp dir that contains the path: the folder is stored, `Apply` runs, `answer` is `ok`. A second folder already stored remains.
  - `ChooseFolderAsync` returns null: `answer` is `cancelled`, store unchanged, temp file unchanged.
  - `ChooseFolderAsync` returns a dir that still does not contain the path: that dir is stored, `answer` is `outside`, the target file is unchanged.
  - UNC path, `AllowNetworkAsync` false: store unchanged, `answer` `cancelled`. True: root stored, then the read runs.
  - `id_rsa`: `AllowKeyFileAsync` true stores the file and the read runs; false answers `cancelled` and does not read.
  - `delete_folder`, prompt false: tree remains, `answer` `cancelled`. Prompt true: tree is gone, `answer` `ok`.
  - `hold` returns `not_open`: prompt may have returned true, and the file is still absent (no write).
  - Dispatch code 4018: `TickAsync` sets `PluginMissing` true and does not throw.
- [ ] **Step 2–4: fail, implement, pass.** One profile per `TickAsync`, from `profile()`. Ignore a pending `profile` that does not match; `answer` `invalid` for that id so the tool unblocks.
- [ ] **Step 5: Commit** `FolderBridgeLoop.cs` and the test file.

### Task 5: The asks, the switch, and Projects

**Files:** Modify `src/Hikyaku.Core/ExperienceFeatures.cs`, `src/Hikyaku.App/ExperienceUi.cs`, `src/Hikyaku.App/ProjectsUi.cs`, `src/Hikyaku.App/MainWindow.xaml`, `src/Hikyaku.App/MainWindow.xaml.cs`; Create `src/Hikyaku.App/FolderBridgePrompts.cs`; Test `tests/Hikyaku.App.Tests/Checkpoint50FolderBridgeWpfTests.cs`.

**Interfaces — Consumes:** Tasks 1–4. **Produces:** WPF `IFolderBridgePrompts`. `ExperienceFeature.SettingsFolderKeyFiles`. Settings switch bound to `SetKeyFilesOpenAsync` for the current profile. Projects lists `BridgeFolder` and `BridgeKeyFile` with a remove button each. `RiskNotice` includes "A folder on this computer is shared with Hermes" when the bridge list is non-empty (keep the existing sentence when the git grant is set). Stop sharing calls `RemoveFolderAsync` for each bridge folder and `RevokeKeyFileAsync` for each key file on the current profile, and still revokes the git grant as it does today. Advanced hint text when `PluginMissing`: the install line from Task 3. Simple hides the hint and the switch.

- [ ] **Step 1: Write the failing WPF test.** In Simple and in Advanced, a loop tick with a `ChooseFolder` decision reaches the picker callback; same for network, key file, and both delete kinds. In Simple the switch is collapsed; in Advanced it is visible and turning it on makes `GetAsync(...).KeyFilesOpen` true. Remove on Projects drops that one folder and leaves the other. `RiskNotice` is non-empty in Simple when a bridge folder exists.
- [ ] **Step 2: Run** `dotnet test tests/Hikyaku.App.Tests --filter Checkpoint50FolderBridge` **and confirm failure.**
- [ ] **Step 3: Implement.** Picker is `OpenFolderDialog` (folder). Network, key file, and delete are Yes/No. Delete of a folder uses the Detail string from `Decide` (it already says everything inside goes). The switch is a Settings row: add `FolderKeyFiles` to `SettingsSectionPlan` and `SettingsSections.For`, true only in Advanced. Start the loop when the gateway client is connected; stop it when the client is disposed. Do not add `SettingsFolderKeyFiles` to `SimpleFeatures`. `answer` splits as verb, id, and the remainder of the string (the JSON), so the payload may contain spaces.
- [ ] **Step 4: Re-run the WPF filter and confirm pass.**
- [ ] **Step 5: Gates, then commit** the files in this task. Gates from the repo root: `./scripts/build.ps1`; `./scripts/smoke.ps1`; `./scripts/ui-acceptance.ps1 -OutDir artifacts/claude-acceptance`; `./scripts/check-secrets.ps1`; `python -m unittest discover -s hermes-plugin/hikyaku-files/tests`; `python -m unittest discover -s hermes-plugin/hikyaku-cron/tests`; the protected SHA256 of `verify-hermes.ps1` equals `6F101309A494DC67E405B8BC5D97C7DA30476A6605BDF46647926B850153896B`; the `.superpowers` manifest matches `artifacts/claude-baseline/superpowers-manifest-before.txt`. Do not commit `artifacts/`.

## Spec coverage

Asks and remembers: Tasks 1, 4, 5. Any kind and size, folder delete, junction, key names, switch: Task 2. In-memory channel, platforms, no gateway disk, lease: Task 3. Closed app and dropped lease write nothing: Tasks 3 and 4. Profile isolation: Tasks 1, 3, 4. Simple versus Advanced: Task 5. Git grant and SSH: untouched, stated above.
