# Scoped folder bridge: design

Date: 2026-10-28. Release: not chosen (after 0.7.0). Status: agreed in conversation; read this before any plan or code.

## Goal

While Hikyaku is open and connected, the agent can list, read, and write files in folders the maintainer chose on this PC. The files stay on this PC. The gateway asks the open app, and the app does the work. A closed app or a dropped connection fails the call and changes nothing.

## Decisions (maintainer, 2026-10-28)

- The tool asks the open app. Copying a folder up to the gateway was the other way, and was not chosen.
- SSH that is configured in Hermes keeps working. This tool has no shell and does not use that SSH.
- A folder the maintainer allows stays with that one profile. Its chats, a chat continued from the phone, and its scheduled jobs can use it while the app is open. Another profile asks for itself.
- When the agent needs a folder this profile does not have, Hikyaku asks on this PC and the maintainer chooses the folder. The choice can be the user folder or a whole drive. The agent receives the folder that was chosen, even if it asked for a different one. An earlier folder stays allowed.
- After a folder is chosen, reads and writes inside it go ahead, including new files. A delete asks on this PC every time. An unanswered delete leaves the file as it is.
- A file whose name looks like a key or a password stays blocked. The block is the file's name (`.env`, a `.pem` or other key extension, `id_rsa`, a name containing "credentials" or "secret", saved browser logins). A normal document that only contains a password in its text stays readable.
- In Advanced, Settings has a switch for this profile, off unless turned on. On lets the agent read and write those named files in folders this profile already has. Off blocks them again. Simple view has no switch.
- Without that switch, the agent can ask for one of those files. Hikyaku names the file on this PC. Allow sticks for this profile until it is taken back, and then this profile can read and write that file. Refuse leaves it blocked. The other key files stay blocked. A delete of an allowed key file still asks.
- The allow is remembered on this PC. Taking a folder or a file back removes it.

## What the person sees

- Folder ask: a folder picker on this PC, in Simple and in Advanced, because the agent is waiting. The path the agent asked for is shown. Cancel adds nothing.
- Key-file ask: Allow or Refuse for the named file, on this PC, in both views. Allow remembers that file for this profile.
- Delete ask: Yes or No for that file, on this PC, in both views. It does not stick.
- The switch that lifts the name block for every key file in this profile's folders is Advanced only (`ExperienceFeature`, Settings). Simple has no switch, so the block stays unless a file was allowed one by one.
- Folders and allowed key files for this profile are listed where shared folders are listed today (Projects, Advanced). That is where one is taken back. Simple still shows that a folder is shared, as it does for a shared folder today.
- A phone chat does not show the picker. The ask is on the PC. The phone chat can use a folder or a file already allowed while the app is open.
- Without the plugin, the agent has no tool from this feature. Advanced can show the install line, the way the Inbox shows its plugin hint. Simple shows no technical hint.

## Hermes plugin `hikyaku-files`

- Lives in this repository, installed once on the gateway, same idea as `hikyaku-cron`:
  `hermes plugins install BookahCoding/Hikyaku/hermes-plugin/hikyaku-files --enable`.
- Registers a tool the agent can call. The tool does no disk work on the gateway. It asks the open Hikyaku over the dashboard connection that is already up, the same request and answer channel as an approval (`server.request`, answered by the app). The method name is fixed in the plan, after checking hermes-agent for how a plugin tool waits on that channel. If a plugin cannot wait on the connected app, the plan stops and asks before any other channel is added.
- The tool is usable from a chat, a phone platform, and a scheduled job. The work happens only when Hikyaku is connected for that gateway. No connected app means an error, and nothing is read or written.
- Operations: list a directory, read a file, write a file, delete a file. No shell, no move, no directory delete.
- The plugin answers only for the profile of the calling session. It does not accept a path of its own. The app decides what is inside a chosen folder.

## Hikyaku side

- The existing per-profile folder grant becomes a list of chosen folders for that gateway and profile, remembered locally. Choosing a folder allows read and write inside it. Delete still asks. A junction or link that steps outside the chosen folder is refused. A network path is refused.
- Choosing the user folder or a whole drive is allowed. That is a change from today's grant, which refuses both.
- Key-file names use the existing name check (`SensitivePathPolicy`). The Advanced switch and the per-file allows are stored with the profile's grants. Default is blocked.
- A read or write of a blocked name fails closed until that file is allowed or the switch is on. The file bytes are not sent either way until then.
- Reads and writes are text, up to 512 KB. A longer file is cut and the answer says so. A file that is not text is refused with that reason. Parent folders needed for a new file may be created inside the chosen folder.
- Delete of a directory is refused. Delete of a file runs only after Yes on this PC.
- An ask that is still open when the app closes or the connection drops is cancelled. Nothing is written.

## Error handling

- App closed, connection dropped, or no plugin: the tool fails, and the PC files stay as they are.
- Cancel on a folder ask, Refuse on a key file, or No on a delete: no change. The rest of the call fails closed.
- A path outside every chosen folder starts a folder ask. If the chosen folder still does not contain that path, the call fails and the new folder stays allowed for later.
- A missing folder (moved or deleted on disk) fails closed until a folder is chosen again.

## Testing

- Core: a chosen folder allows list, read, and write; a second folder stays; another profile does not see them; user folder and a drive letter can be chosen; a network path is refused; a link that escapes the folder is refused; a key-named file is blocked, then allowed for that file only, then blocked again when taken back; the Advanced switch allows every key-named file for that profile and blocks them again when off; delete without Yes does not delete; directory delete is refused; a file over 512 KB reports the cut; a non-text file is refused.
- WPF: the folder picker, the key-file ask, and the delete ask show in Simple and Advanced; the switch shows only in Advanced; taking a folder back removes it; a shared folder still shows in Simple.
- Plugin: the tool does not read the gateway disk; with no connected app the answer is an error; a call for one profile cannot ask for another profile's folders.

## Out of scope

Shell; changing or wrapping SSH; copying files onto the gateway; scanning the contents of ordinary files for secrets; deleting a folder; Office or binary files; an upstream Hermes change, unless the plan finds the plugin cannot wait on the open app.
