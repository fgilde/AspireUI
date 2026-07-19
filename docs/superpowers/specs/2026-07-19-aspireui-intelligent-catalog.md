# AspireUI — Intelligent Catalog + Dynamic Config UI + Dockable Layout (Design)

**Date:** 2026-07-19 (Slice 3)
**Builds on:** tool-ux slice. Makes resource configuration genuinely intelligent and the
layout freely arrangeable.

## Goals

1. **Reflection-driven catalog** — every `AddX`/`WithX` exposes its REAL parameters from the
   Aspire assemblies: type (string/int/bool/enum), enum option lists, required vs optional,
   defaults — merged with curated overlays for labels/icons/grouping.
2. **Overload-aware** — methods with multiple renderable overloads expose all of them; the UI
   lets the user pick which signature to use.
3. **Dynamic Add-node dialog** — adding a resource opens a form: required fields must be filled,
   optional may be; enum→dropdown, string→input, int→number, bool→switch. Overload chooser when
   more than one renderable overload exists.
4. **Property grid depth for ALL resources** — every resource's `WithX` methods are available
   (not just curated ones); env vars (and other repeatable withs) render as a clean list.
5. **More integrations** — `Nextended.Aspire.Hosting.{Supabase,N8n,LocalAI}` referenced and
   discovered (with their options). CommunityToolkit.Aspire.* can be added the same way.
6. **Dockable panels** — palette / canvas / properties / preview as dockview panels the user can
   split, tab, float, and rearrange; layout persisted (localStorage).
