# AspireUI Monaco + C# IntelliSense Implementation Plan

> REQUIRED SUB-SKILL: superpowers:subagent-driven-development (or inline execution).

**Goal:** A Monaco "Code" tab that edits the stack's Program.cs with C# completion, signature help,
hover, and live diagnostics from a Roslyn backend; saving re-parses into the stack model.

**Architecture:** `RoslynLspService` (cached AdhocWorkspace, fixed MetadataReferences from the loaded
Aspire assemblies, per-request document swap → SemanticModel; compile-only, no execution). HTTP
endpoints on the authenticated group. Monaco bundled locally, providers call the endpoints; Save routes
through the existing `ImportService`.

**Tech Stack:** .NET 10, Microsoft.CodeAnalysis.CSharp (+ .Features for CompletionService); React,
monaco-editor.

## Global Constraints
- Tool projects net10.0. Conventional Commits, NO Co-Authored-By, push after every commit.
- New endpoints on `app2` (RequireAuthorization) only.
- Roslyn compiles/analyzes only — never executes user code.
- LSP endpoints must not 500 on invalid code — degrade to empty results.
- Don't break the 81 backend / 20 vitest tests.

---

### Task 1: RoslynLspService (completion + diagnostics + hover + signature)

**Files:** Create `src/AspireUI.Server/Services/RoslynLspService.cs`; add package
`Microsoft.CodeAnalysis.CSharp.Features`; Test `tests/AspireUI.Server.Tests/RoslynLspTests.cs`.

**Interfaces produced:**
- `record CompletionItem(string Label, string Kind, string InsertText, string? Detail);`
- `record CodeDiagnostic(string Message, string Severity, int Start, int End);`
- `record SignatureInfo(string Label, string[] Parameters);`
- `class RoslynLspService` with:
  - `Task<IReadOnlyList<CompletionItem>> CompleteAsync(string code, int offset)`
  - `IReadOnlyList<CodeDiagnostic> Diagnostics(string code)`
  - `Task<string?> HoverAsync(string code, int offset)`
  - `Task<SignatureInfo?> SignatureAsync(string code, int offset)`

- [ ] Step 1: Add the Features package to `AspireUI.Server.csproj`
  (`Microsoft.CodeAnalysis.CSharp.Features` at the version matching the existing
  `Microsoft.CodeAnalysis.CSharp` 5.6.0 — check the installed/compatible version first; restore).
- [ ] Step 2: Failing tests in RoslynLspTests.cs:
  - `CompleteAsync` on `"var builder = Aspire.Hosting.DistributedApplication.CreateBuilder(args);\nbuilder."`
    with offset at end returns an item whose Label contains `AddRedis` (Redis assembly is referenced by
    the server, so its extension method is in scope). Assert `result.Any(c => c.Label.Contains("AddRedis"))`.
  - `Diagnostics("var x = ;")` returns at least one Error; `Diagnostics("var x = 1;")` returns no Error-severity item.
- [ ] Step 3: Run, verify fail.
- [ ] Step 4: Implement. Build the reference set ONCE (static/lazy): every
  `AppDomain.CurrentDomain.GetAssemblies()` with a non-empty `Location` (dedup by location) →
  `MetadataReference.CreateFromFile`. Create an `AdhocWorkspace`, a `ProjectInfo` (C#, OutputKind
  DynamicallyLinkedLibrary, `ImplicitUsings`-equivalent via a global usings document OR rely on
  fully-qualified names in tests) with those references. Per call: add/replace a document with `code`,
  `GetSemanticModelAsync`. Completion via `CompletionService.GetService(document).GetCompletionsAsync(document, offset)`
  → map items (Kind from tags, InsertText from `item.DisplayText`, Detail from
  `GetDescriptionAsync` first line — keep cheap). Diagnostics via `(await document.GetSemanticModelAsync()).GetDiagnostics()`
  filtered to CS diagnostics, mapped with `d.Location.SourceSpan.Start/End`. Hover: symbol at offset via
  `SymbolFinder`/`SemanticModel.GetSymbolInfo` on the token → `symbol.ToDisplayString()` + XML doc summary.
  Signature: nearest invocation's method group → parameter display strings. Wrap each public method so an
  exception yields an empty/null result (never throw).
- [ ] Step 5: Run tests, verify pass. Run the full backend suite (no regressions).
- [ ] Step 6: Commit `feat: roslyn LSP service (completion, diagnostics, hover, signature)` + push.

---

### Task 2: Code endpoints (LSP + save)

**Files:** Modify `src/AspireUI.Server/Endpoints/StackEndpoints.cs`; Test extend RoslynLspTests.cs or
new `CodeEndpointTests.cs`.

