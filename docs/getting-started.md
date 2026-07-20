# Getting Started

## Run it

AspireUI is an ASP.NET Core server (net10.0) that serves the React SPA. For development:

```bash
dotnet run --project src/AspireUI.Server
```

Opens at **http://localhost:5158**.

To self-host on a server instead, see [Running & Deploying](running-and-deploying.md).

## First run: create the admin

On first launch AspireUI runs a **setup wizard**: it checks your environment (.NET SDK, Docker, git)
and has you create the first **admin** user. After that you log in with username/password; admins can
add more users under **Users**. It's cookie-based auth for a small-team, local-first tool — put a
reverse proxy + TLS in front before exposing it beyond localhost.

## Your first stack

![Stacks overview](screenshots/overview.png)

1. The **Stacks overview** lists your stacks — each card shows a live status traffic-light and
   run/stop/open-dashboard buttons, plus a ⋯ menu (rename / duplicate / delete) and a search box.
2. Click **New Stack** to start blank, or use the **demo dropdown** to create a runnable example
   (see below) — either way you land in the editor.
3. In the editor, click a resource in the **Palette** to add it (the add dialog previews the C# it
   generates), or import an existing AppHost (see [Importing](importing.md)).
4. Select a node to edit it in the **Properties** panel, wire up references between nodes, and watch
   the **Code preview** update with the generated `Program.cs`.
5. Hit **Run** to start the stack, then open the **Dashboard** panel or the Aspire dashboard link.

## Demo templates

The overview's demo dropdown creates a ready-to-run stack without building one by hand:

- **Local AI Demo** — Ollama (+ models via `AddModel`), LocalAI, and n8n (waiting on both), CPU-safe.
- **Web backend** — Postgres + Redis + RabbitMQ.
- **Elasticsearch + Kibana** — ES with a Kibana UI wired to it.
- **Kafka + UI** — Kafka broker with the provectus Kafka-UI.
- **Keycloak + Postgres** — identity server backed by Postgres.
- **Observability (Seq)** — a Seq log server.

## Layout, themes & shortcuts

The workspace is built from dockable panels (Palette, Canvas, Properties, Code preview, Packages,
Logs, Assistant, Publish/Deploy, Code, Dashboard, Validation) — split, tab, float, and drag them,
save named **layouts**, pick a **theme**, and use the **command palette** (Ctrl/⌘+K). See
[UI, Themes & Shortcuts](ui-and-shortcuts.md).
