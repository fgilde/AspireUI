# AspireUI Demo Templates + Model Power Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development, task-by-task.

**Goal:** Raw statements + waitFor edges in the model; Ollama/Nextended.Aspire integrations; a "Create from demo" Local-AI template that runs; a NuGet packages panel; a run logs/error panel.

**Tech:** unchanged stack. Reference demo: `C:\dev\privat\github\Nextended\Tests\TestProjects\AiStack.AppHost\Program.cs`.

## Global Constraints
- Tool + generated projects net10.0; Aspire 13.4.6; Nextended 10.1.14.
- Canonical marker-block emit order: **declarations → raw statements → withCalls → edges**.
- Round-trip invariant `import(generate(m)) == m` must hold INCLUDING RawStatements and waitFor edges.
- Conventional Commits, NO Co-Authored-By footer, `git push` after every commit.
- Serialization stays positional/raw-literal for nodes; RawStatements are verbatim strings.

---

### Task 1: Model + CodeGen + Import (raw statements, waitFor edges)

**Files:** `Models/StackModel.cs`, `Services/CodeGenService.cs`, `Services/ImportService.cs`; tests `CodeGenTests.cs`, `ImportTests.cs`.

**Interfaces:**
- `StackModel` gains `List<string> RawStatements` as LAST positional param. Update ALL existing `new StackModel(...)` constructions (tests + `StackEndpoints` in-place `with` expressions are unaffected; ApiTests fixtures need a trailing `[]`).
- `EdgeModel.Kind` now also `"waitFor"` (string field already exists — no model change).
- CodeGen emit inside markers, in order: (1) all node declarations, (2) `foreach (var raw in s.RawStatements) sb.AppendLine(raw);`, (3) withCalls per node, (4) edges: `Kind == "reference"` → `.WithReference(v)`, `Kind == "waitFor"` → `.WaitFor(v)`.
- Import: inside markers — `X.WaitFor(Y)` with known vars → edge kind `waitFor`; ANY statement not recognized as node-decl / withcall / reference / waitFor → append its **exact source text** (`st.ToString()` trimmed) to `RawStatements`, preserving order.

- [ ] Step 1: failing tests — extend `CodeGenTests` fixture with `RawStatements = ["var extra = ReferenceExpression.Create($\"{db.Resource}\");"]` and a waitFor edge; assert emit order (decl index < raw index < with index < edge index) and `.WaitFor(` emitted. Extend `ImportTests` round-trip fixture with a raw statement + waitFor edge; extend node/edge keys to include them; assert raws survive verbatim.
- [ ] Step 2: run — FAIL. Step 3: implement. Step 4: `dotnet test` all green (fix all StackModel construction sites).
- [ ] Step 5: commit `feat: raw statements and waitFor edges in model` + push.

---

### Task 2: Ollama + Nextended.Aspire packages + overlay

**Files:** `AspireUI.Server.csproj`, `Services/CatalogService.cs` (force-load), `catalog/aspire-hosting.json`, `CatalogTests.cs`.

