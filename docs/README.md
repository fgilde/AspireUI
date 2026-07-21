<img width="2547" height="1261" alt="image" src="https://github.com/user-attachments/assets/54d51236-c6c0-4cf7-a4a3-ae5b814a879a" />

<p align="center"><img src="aspireui.png" alt="AspireUI" width="140" /></p>

# AspireUI

AspireUI is a visual, web-based builder for [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/)
AppHost projects. Drag resources out of a reflection-driven catalog, wire up references, tweak
properties in a typed grid, and watch the generated C# update live — then **run** the stack, open the
**Aspire dashboard**, **publish** to Docker Compose / Kubernetes / Bicep / manifest, or open the
project straight in your IDE. Import an existing AppHost, start from a template, or let the built-in AI
assistant build it for you.

It also doubles as a way to **learn Aspire**: every resource carries a description, the add dialog
shows the exact C# it will generate, and the property grid has inline explanations.

![Stacks overview](screenshots/overview.png)

## Highlights

- **Visual canvas** — nodes for resources, edges for `WithReference`/`WaitFor`, minimap, auto-layout,
  resource search, right-click context menu, snap-to-grid.
- **Huge resource catalog** — reflection over the official `Aspire.Hosting.*` and
  `CommunityToolkit.Aspire.*` packages: databases (Postgres, SQL Server, MySQL, MongoDB, Cosmos,
  Oracle), caches (Redis, Valkey, Garnet), messaging (Kafka, RabbitMQ, NATS, ActiveMQ), search &
  vector (Elasticsearch, Qdrant, Milvus), identity (Keycloak), AI (Ollama, LocalAI, Azure OpenAI),
  compute (Project, Java/Spring, Go, Python, Container), Dapr, YARP, Seq, Azure services and more.
- **Typed property grid** — smart inputs per parameter (enums → dropdowns, configure-lambdas →
  expandable fields, type-filtered resource-reference pickers with inline ＋-create, a server-side
  path picker for folder/project params), environment variables with a Text/Expression toggle and a
  resource-reference picker, plus quick settings (public endpoint toggle, HTTP port).
- **Setup / macro extensions** — composite helpers like `AddObservabilityStack` or `AddDapr` (which
  wire up several resources at once) are discovered automatically and grouped under **Setup**.
- **Live C# preview** and a full **Monaco code editor** with Roslyn IntelliSense; edits parse back into the graph.
- **Run & live view** — run/stop with logs; while running, the canvas shows **real per-resource
  status, endpoint URLs, and the child resources each builder spawns** (from the Aspire resource
  service), with **per-resource console-log streaming**. Open the Aspire dashboard for full telemetry.
- **Import** — an existing AppHost (`.cs` / `.csproj` / `.zip`) or a `docker-compose.yml`.
- **Publish / deploy** — Docker Compose, Kubernetes (Helm), Azure Bicep, or the Aspire manifest;
  view/download the artifacts; deploy Compose locally.
- **Whole-stack validation** — Roslyn diagnostics over the generated code, surfaced as a health badge.
- **Themes** (GitHub, Aspire, Blazor, Dracula, Nord, Terminal, …), **command palette** (Ctrl/⌘+K),
  keyboard shortcuts, saveable dock **layouts**, undo/redo, toast notifications.
- **Auth** — a first-run wizard creates an admin user; cookie-based login; user management.

![A running stack with live child resources and status](screenshots/live-resources.png)

## Where to start

- **[Getting Started](getting-started.md)** — run AspireUI, the first-run wizard, create your first stack.
- **[Building Stacks](building-stacks.md)** — palette, add-resource dialog, property grid, references, code editor, validation.
- **[Importing](importing.md)** — bring in an existing AppHost from `.cs`, `.csproj`, or a `.zip`.
- **[AI Assistant](ai-assistant.md)** — configure a provider and let the assistant edit your stack.
- **[Running & Deploying](running-and-deploying.md)** — run locally, publish to Compose/K8s/Bicep, open the dashboard, self-host AspireUI.
- **[Live Resources & Logs](live-resources.md)** — per-resource status, spawned child resources, endpoint links, and live log streaming on the canvas.
- **[UI, Themes & Shortcuts](ui-and-shortcuts.md)** — themes, command palette, layouts, keyboard shortcuts.

## Notes / limitations

Login-gated (a first-run wizard creates the admin user), but still a small-team, local-first tool —
don't expose its port directly to the internet without a reverse proxy and TLS in front. Running a
stack shells out to `dotnet run` and Aspire resources commonly start containers, so the .NET SDK and
Docker both need to be available wherever AspireUI runs.
