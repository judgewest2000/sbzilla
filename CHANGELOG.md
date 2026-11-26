# Release notes

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
