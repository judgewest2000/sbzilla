# Sbzilla

Sbzilla is a lightweight Azure Service Bus explorer for Windows in the spirit of [Service Bus Explorer](https://github.com/paolosalvatori/ServiceBusExplorer), built with WinUI 3. You can organise namespaces into colour-coded groups such as Production, Develop and Read-only. Every window is tinted with its group's colour, so you always know which environment you are working in.

## Install

Requires Windows 10 1809 or later. Installs per user, no admin rights needed.

### Option 1: PowerShell one-liner

```powershell
& ([scriptblock]::Create((irm "https://raw.githubusercontent.com/judgewest2000/sbzilla/main/install.ps1")))
```

This downloads the latest release for your architecture to `%LOCALAPPDATA%\Programs\Sbzilla`, then adds a Start Menu shortcut and an Apps & Features entry.

Options (append after the closing parentheses):

- `-Version v1.0.0` installs a specific release
- `-InstallDir <path>` installs somewhere else
- `-Uninstall` removes Sbzilla

### Option 2: Setup.exe

Download `Sbzilla-Setup-<arch>.exe` from the [latest release](https://github.com/judgewest2000/sbzilla/releases/latest) and run it.

### Uninstall

Use Windows Settings > Apps, or run the one-liner above with `-Uninstall`.

## Features

- **Namespace groups**: a tree of groups, each with a name and colour. Under each group are namespaces, and each namespace expands into **Queues** and **Topics** folders. Topics expand into their subscriptions. Every queue and subscription shows its active and dead-letter counts.
- **Explorer search**: the box above the tree (Ctrl+Shift+F) filters groups, namespaces and loaded queues, topics and subscriptions. Press Enter to open the first match, or Esc to clear.
- **Favourites**: right-click a queue, topic or subscription and choose **Add to favourites** to pin it in a **Favourites** folder at the top of its namespace. Favourites are saved per namespace and work in read-only groups too.
- **Colour-coded windows**: entity tabs, the title bar and the status bar are tinted with the group's colour.
- **Read-only groups**: you can browse and peek, but sending, resubmitting, deleting, purging and editing are all blocked, both in the UI and in Sbzilla's Service Bus layer. This is enforced by Sbzilla only; for a guarantee in Azure, use credentials with fewer rights, such as a Listen-only policy.
- **Authentication**:
  - Shared access connection string, either namespace- or entity-scoped. Entity-scoped strings show just that entity.
  - Interactive Microsoft Entra ID sign-in, using the Windows account broker.
  - `DefaultAzureCredential` (Azure CLI, Visual Studio, managed identity and so on).
  - Service principal.
  - Optional AMQP over WebSockets (port 443), for networks that block port 5671.
- **Peek messages**:
  - Peek from the active, dead-letter or transfer dead-letter queue. Messages are not locked or removed.
  - Peek a page at a time, with **Next** to continue.
  - Session-enabled entities can be peeked across all sessions or one session.
  - **Filter** the peeked messages by any text in the body or properties.
  - The detail pane shows the body and all system and application properties. JSON and XML bodies are pretty-printed and syntax-highlighted; binary bodies are shown as a hex dump. Ctrl+F in the body opens a find bar.
- **Send messages** (Ctrl+N):
  - Body (type it or load it from a file), content type, message id, subject, correlation id, session id, partition key, To and Reply-to.
  - Time to live, a scheduled enqueue time (e.g. `+10m` or a date), and application properties written as `name = value` lines.
  - Send several copies at once. `{n}` is replaced with the copy number and `{guid}` with a new GUID.
- **Edit & resend**: opens the Send dialog pre-filled from the selected message.
- **Resubmit** dead-lettered messages to their queue or topic, optionally removing the originals. Messages resubmitted from a subscription go to its topic, so every subscription on that topic receives them, and Sbzilla warns you about this.
- **Delete** selected messages and **purge** a queue, a subscription or their dead-letter queues. Purging and deleting entities require you to type the name to confirm.
- **Manage entities**:
  - Create, edit and delete queues, topics and subscriptions. The properties form covers lock duration, TTL, dead-lettering, sessions, duplicate detection, forwarding, status and more. Durations are written like `30s`, `1h 30m`, `14d` or `never`.
  - Manage subscription **rules**: SQL, correlation and true/false filters, with an optional SQL action.
- **Copy and save**: copy the selected messages as JSON (Ctrl+C), or save them to a file.

Connections are stored in `%LOCALAPPDATA%\Sbzilla\connections.json`. Connection strings and secrets are encrypted with Windows DPAPI.

Open tabs are remembered between runs in `%LOCALAPPDATA%\Sbzilla\session.json`.

### Shortcuts

| Key | Action |
| --- | --- |
| F5 | Refresh messages and counts (or the selected namespace in the explorer) |
| Ctrl+N | Send a message to the current entity |
| Ctrl+Shift+F | Search namespaces and entities |
| Ctrl+F | Filter the peeked messages |
| Ctrl+W | Close tab |
| Ctrl+C | Copy selected messages as JSON |
| Delete | Delete selected messages |

## Updates

On startup, Sbzilla checks GitHub for a newer release and asks whether to update. You can also use **Check for updates** in the About dialog. **Release notes** in the About dialog shows what changed in each version (from [CHANGELOG.md](CHANGELOG.md)).

## Build from source

Requires the .NET 11 SDK.

```powershell
# Runs tests, then publishes a self-contained build to .\publish\win-x64
.\publish.ps1 -Zip
```

## Publish a release

Requires [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`) and the GitHub CLI. First add the version's notes to the top of [CHANGELOG.md](CHANGELOG.md).

```powershell
.\installer\build-installer.ps1 -Platform x64 -Version 1.0.1
gh release create v1.0.1 publish\Sbzilla-win-x64.zip publish\Sbzilla-Setup-x64.exe --title v1.0.1
```

`-Version` is stamped into the app, so the release tag must match it for the update check to work. The zip must be named `Sbzilla-win-<arch>.zip` for `install.ps1` to find it.

## License

[MIT](LICENSE). Copyright (c) 2026 James West.