**Interfaces:** request records `CodeRequest(string Code, int Offset)`, `CodeSaveRequest(string Name, string Code)`.

- [ ] Step 1: Register a `RoslynLspService` singleton (like `run`/`publish`). Map on `app2`:
  - `POST /stacks/{id}/code/complete` (CodeRequest) → `await lsp.CompleteAsync(...)`
  - `POST /stacks/{id}/code/hover` (CodeRequest) → `new { contents = await lsp.HoverAsync(...) }`
  - `POST /stacks/{id}/code/signature` (CodeRequest) → `await lsp.SignatureAsync(...)`
  - `POST /stacks/{id}/code/diagnostics` (CodeRequest) → `lsp.Diagnostics(req.Code)`
  - `POST /stacks/{id}/code/save` (CodeSaveRequest): 404 if `store.Get(id)` null; else
    `Persist(import.Import(id, req.Name, req.Code, ""))` (reuses the existing markerless parser + the
    422-on-compile-error path). The LSP endpoints do NOT require the stack to exist.
- [ ] Step 2: Test: `POST /code/complete` with the redis snippet returns items incl. AddRedis;
  `/code/save` with a valid Program.cs 200s and the returned stack has the parsed nodes; `/code/save`
  unknown id → 404; a code endpoint without auth cookie → 401 (NoAuthTestFactory).
- [ ] Step 3: Run full backend suite green. Commit `feat: code editor endpoints (LSP + save via import)` + push.

---

### Task 3: Monaco editor panel (frontend)

**Files:** `web/package.json` (+ `monaco-editor`, and `@monaco-editor/react` if used); `web/src/api.ts`
(+ code* calls); create `web/src/editor/CodeEditorPanel.tsx`; `web/src/editor/DockLayout.tsx` (register
+ layout bump). Check `web/vite.config.ts` — bundling monaco may need worker config.

- [ ] Step 1: `npm i monaco-editor` (+ `@monaco-editor/react` if chosen). Verify Vite builds monaco
  workers (may need `?worker` imports or the `@monaco-editor/react` loader pointed at the local package
  via `loader.config({ monaco })`). Confirm `npm run build` stays clean.
- [ ] Step 2: api.ts — `codeComplete(id, code, offset)`, `codeHover`, `codeSignature`,
  `codeDiagnostics(id, code)`, `codeSave(id, name, code): Promise<Stack>` (POST via `ok()`).
- [ ] Step 3: CodeEditorPanel.tsx: mount Monaco (language `csharp`, theme from
  `useMantineColorScheme` → `vs-dark`/`vs`), initial value from `api.previewStack(stack.id)`. Register
  (once) a completionItemProvider/hoverProvider/signatureHelpProvider for `csharp` that call the api
  with the current model text + offset (`model.getOffsetAt(position)`); map results to Monaco shapes.
  On model change, debounce ~400ms → `codeDiagnostics` → `monaco.editor.setModelMarkers`. A Save button
  + Ctrl+S command → `codeSave(stack.id, stack.name, model.getValue())` → `setStack`; on 422 show the
  errors in an inline alert without clearing the editor.
- [ ] Step 4: Register `code-editor` component in DockLayout, add to default layout (within the
  preview group), bump `LAYOUT_KEY` to v5.
- [ ] Step 5: `npm run build` clean + `npm test` green. Commit `feat: monaco code editor tab with C# intellisense` + push.

---

### Task 4: Docs + E2E verify

**Files:** `docs/building-stacks.md` (or a new doc) + README feature line; ledger.

- [ ] Step 1: Document the Code tab (edit Program.cs, IntelliSense, save re-parses, formatting/comments
  not preserved).
- [ ] Step 2: Real E2E on the running server: `POST /stacks/{id}/code/complete` with a `builder.` snippet
  returns AddX items; `/code/diagnostics` flags a broken edit; `/code/save` of an edited Program.cs
  (e.g. adding `o => o.GitRef = "master"` to a github node) round-trips and the stack reflects it.
- [ ] Step 3: `dotnet test` + `npm run build` + `npm test` green.
- [ ] Step 4: Commit `docs: code editor guide` + push. Update ledger. Final whole-branch review.

## Self-Review
- Coverage: LSP service (T1), endpoints incl. save-via-import (T2), Monaco panel (T3), docs+E2E (T4). ✔
- Non-breakage: new endpoints on app2; save reuses ImportService (no new persistence path); no codegen change. ✔
- Security: Roslyn compile-only, no execution; all endpoints authed. ✔
- Types consistent: CompletionItem/CodeDiagnostic/SignatureInfo identical across backend + api.ts. ✔
- Perf: references built once; per-request document swap. Completion/hover/signature debounced client-side.
