# hikyaku-files

The agent asks for a file on the PC. This plugin holds that request in memory. The open Hikyaku app does the work and sends the result back. The plugin does not open a path, and it has no shell.

Install once on the gateway:

```
hermes plugins install BookahCoding/Hikyaku/hermes-plugin/hikyaku-files --enable
```

The tool is `hikyaku_files`. The app talks to the command `hikyaku-files` (`pending`, `hold`, `answer`).
