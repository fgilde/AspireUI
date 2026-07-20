# AspireUI — Monaco Code Editor with C# IntelliSense — Design

**Date:** 2026-07-19 (Slice 11). User-requested: edit the generated `Program.cs` directly with full C#
IntelliSense so things the visual model can't express (configure lambdas, custom code) can be tweaked.

## Goal

A **"Code"** dockview tab with a Monaco editor showing the stack's `Program.cs`, offering completion,
signature help, hover quick-info, and live diagnostics (red squiggles). Saving parses the code back into
the stack model so the canvas stays authoritative.

## Architecture

IntelliSense is served by a Roslyn backend (`Microsoft.CodeAnalysis.CSharp`, already referenced).
Monaco registers language providers that call HTTP endpoints per trigger.

### Backend — `RoslynLspService`

A cached `AdhocWorkspace` with one C# project whose `MetadataReferences` are the Aspire/Nextended/
CommunityToolkit assemblies already loaded in the server's AppDomain (reuse `assembly.Location`) plus
the core framework reference assemblies (from `AppContext.BaseDirectory`'s trusted platform assemblies /
`typeof(object).Assembly` and friends). References are built **once** (expensive); each request swaps
only the single document's text and pulls a fresh `SemanticModel`. Roslyn only **compiles** (semantic
analysis) — it never executes user code, so this is safe.

Methods (offset = UTF-16 char offset into the code):
- `Complete(code, offset)` → `CompletionItem[]` via `CompletionService.GetCompletionsAsync`.
- `Hover(code, offset)` → quick-info text (symbol + its doc summary) via `SemanticModel`/`QuickInfo`.
- `Signature(code, offset)` → active method's parameter list.
- `Diagnostics(code)` → `Compilation.GetDiagnostics()` mapped to `{message, severity, start, end}`
  (start/end as offsets).

Records: `CompletionItem(string Label, string Kind, string InsertText, string? Detail)`,
`CodeDiagnostic(string Message, string Severity, int Start, int End)`,
`SignatureInfo(string Label, string[] Parameters)`.

### Endpoints (all on the authenticated `app2` group)

```
POST /stacks/{id}/code/complete    { code, offset } → CompletionItem[]
POST /stacks/{id}/code/hover       { code, offset } → { contents: string }   (204/empty if none)
POST /stacks/{id}/code/signature   { code, offset } → SignatureInfo | null
POST /stacks/{id}/code/diagnostics { code }         → CodeDiagnostic[]
POST /stacks/{id}/code/save        { name, code }   → Stack (or 422 with parse/compile errors)
```

`/code/save` runs `ImportService.Import(id, name, code, sidecarJson="")` (the existing markerless
parser), then `Persist`; returns the rebuilt stack. Compile errors surface as 422 like other persists.
`id` need not exist for the LSP endpoints (they analyze the posted `code` only), but `/code/save`
requires the stack (404 otherwise) so it persists to the right record.

## Save model (decided)

Save → `ImportService` re-parses `code` into nodes/edges/raw-statements → canvas stays the source of
truth. Anything the model can't represent becomes a raw statement. **Formatting/comments are lost** —
the code is regenerated canonically on the next preview. This is the accepted price of keeping the
canvas usable (vs. a raw-override mode).

## Frontend

- `monaco-editor` bundled locally (NOT the CDN loader) so it works offline / behind auth. Wire it with
  a thin wrapper (either `@monaco-editor/react` configured to use the local `monaco-editor`, or mount
  `monaco.editor.create` directly in a ref'd div — pick whichever builds clean under strict tsc/Vite).
- `CodeEditorPanel` dockview tab: loads `GET /stacks/{id}/preview` into the model on mount; registers
  a `csharp` CompletionItemProvider, HoverProvider, SignatureHelpProvider that POST to the endpoints
  (completion/hover/signature debounced to avoid a request per keystroke); on content change (debounced
  ~400ms) calls `/diagnostics` and sets Monaco markers.
- A **Save** button (and Ctrl+S) → `/code/save` → `setStack(updated)` so canvas/preview/packages
  refresh. Show compile/parse errors (422 body) in an inline alert; keep the editor content intact.
- Theme follows Mantine color scheme (`vs-dark` / `vs`).
- Register the panel in DockLayout (bump LAYOUT_KEY) next to Code Preview / Assistant.

## Error handling

- LSP endpoints never 500 on bad code: a failed parse/compile just yields empty completions / the
  diagnostics list (that's the point). Wrap Roslyn calls; on unexpected exception return an empty result.
- `/code/save` 422 with the compile-error list (reuse the existing `CompileErrors` shape); the editor
  does not lose the user's text.
- Provider fetch failures degrade silently (no completions) rather than throwing in Monaco.

## Testing

Backend (xUnit): `RoslynLspService.Complete` returns an item containing `AddRedis` for `code =
"var builder = ...; builder."` with the Redis reference present; `Diagnostics` flags an obviously broken
snippet and returns empty for a valid one; `/code/save` round-trips a valid Program.cs into a stack
(reuses ImportService, already tested) and 422s a broken one. Endpoint auth (401 without cookie).
Frontend: build gate (Monaco isn't meaningfully unit-testable here); a small pure test only if a mapper
is extracted.

## Non-Goals (this slice)

- Multi-file editing (only the AppHost `Program.cs`).
- Formatting preservation / round-trip fidelity of comments.
- Go-to-definition, rename, refactorings (completion + signature + hover + diagnostics only).
- IntelliSense over the raw-statement/extra-file bodies beyond what Program.cs contains.

## Deferred (BACKLOG)

Go-to-def/rename, formatter, multi-file, per-keystroke perf tuning beyond a references cache.
