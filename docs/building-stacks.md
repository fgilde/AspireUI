# Building Stacks

A stack is a canvas of resource nodes and references between them, backed 1:1 by an Aspire AppHost
`Program.cs`. Everything you do in the editor keeps that C# in sync — the **Code preview** panel is
a live, read-only view of exactly what will be generated.

![The editor](screenshots/editor.png)

## The canvas

Nodes are resources (with a brand icon per type); edges are connections. The canvas has a **minimap**,
**auto-arrange** (dagre layout) and a **resource search** box (top-left), **snap-to-grid**, and a
**right-click context menu** per node (edit properties / duplicate / delete). Dragging is live, and a
per-node status dot reflects the run state while the stack is running.

## The palette

The palette lists every resource type AspireUI knows about, grouped (Database, Cache, Messaging,
Search, Vector, Identity, AI, Compute, Integration, Observability, …). This list isn't hardcoded:
it's built by **reflecting** over the referenced Aspire hosting assemblies — the official
`Aspire.Hosting.*` packages plus `CommunityToolkit.Aspire.*` and Nextended integrations — so new
Aspire integrations show up automatically once their package is referenced. A curated overlay adds
friendly labels, brand logos, grouping and descriptions on top; resources without an overlay still
work generically. The generated project only references the packages for resources you actually use.

Click a palette entry to open its add dialog.

## The add-resource dialog

Adding a resource opens a form (not a bare node) that also teaches you the Aspire API:

![Add-resource dialog](screenshots/add-dialog.png)

- A one-line **description** of what the resource is.
- **Name** — required; becomes the resource's variable name in the generated code.
- If the `AddX` method has more than one usable **overload**, a selector picks the signature.
- The overload's parameters as typed controls: `string` → text, numeric → number, `bool` → switch,
  `enum` → dropdown, and **configure-lambdas** (`Action<TOptions>`) expand into their settable fields
  (e.g. a GitHub repo's `GitRef` branch).
- **References** — a multiselect of existing resources this one should reference; picking them adds
  the matching `.WithReference(...)` edges on create.
- A **live code preview** shows the exact C# the dialog will generate as you type — great for
  learning what each option produces.

## The property grid

Selecting a node shows its editable fields. Small **ℹ️ icons** on the sections explain the underlying
Aspire concept as you go.

- **Quick settings** (for resources with endpoints): a **Publicly accessible** toggle
  (`WithExternalHttpEndpoints()`) and an **HTTP port** field (`WithHttpEndpoint(port:)`) — the common
  cases, with full control still available below.
- The **Add** parameters, re-derived from the overload.
- A **capabilities** section: every `With*` method on the resource **and** every `Add*` method on its
  own builder (e.g. `ollama.AddModel(...)`, `pg.AddDatabase(...)`). Each lists its calls as editable
  rows with an add-row form.
- **Environment variables** as a name/value list. Each value has a **Text ⇄ Expr** toggle — Text is a
  quoted string literal; **Expr** is raw C# (e.g. another resource's endpoint). In Expr mode a 🔗 menu
  lets you **insert a reference** to another resource (its HTTP endpoint or connection string). Free
  typing always works.
- A generic **raw-call** escape hatch for anything the catalog doesn't cover — editing is never blocked.

## References & dependencies

Wire resources by dragging an edge on the canvas, via the References picker in the properties panel,
or straight from the add dialog. Click an edge's chip to open its menu: toggle **References**
(`WithReference`) and **Waits for** (`WaitFor`) independently (both can apply), **reverse** the
direction, or **remove** the connection. `WaitFor`-only edges render dashed.

## Validation

The **Validation** panel (and the health badge in the header) run Roslyn diagnostics over the
generated code and list any errors/warnings. Clicking the badge focuses the panel and flashes it.

## Code preview

The **Code preview** panel is the generated `Program.cs`, syntax-highlighted, refreshed after every
save. It's read-only — think of it as a live receipt, not an editing surface. Nothing you do bypasses
it: every change on the canvas or in the property grid is round-tripped through the same model that
generates this code.

## Code editor (Monaco + C# IntelliSense)

The **Code** panel is a full editor for `Program.cs` with C# **IntelliSense** — completion, signature
help, hover, and live error squiggles — backed by Roslyn on the server. Use it for things the visual
model can't express directly (a `configure` lambda like `o => o.GitRef = "master"`, an expression, a
bit of custom wiring). **Save** (button or Ctrl+S) re-parses your code back into the node graph, so the
canvas stays in sync — anything the graph can't represent is kept as a raw statement. Note: because the
code is regenerated canonically from the model, **your formatting and comments are not preserved** on
save. Compile/parse errors are shown inline and your edits are kept so you can fix them. (The editor
only analyzes — it never runs your code.)

## Packages panel

The **Packages** panel lists every NuGet package the generated AppHost project needs, and which
resource(s) pulled each one in — handy for sanity-checking what a stack actually depends on before
you run or export it.