- [ ] Add packages: `CommunityToolkit.Aspire.Hosting.Ollama` (13.4.0 or nearest resolvable) and `Nextended.Aspire` (10.1.14). Reality-check versions; document.
- [ ] Force-load both in `LoadDefault()` (Assembly.Load fallback pattern already there). Extend the assembly-name filter to include `CommunityToolkit.Aspire` prefix.
- [ ] Overlay: add `"AddOllama": { label "Ollama", group "AI", package "CommunityToolkit.Aspire.Hosting.Ollama", packageVersion "<used version>" }`, `"AddGithubRepository": { label "GitHub Repo", group "Source", package "Nextended.Aspire", packageVersion "10.1.14" }`, and groups/labels for `AddN8n`/`AddSupabase`/`AddLocalAI` (labels n8n/Supabase/LocalAI, group "AI"/"Backend") with their existing package entries.
- [ ] Test: catalog contains `AddOllama` and `AddGithubRepository`. Verify the actual AddMethod names via a catalog dump; adjust overlay keys to the REAL names (e.g. if it's `AddGitProject` — use what reflection finds; document).
- [ ] `dotnet test` green. Commit `feat: Ollama and Nextended.Aspire integrations` + push.

---

### Task 3: TemplateService + endpoints + Local AI demo template

**Files:** `Services/TemplateService.cs` (new), `Endpoints/StackEndpoints.cs`, `TemplateTests.cs` (new).

**Interfaces:**
- `record TemplateInfo(string Id, string Name, string Description);`
- `TemplateService.List(): IReadOnlyList<TemplateInfo>`, `TemplateService.Create(string templateId): StackModel?` (fresh ids, name "Local AI Demo").
- Routes: `GET /templates` → list; `POST /stacks/from-template/{templateId}` → `Create` + existing `Persist` flow (404 unknown template).

Template `local-ai-demo` (mirrors the AiStack demo, CPU-safe, no private repo part):
```csharp
// nodes (varName/addMethod/resourceName/addArgs/withCalls):
// ollama = AddOllama("ollama"); withCalls: WithDataVolume(); AddModel("llama3.2"); AddModel("nomic-embed-text")
//   -> WithCalls: [ {WithDataVolume,[]}, {AddModel,["\"llama3.2\""]}, {AddModel,["\"nomic-embed-text\""]} ]
// localai = AddLocalAI("localai"); withCalls: WithDataVolume(); AddModel(KnownTextModel.Qwen3_8b);
//   AddModel(KnownEmbeddingModel.BertEmbeddings); WithOpenWebUI()
// n8n = AddN8n("n8n"); withCalls: WithTimezone("Europe/Berlin");
//   WithEnvironment("OPENAI_API_BASE_URL", localAiOpenAiBase); WithEnvironment("OPENAI_BASE_URL", localAiOpenAiBase);
//   WithEnvironment("OPENAI_API_KEY", "sk-local"); WithEnvironment("OLLAMA_BASE_URL", ollama.Resource.PrimaryEndpoint)
// rawStatements: [ "var localAiOpenAiBase = ReferenceExpression.Create($\"{localai.Resource.HttpEndpoint}/v1\");" ]
// edges: n8n->ollama waitFor, n8n->localai waitFor
```
Positions spread out (ollama 80/80, localai 80/300, n8n 480/190).

- [ ] Failing test: `POST /stacks/from-template/local-ai-demo` (or service-level) → stack has ≥3 nodes, generated program contains `AddOllama`, `AddN8n`, `AddLocalAI`, `ReferenceExpression.Create`, `.WaitFor(`, and `CompileErrors(...)` is empty. `GET /templates` returns local-ai-demo.
- [ ] Implement; `dotnet test` green. Commit `feat: local AI demo template` + push.

---

### Task 4: Packages endpoint

**Files:** `Endpoints/StackEndpoints.cs` (route), `Services/CodeGenService.cs` (expose used-packages computation), `ApiTests.cs`.

- `GET /stacks/{id}/packages` → `[{ id, version, resources: [resourceName...] }]`: always `Aspire.Hosting.AppHost@AspireVersion` (resources: []), plus per overlay-map entry used by the stack's nodes, grouping resourceNames. Reuse the SAME overlay map (CatalogService.ResourcePackages — extend it to also return version, or add a parallel accessor; keep single source).
- [ ] Failing test: stack with a Redis + N8n node → packages contain Aspire.Hosting.Redis@13.4.6 (resources ["cache"]) and Nextended.Aspire.Hosting.N8n@10.1.14. Implement. Green. Commit `feat: stack packages endpoint` + push.

---

### Task 5: Frontend — demo dropdown, waitFor edges, expression chips

**Files:** `web/src/api.ts`, `web/src/model.ts` (Stack.rawStatements type), `web/src/pages/StacksOverview.tsx`, `web/src/editor/Canvas.tsx`, `web/src/editor/PropertyGrid.tsx`, `web/src/editor/PropertyPanel.tsx`.

- [ ] `model.ts`: `Stack` gains `rawStatements: string[]`. api: `getTemplates()`, `createFromTemplate(id)`, include in new-stack creation payloads (`rawStatements: []` everywhere a Stack is built client-side).
- [ ] Overview: split/dropdown button — primary "New Stack" + `Menu` "From demo…" listing `GET /templates`; picking one calls `createFromTemplate` then navigates to the editor.
- [ ] Canvas: waitFor edges dashed (`style: { strokeDasharray: "6 3" }`, label "waits for"); reference edges solid. New connections stay kind "reference"; PropertyPanel References tab unchanged. Add a small kind toggle when an edge is selected? NO — YAGNI this slice; waitFor edges come from templates/import for now (note in report).
- [ ] PropertyGrid env list: if a value literal does NOT start with `"` → render a read-only Badge "expression" + the raw text (no TextInput), preventing re-quote corruption. Same for non-env with-rows via the existing raw row (already text).
- [ ] Gates: `npm run build` clean, `npm test` green. Commit `feat: demo templates dropdown, waitFor edges, expression chips` + push.

---

### Task 6: Frontend — Packages panel + Logs/error panel

**Files:** `web/src/editor/DockLayout.tsx` (2 new panels), `web/src/editor/PackagesPanel.tsx` (new), `web/src/editor/LogsPanel.tsx` (new), `web/src/editor/RunToolbar.tsx` (consume shared status), `web/src/pages/Editor.tsx` (status polling in context), `web/src/api.ts` (`getPackages`), `web/src/model.ts` (log classifier), `web/src/model.test.ts`.

- [ ] Move run-status polling into Editor state/context: `runStatus` + poller (2s while Starting/Running, 5s otherwise); RunToolbar consumes context instead of own poller.
- [ ] `LogsPanel`: renders `runStatus.log` in monospace, auto-scroll to bottom, error lines highlighted red. Pure classifier in model.ts: `export function isErrorLine(line: string): boolean` (case-insensitive match on `error|exception|fail`), with 2-3 vitest cases. On `state === "Failed"`: red banner "Run failed — check highlighted lines below".
- [ ] `PackagesPanel`: fetch `getPackages(stack.id)` on stack content change; list rows: package id (mono), version Badge, used-by resource Badges.
- [ ] Register both as dockview panels; add to DEFAULT layout as tabs next to the preview panel (bottom group: Preview | Packages | Logs). Bump the localStorage layout key (e.g. `aspireui.layout.v2`) so existing saved layouts pick up the new panels.
- [ ] Gates: build clean + vitest green. Commit `feat: packages and logs panels` + push.

---

### Task 7: E2E verify

- [ ] `dotnet test` + `npm run build`/`npm test` green.
- [ ] Live Release run: `GET /templates` lists demo; create from template via curl → preview shows ollama/localai/n8n + raw ReferenceExpression + `.WaitFor(`; `GET /packages` shows Ollama toolkit + Nextended packages; run the demo stack → expect Starting → (long image pulls; bounded wait ~3-4 min) — accept either Running w/ dashboard URL or document how far it got + the log tail via the status endpoint (Docker + multi-GB pulls may exceed the window; that's environmental, not a defect — report honestly). Stop. Also verify a deliberately broken stack (raw statement `var x = ;`) returns 422 on save.
- [ ] Fix real issues in small pushed commits; report results.

---

## Self-Review
- Spec coverage: raw statements+waitFor (T1), packages/integrations (T2), template+button (T3/T5), packages panel (T4/T6), logs/error panel (T6), runnable demo verify (T7). ✔
- Round-trip: T1 extends the invariant test to raws+waitFor — the load-bearing check. ✔
- Consistency: `Stack.rawStatements` camelCase mirrors `StackModel.RawStatements`; `isErrorLine` defined T6 and tested; template ids `local-ai-demo` consistent across T3/T5. ✔
- Risk: template compile depends on enum short names (`KnownTextModel.Qwen3_8b`) resolving via implicit usings at *generated-build* time — same accepted risk as slice 3; the syntax check passes regardless. Emit-order change (raws before withs) alters existing generated files' layout — harmless, regenerated on next save.
