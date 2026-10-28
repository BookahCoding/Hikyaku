# Scoped folder bridge: design

Date: 2026-10-28. Release: not chosen (after 0.7.0). Status: agreed in conversation; read this before any plan or code.

## Goal

While Hikyaku is open and connected, the agent can list, read, and write files in folders the maintainer chose on this PC. The files stay on this PC. The gateway asks the open app, and the app does the work. A closed app or a dropped connection fails the call and changes nothing.

## Decisions (maintainer, 2026-10-28)

- The tool asks the open app. Copying a folder up to the gateway was the other way, and was not chosen.
- SSH that is configured in Hermes keeps working. This tool has no shell and does not use that SSH.
- A folder the maintainer allows stays with that one profile. Its chats, a chat continued from the phone, and its scheduled jobs can use it while the app is open. Another profile asks for itself.
- When the agent needs a folder this profile does not have, Hikyaku asks on this PC and the maintainer chooses the folder. The choice can be the user folder or a whole drive. The agent receives the folder that was chosen, even if it asked for a different one. An earlier folder stays allowed.
- A network path is not refused. Hikyaku asks on this PC, and the maintainer allows or denies that path. Allow remembers it for this profile, the same as a chosen folder. Deny adds nothing.
- After a folder or network path is allowed, reads and writes inside it go ahead, including new files, with no limit on kind or size. A delete asks on this PC every time, for a file or for a folder. The folder ask says that everything inside it goes with it. An unanswered delete leaves the file or folder as it is.
- A file whose name looks like a key or a password stays blocked. The block is the file's name (`.env`, a `.pem` or other key extension, `id_rsa`, a name containing "credentials" or "secret", saved browser logins). A normal document that only contains a password in its text stays readable.
- In Advanced, Settings has a switch for this profile, off unless turned on. On lets the agent read and write those named files in folders this profile already has. Off blocks them again. Simple view has no switch.
- Without that switch, the agent can ask for one of those files. Hikyaku names the file on this PC. Allow sticks for this profile until it is taken back, and then this profile can read and write that file. Refuse leaves it blocked. The other key files stay blocked. A delete of an allowed key file still asks.
- The allow is remembered on this PC. Taking a folder or a file back removes it.

## What the person sees

- Folder ask: a folder picker on this PC, in Simple and in Advanced, because the agent is waiting. The path the agent asked for is shown. Cancel adds nothing.
- Network ask: Allow or Deny for the path the agent named, on this PC, in both views. Allow remembers it. Deny adds nothing.
- Key-file ask: Allow or Refuse for the named file, on this PC, in both views. Allow remembers that file for this profile.
- Delete ask: Yes or No, on this PC, in both views, for a file or for a folder. A folder delete names the folder and says that everything inside it goes. It does not stick.
- The switch that lifts the name block for every key file in this profile's folders is Advanced only (`ExperienceFeature`, Settings). Simple has no switch, so the block stays unless a file was allowed one by one.
- Folders and allowed key files for this profile are listed where shared folders are listed today (Projects, Advanced). That is where one is taken back. Simple still shows that a folder is shared, as it does for a shared folder today.
- A phone chat does not show the picker. The ask is on the PC. The phone chat can use a folder or a file already allowed while the app is open.
- Without the plugin, the agent has no tool from this feature. Advanced can show the install line, the way the Inbox shows its plugin hint. Simple shows no technical hint.

## Hermes plugin `hikyaku-files`

- Lives in this repository, installed once on the gateway, same idea as `hikyaku-cron`:
  `hermes plugins install BookahCoding/Hikyaku/hermes-plugin/hikyaku-files --enable`.
- Registers a tool the agent can call. The tool does no disk work on the gateway. It asks the open Hikyaku over the dashboard connection that is already up, the same request and answer channel as an approval (`server.request`, answered by the app). The method name is fixed in the plan, after checking hermes-agent for how a plugin tool waits on that channel. If a plugin cannot wait on the connected app, the plan stops and asks before any other channel is added.
- The tool is usable from a chat, a phone platform, and a scheduled job. The work happens only when Hikyaku is connected for that gateway. No connected app means an error, and nothing is read or written.
- Operations: list a directory, read a file, write a file, delete a file, delete a folder. No shell and no move.
- The plugin answers only for the profile of the calling session. It does not accept a path of its own. The app decides what is inside a chosen folder.

## Hikyaku side

- The existing per-profile folder grant becomes a list of allowed folders for that gateway and profile, remembered locally. A local folder is one the maintainer chose. A network path is one they allowed. Either allows read and write inside it, of any kind and any size. Delete still asks. A junction or link that steps outside the allowed folder is refused.
- Choosing the user folder or a whole drive is allowed. Allowing a network path is allowed. Both are a change from today's grant, which refuses all three.
- Key-file names use the existing name check (`SensitivePathPolicy`). The Advanced switch and the per-file allows are stored with the profile's grants. Default is blocked. That name block is the only holdback on read and write inside an allowed folder.
- A read or write of a blocked name fails closed until that file is allowed or the switch is on. The file bytes are not sent either way until then. Once allowed, that file has no kind or size limit either.
- Parent folders needed for a new file may be created inside the allowed folder.
- Delete of a file or a folder runs only after Yes on this PC. A folder delete removes the folder and everything inside it.
- An ask that is still open when the app closes or the connection drops is cancelled. Nothing is written.

## Error handling

- App closed, connection dropped, or no plugin: the tool fails, and the PC files stay as they are.
- Cancel on a folder ask, Deny on a network path, Refuse on a key file, or No on a delete: no change. The rest of the call fails closed.
- A path outside every chosen folder starts a folder ask. If the chosen folder still does not contain that path, the call fails and the new folder stays allowed for later.
- A missing folder (moved or deleted on disk) fails closed until a folder is chosen again.

## Testing

- Core: an allowed folder allows list, read, and write of any kind and any size; a second folder stays; another profile does not see them; user folder, a drive letter, and a network path can be allowed; a denied network path adds nothing; a link that escapes the folder is refused; a key-named file is blocked, then allowed for that file only, then blocked again when taken back; the Advanced switch allows every key-named file for that profile and blocks them again when off; delete of a file or a folder without Yes does not delete; a Yes on a folder removes it and everything inside it.
- WPF: the folder picker, the network allow or deny, the key-file ask, and the delete ask (file or folder) show in Simple and Advanced; the switch shows only in Advanced; taking a folder back removes it; a shared folder still shows in Simple.
- Plugin: the tool does not read the gateway disk; with no connected app the answer is an error; a call for one profile cannot ask for another profile's folders.

## Out of scope

Shell; changing or wrapping SSH; copying files onto the gateway; scanning the contents of ordinary files for secrets; an upstream Hermes change, unless the plan finds the plugin cannot wait on the open app.