7. **Syntax-highlighted code preview** (C#).
8. **Node deletion.**

## Non-Goals (this slice)

Auth, wizard, deploy, reverse-proxy, foreign-node import UI, `AddProject<T>` generics, semantic
compile-check. Editing generated code in-place (preview stays read-only).

## Architecture

The intelligence lives in the **catalog**; serialization stays positional so CodeGen/Import and
the round-trip invariant are untouched.

```
Backend (net10.0)
  CatalogService   reflection extracts overloads + typed params + enums per AddX/WithX;
                   overlay merges label/icon/group/hidden. NO change to codegen/import.
  server csproj    + Nextended.Aspire.Hosting.{Supabase,N8n,LocalAI} package refs
                   (+ their per-resource package mapping in CodeGen so generated projects build)
Frontend (React + Mantine + dockview)
  DockLayout       palette / canvas / properties / preview as dock panels (persisted)
  AddResourceDialog  dynamic form (overload chooser + typed fields) on add
  PropertyGrid     per-resource withs from catalog; env/repeatable withs as clean lists; overload-aware
  CodePreview      react-syntax-highlighter (csharp)
  Canvas           node delete (key + button)
```

## Catalog model (reflection + overlay)

```
CatalogParam    { Name, Type, Required, Default?, Options?, EnumTypeName?, Label }   // Type: string|int|bool|enum
CatalogOverload { Params: CatalogParam[] }                                            // one renderable signature
CatalogMethod   { Method, Label, Overloads: CatalogOverload[] }                       // a WithX (>=1 overload)
ResourceType    { AddMethod, Label, Icon?, Group?, AddOverloads: CatalogOverload[], Withs: CatalogMethod[] }
```

**Reflection rules:**
- `AddX`: extension methods on `IDistributedApplicationBuilder` returning `IResourceBuilder<T>`.
  The first user param is the resource **name** (handled separately, always required). Remaining
  params form the overload's `Params`.
- `WithX`: extension methods on `IResourceBuilder<T>` returning `IResourceBuilder<T>` (or the
  same). Params after the receiver form the overload's `Params`.
- A param is **renderable** if its type is `string`, `int`/`long`/`double`, `bool`, or an `enum`.
  An overload is renderable if ALL its params are renderable OR optional (params with a default
  value that aren't renderable can be dropped from the form — they'll use their default).
  Overloads with a required non-renderable param (delegate, complex type) are **excluded**.
- `Required` = param has no default value (and isn't nullable-with-null-default). `Default` =
  `HasDefaultValue ? value : null`. `Options`/`EnumTypeName` filled for enum types
  (`Enum.GetNames`, short type name).
- Group `WithX` overloads under one `CatalogMethod` by method name. A method with no renderable
  overload is omitted (still reachable via a raw-call escape hatch in the grid).
- Overlay (`catalog/*.json`) still supplies label/icon/group and MAY hide noise
  (`"hidden": ["WithX", ...]`) or relabel. Reflection is the source of truth for params.

## Serialization (unchanged — low risk)

Node stays `{ AddMethod, ResourceName, AddArgs[raw literals], WithCalls[{Method, Args[raw]}] }`.
- The chosen overload is **implied by arity**: on reopen, the UI matches stored `AddArgs.length`
  (or a WithCall's `Args.length`) to the catalog overload with the same parameter count, and
  renders that overload's typed fields. No new stored field.
- **Literals by type:** string→`"value"` (escaped); int→`8080`; bool→`true`/`false`; enum→
  `EnumTypeName.Member` (unquoted). The client transform (`toLiteral`/`fromLiteral`, extended
  for enum using `EnumTypeName`) produces these; CodeGen emits them verbatim. Enum emission
  relies on the generated project's implicit usings / fully-qualified short name resolving in
  the Aspire namespace (best-effort; acceptable for this slice).

## Add-node dialog (dynamic form)

Clicking a palette resource opens `AddResourceDialog`:
1. Always: **Name** field (the resource name / var), required.
2. If `AddOverloads.length > 1`: an **overload Select** (labelled by signature, e.g.
   `image, tag?`). Default to the first/simplest.
3. Render one control per param of the chosen overload: string→TextInput, int→NumberInput,
   bool→Switch, enum→Select(Options). Required params validated (can't confirm until filled);
   optional params blank-able.
4. Confirm → create node with `ResourceName` + `AddArgs` = the filled params as literals in the
   overload's order (trailing blank optionals trimmed).

## Property grid

- Resolve the node's `ResourceType`; show **Name** + the AddX params (re-derived from the stored
  overload) editable.
- **WithX section**: for each `CatalogMethod`, list existing calls (matched by method name) as
  rows; each row shows the chosen overload's typed fields; add-row opens the same
  overload-aware mini-form; remove-row deletes. Repeatable withs (env vars, endpoints, mounts)
  therefore render as clean editable lists.
- **Env vars** specifically get a tidy two-column (Name / Value) list with add/remove.
- Raw-call escape hatch retained for any `WithX` not in the catalog.

## Node deletion

Delete key on a selected node, and a delete button in the node/property panel. Client-side:
remove the node from `stack.nodes` AND any edges referencing it, then `saveStack`. No new
endpoint. React Flow `onNodesChange`/`onEdgesChange` "remove" changes are also honored.

## Dockable layout

`dockview` hosts four panels — Palette, Canvas, Properties, Preview — as a default docked layout
(palette left, canvas center, properties right, preview bottom). User can split/tab/float/drag.
Layout serialized to `localStorage` (key per app, not per stack) and restored on load; a "reset
layout" action restores the default.

## Code preview

`react-syntax-highlighter` (Prism, `csharp` language, a dark theme matching Mantine). Read-only,
fed by the existing `/preview` endpoint; refreshes on stack content change (already fixed to key
on serialized content).

## Packages / generated-project build

- Server csproj references `Nextended.Aspire.Hosting.Supabase`, `Nextended.Aspire.Hosting.N8n`,
  `Nextended.Aspire.Hosting.LocalAI` (+ base `Nextended.Aspire.Hosting`/`Nextended.Aspire` if
  required) at their resolvable versions, so their `AddX` are reflected and appear in the palette.
- CodeGen's resource→package map is extended (or driven from an overlay `"package"` field —
  consolidated to ONE source of truth this slice) so generated projects that use these resources
  reference the right packages and build/run.

## Error handling

- Reflection on a param type it can't classify → treat overload as non-renderable (excluded), not
  a crash.
- Add dialog with unfilled required field → confirm disabled.
- Enum whose value doesn't resolve at generated-build time → best-effort (documented, deferred).
- dockview with a corrupt/old persisted layout → catch, fall back to default layout.
- Deleting a node that's referenced → its edges are removed too (no dangling `WithReference`).

## Testing

Backend (xUnit):
- Reflection: `AddContainer` exposes ≥1 overload; a known enum param surfaces `Type == "enum"`
  with non-empty `Options` (find a real Aspire method with an enum param, e.g. a lifetime/mode);
  an optional param surfaces `Required == false`.
- A Nextended resource (e.g. `AddN8n`/`AddSupabase`/`AddLocalAI`, whichever exists) appears in
  the catalog once its package is referenced.
- CodeGen still round-trips (existing invariant) — unchanged serialization.
- Generated csproj includes the package for a Nextended resource used in a stack.

Frontend (Vitest):
- Extended transform: enum literal `EnumTypeName.Member` ↔ field value; overload matching by
  arity picks the right param set; existing container transform still passes.

UI (build gate): `npm run build` clean; dockview + dialog + highlighter compile.

## Deferred (tracked)

Auth, wizard, deploy, reverse-proxy, foreign-node import UI, `AddProject<T>` generics, semantic
compile-check, in-place code editing, per-stack layout persistence, NU1903 advisory bump.
