# Live Resources & Logs

When a stack is running, AspireUI connects to the AppHost's **Aspire resource service** — the exact
same live feed the Aspire dashboard renders — and overlays it onto the canvas. You see real
per-resource state, the URLs each resource exposes, the child resources a builder spawns, and you can
stream any resource's console logs, all without leaving the editor.

![A running Supabase + Observability stack with live child resources](screenshots/live-resources.png)

*(Above: the `supabase` builder node has spawned `supabase-db`, `supabase-auth`, `supabase-kong`, …
as live child nodes; the observability macro contributes the `monitoring-*` resources. Each carries
its own status dot and a link to its endpoint.)*

## How it works

Running a stack shells `dotnet run` on the generated AppHost. Before launching, AspireUI hands the
AppHost a **deterministic resource-service endpoint and API key** (via the standard
`DOTNET_RESOURCE_SERVICE_ENDPOINT_URL` / `DOTNET_DASHBOARD_RESOURCESERVICE_APIKEY` variables). It then
opens its own gRPC client (`aspire.v1.DashboardService`) against that endpoint and subscribes to the
`WatchResources` stream. The canvas polls the resulting snapshot a few times a second while the stack
is `Starting` / `Running`, and clears it when you stop.

Nothing about your stack model changes — the live view is a transient overlay on top of the graph you
built.

## Per-resource status on your nodes

Each builder node you placed gets a **traffic-light dot** driven by that resource's *real* Aspire
state (not just the shared stack-level run state):

| Color | Meaning |
|---|---|
| 🟢 Green | Running / Healthy |
| 🟡 Yellow | Waiting / Starting / Pending |
| 🔴 Red | Failed / Exited / Unhealthy |
| ⚪ Grey | Unknown / no state yet |

Hover the dot for the exact state string. If the resource exposes a browsable URL, an **↗ link** on
the node opens it in a new tab.

## Spawned child resources

A single builder often materializes into **many** actual Aspire resources. `AddSupabase(...)` alone
brings up `supabase-db`, `supabase-auth`, `supabase-realtime`, `supabase-kong`, `supabase-storage`,
and more; a macro extension like `AddObservabilityStack` adds Grafana, Loki, Prometheus, Promtail,
cAdvisor and a postgres-exporter.

AspireUI renders these as **translucent, dashed child nodes** hanging off the builder they belong to,
with a faint edge back to their parent (parent/child comes from the resource's Aspire *relationships*,
so the nesting is exactly what the dashboard shows). Grandchildren chain to their live parent. Each
child node has the same status dot, endpoint link, and log button as a top-level node.

Resources that don't map to any node you placed (e.g. the containers a macro extension creates on its
own) appear as an **orphan cluster** to the right of the graph — visible and streamable, just not
anchored to one of your nodes. Aspire's own hidden resources (like the dashboard process) are not
shown.

> Child nodes are read-only and ephemeral: you can't drag, edit, or delete them, and they vanish when
> the stack stops. They're a live window into what's actually running, not part of the saved stack.

## Streaming console logs

Every running node and child node has a **terminal icon**. Click it to open the **log drawer** at the
bottom of the editor, which streams that specific resource's stdout/stderr live (over Server-Sent
Events, backed by the resource service's `WatchResourceConsoleLogs`).

- **stderr** lines are highlighted.
- The view **auto-scrolls** to the newest line — unless you scroll up to read history, then it holds
  your position.
- The buffer is capped (the most recent ~2000 lines) so a chatty container won't grow it forever.
- Lines carry Aspire's timestamp prefix, matching the dashboard's log view.

Open logs for the parent builder to watch the whole thing come up, or drill into a single child (say
`supabase-db`) when one resource is the one misbehaving.

## When to use the Aspire dashboard instead

The live overlay covers status, URLs, topology, and logs right on the canvas. For deeper telemetry —
traces, metrics, structured/queryable logs, environment-variable inspection, per-resource commands —
open the full **Aspire dashboard** (the **Dashboard** tab or the header link). See
[Running & Deploying](running-and-deploying.md#the-dashboard-panel).
