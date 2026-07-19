# AspireUI — Demo Templates + Model Power + Run Diagnostics (Design)

**Date:** 2026-07-19 (Slice 4)
**Builds on:** intelligent-catalog slice. Reference demo: `Nextended/Tests/TestProjects/AiStack.AppHost`.

## Goals

1. **Model power** so real stacks (like the AiStack demo) are representable:
   - **Raw statements**: `StackModel.RawStatements: List<string>` — verbatim C# lines emitted inside
     the marker block (e.g. `var localAiOpenAiBase = ReferenceExpression.Create($"...");`).
     Import preserves unrecognized marker-block statements as raw statements instead of dropping them.
   - **WaitFor edges**: `EdgeModel.Kind = "waitFor"` → `.WaitFor(x)`; rendered as dashed edge.
   - Already-possible today (no model change): child adds as WithCalls (`ollama.AddModel("llama3.2")`),
     options lambdas as raw AddArgs, expression env values as raw With args.
2. **Demo templates**: overview gets a "Create from demo" dropdown; picking **Local AI Demo** creates
   a runnable stack: Ollama (2 models via AddModel calls) + LocalAI (models + UIs) + n8n (waitFor both,
   env wiring via a raw ReferenceExpression variable). CPU-safe (no GPU withs; user can add
   `WithGPUSupport` via the grid). The demo's private github-repo/postgres part is NOT in the template.
3. **New integrations discovered**: `CommunityToolkit.Aspire.Hosting.Ollama` (AddOllama) and
   `Nextended.Aspire` (AddGithubRepository) referenced, force-loaded, and in the overlay package map
   so generated projects build.
4. **NuGet packages panel**: per stack, show which packages (id + version) the generated project uses
   and which resource causes each — `GET /stacks/{id}/packages`.
5. **Run diagnostics**: a Logs panel (dockview) showing the live run log; on `Failed`, errors are
   highlighted and a failure banner explains state. Status polling moves to shared editor state so
   toolbar + logs panel stay consistent.

## Non-Goals (this slice)

Import UI (Slice 5), settings/AI (Slice 6), child resources with own variables (`var db = pg.AddDatabase`),
publish-mode conditionals, `EnsureDockerRunningIfLocalDebug` trailer (generated stacks keep plain
`builder.Build().Run()`), everything in BACKLOG "Later".

## Canonical emit order (marker block)

1. Node declarations (`var x = builder.AddX(...);`) in node order
2. **Raw statements** (verbatim, in stored order)
3. WithCalls per node (includes child-adds like `ollama.AddModel(...)`)
4. Edges: `.WithReference(x)` / `.WaitFor(x)`

This lets raw expression vars reference declared nodes and be used by later WithCalls.

## Import rules (round-trip invariant grows)

Inside the marker block:
- Recognized: node declarations, `X.WithY(...)`, `X.WithReference(Y)` → edge(reference),
  `X.WaitFor(Y)` → edge(waitFor).
- Everything else (other local declarations, expression statements on unknown receivers, etc.)
  → `RawStatements` verbatim (source text), preserving order among raws.
- Invariant: `import(generate(m)) == m` including raw statements and waitFor edges.

## Template mechanism

Backend `TemplateService` with hardcoded templates (code, not JSON — templates are StackModel
factories):
- `GET /templates` → `[{ id, name, description }]`
- `POST /stacks/from-template/{id}` → creates + persists a new stack from the template, returns it.
- Template `local-ai-demo` mirrors the AiStack demo minus GPU + private repo parts. Must pass the
  syntax compile-check on creation (same Persist flow).

## Packages endpoint

`GET /stacks/{id}/packages` → `[{ id, version, resources: [resourceName...] }]`, computed from the
same overlay package map CodeGen uses (single source of truth) + the always-present
`Aspire.Hosting.AppHost`. UI: a "Packages" dockview panel with a tidy list (package, version, used-by
badges).

## Run diagnostics

- Editor status polling moves into EditorContext (single poller; toolbar + panels consume).
- New **Logs** dockview panel: shows `RunStatus.log` (auto-scrolls), polls while Starting/Running,
  highlights lines matching error patterns (`error`, `exception`, `fail`) in red; on `Failed` state a
  banner at top: "Run failed — see highlighted lines".
- Env-value expressions (unquoted args like `localAiOpenAiBase`) render in the env list as a
  read-only "expression" chip instead of an editable text field (editing would re-quote and corrupt).

## Error handling

- Template creation runs through Persist (syntax compile-check; 422 surfaces if a template is broken).
- Import of a statement that Roslyn can't parse at all: whole-file behavior unchanged (best effort);
  parseable-but-unrecognized statements become raws.
- Packages endpoint for unknown stack → 404.
- Raw statements are user-owned: no validation beyond the existing whole-file syntax check on save.

## Testing

Backend: emit-order test (decl → raw → with → edges); round-trip incl. raw + waitFor; template
endpoint creates a stack whose generated program passes CompileErrors and includes AddOllama/AddN8n/
AddLocalAI + the ReferenceExpression raw; packages endpoint returns Ollama/N8n/LocalAI packages with
correct versions; catalog discovers AddOllama + AddGithubRepository.
Frontend: vitest for log error-line classifier; build gate.

## Deferred

See BACKLOG.md (import, settings/AI, auth, deploy, …).
