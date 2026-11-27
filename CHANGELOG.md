# Release notes

## 1.1.4 (2026-11-27)

- **Latest** peeks the most recent messages, newest first. **Next** then continues with older messages. Works for dead-letter queues and session-enabled entities too.

## 1.1.3 (2026-11-27)

- **About** uses a wider, side-by-side layout so the artwork, information, keyboard shortcuts and update buttons fit without scrolling in normal-sized windows. Release notes also use the wider dialog.

## 1.1.2 (2026-11-26)

- **Search** now lists matching queues, topics and subscriptions even when the namespace's own name also matches (e.g. "baltic" in aimcbalticrs). Namespaces whose name matches are loaded automatically, so this works straight after starting.
- Search waits until you pause typing (400 ms) before filtering; Enter and Down apply it immediately.

## 1.1.1 (2026-11-26)

- Double-clicking a message shows the whole message as JSON (body and properties) in a syntax-highlighted viewer. Previously only the first line appeared.

## 1.1.0 (2026-11-26)

- **Favourites**: right-click a queue, topic or subscription and choose **Add to favourites** to pin it in a **Favourites** folder at the top of its namespace.
- **Read-only groups** are now also enforced in Sbzilla's core: sending, resubmitting, deleting, purging and editing are refused for those namespaces even if a button were left enabled.
- **Refresh one item**: right-click a queue, topic or subscription and choose **Refresh** to update just its counts (a topic also reloads its subscriptions).
- **Send** and **Edit & resend** now show the whole message body in a syntax-highlighted editor. Previously only the first line of a formatted JSON body appeared.
- Scroll bars sit beside the message list, property, subscription and rule lists and the body editor instead of over their contents, as in sqlZilla.
- The message list's columns now start at the left edge instead of being centred in wide windows.

## 1.0.1 (2026-11-26)

- **Message bodies** are syntax-highlighted for JSON and XML. Press **Ctrl+F** in the body to find text: every match is highlighted, and Enter / F3 step through them.

## 1.0.0 (2026-11-26)

- First release.
- **Namespace explorer** with colour-coded groups (Production, Develop, Read-only…) as top-level branches; queues, topics and subscriptions show live active / dead-letter counts. Search filters groups, namespaces and loaded entities.
- **Sign-in** with a shared access connection string (namespace or entity scoped), interactive Entra ID through the Windows account broker, `DefaultAzureCredential` (Azure CLI, Visual Studio, managed identity…) or a service principal. Optional AMQP over WebSockets.
- **Peek** messages from the active, dead-letter and transfer dead-letter queues, including session-enabled entities; filter peeked messages; view bodies (JSON/XML pretty-printed, hex for binary) and all system and application properties.
- **Send** messages with any properties, scheduled enqueue, sessions and multiple copies (`{n}` and `{guid}` placeholders); **edit & resend** an existing message.
- **Resubmit** dead-lettered messages (optionally removing the originals), **delete** selected messages and **purge** a queue.
- **Create, edit and delete** queues, topics and subscriptions; manage subscription **rules** (SQL, correlation, true/false filters and actions).
- **Read-only groups** allow browsing and peeking but block every change.
- Copy or save messages as JSON; open tabs are restored on restart.
