# Building Stacks

A stack is a canvas of resource nodes and references between them, backed 1:1 by an Aspire AppHost
`Program.cs`. Everything you do in the editor keeps that C# in sync — the **Code preview** panel is
a live, read-only view of exactly what will be generated.

## The palette

The palette lists every resource type AspireUI knows about, grouped (containers, databases, AI,
messaging, …). This list isn't hardcoded: it's built by reflecting over the Aspire hosting
assemblies (plus a few extra integrations like Ollama, n8n, LocalAI, Supabase, and GitHub
repositories) referenced by the server project, so new Aspire integrations show up automatically
once their package is referenced. A curated overlay adds friendlier labels/icons/grouping on top of
the reflected data — resources without an overlay still work generically.

Click a palette entry to add it to the canvas.

## The add-resource dialog

Adding a resource opens a small form instead of dropping a bare node:

- **Name** is always required — it becomes the resource's variable name in the generated code.
- If the resource's `AddX` method has more than one usable overload (e.g. with vs. without a tag),
  an **overload** selector picks which signature to use.
- The chosen overload's parameters render as typed controls: `string` → text input, `int`/numeric →
  number input, `bool` → switch, `enum` → dropdown. Required fields must be filled before you can
  confirm; optional ones can stay blank.

## The property grid

Selecting a node shows its editable fields:

- The **Add** parameters from step above, re-derived from the overload you picked.
- A **capabilities** section: every `With*` method available on that resource (env vars, ports,
  volumes, bind mounts, options, …) **and** every `Add*`-prefixed method exposed on the resource's
  own builder (e.g. `ollama.AddModel("llama3.2")`, `pg.AddDatabase("db")`) — these behave exactly
  like `With*` calls in the grid and the generated code. Each capability lists its existing calls as
  rows you can edit or remove, with an add-row form for a new one.
- **Env vars** get a tidy two-column (name / value) list. A value that's an expression rather than a
  literal (e.g. wired from a raw `ReferenceExpression` variable) shows as a read-only chip instead
  of an editable field, since editing it would re-quote and corrupt the expression.
- Anything not covered by the catalog (an unknown resource, an uncommon method) is still reachable
  through a generic raw-call escape hatch — editing never gets blocked by an unrecognized method.

## References

Wire one resource to another either by dragging an edge on the canvas, or via the node picker
("which resource, from where") in the properties panel — useful when the canvas is crowded.
References become `.WithReference(...)`; dependency ordering (`.WaitFor(...)`) is drawn as a dashed
edge.

## Code preview

The **Code preview** panel is the generated `Program.cs`, syntax-highlighted, refreshed after every
save. It's read-only — think of it as a live receipt, not an editing surface. Nothing you do bypasses
it: every change on the canvas or in the property grid is round-tripped through the same model that
generates this code.

## Packages panel

The **Packages** panel lists every NuGet package the generated AppHost project needs, and which
resource(s) pulled each one in — handy for sanity-checking what a stack actually depends on before
you run or export it.
