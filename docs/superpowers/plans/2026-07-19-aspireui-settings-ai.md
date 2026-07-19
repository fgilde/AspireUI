# AspireUI Settings + Built-in AI Implementation Plan

> REQUIRED SUB-SKILL: superpowers:subagent-driven-development.

**Goal:** Global app settings (incl. AI provider config) + a built-in, provider-agnostic AI assistant that edits the stack model from a natural-language prompt.

## Global Constraints
- Tool projects net10.0. Provider-agnostic OpenAI-compatible chat (`POST {baseUrl}/v1/chat/completions`); no vendor SDK.
- Serialization/CodeGen unchanged; the assistant produces a full StackModel that goes through the existing `Persist` (syntax compile-check) path.
- Conventional Commits, NO Co-Authored-By, `git push` after every commit.

---

### Task 1: SettingsStore + endpoints

**Files:** `Models/StackModel.cs` (or new `Models/AppSettings.cs`), `Services/SettingsStore.cs` (new), `Endpoints/StackEndpoints.cs`; tests `SettingsTests.cs` (new).

- `record AppSettings(string? AiBaseUrl, string? AiApiKey, string? AiModel, string? AiProviderLabel);`
- `SettingsStore(string dbPath)`: SQLite table `settings(key,value)`; `AppSettings Get()`, `void Save(AppSettings)`. Store each field as a row (or one JSON row — pick one, keep simple). Reuse the `:memory:` keep-alive pattern from StackStore for tests.
- Endpoints: `GET /settings` → AppSettings with `AiApiKey` masked to `"***"` when a key is stored, `null`/empty when not. `PUT /settings` (body AppSettings): if incoming `AiApiKey == "***"` keep the stored key; if empty/null clear it; else store it. Register a `SettingsStore` in MapStackEndpoints using the same dataDir as StackStore.
- Tests: round-trip; GET masks; PUT `"***"` keeps; PUT empty clears. TDD.
- Commit `feat: app settings store and endpoints` + push.

---

### Task 2: AssistService + /assist endpoint (injectable chat client)

**Files:** `Services/AssistService.cs` (new), `Services/IChatClient.cs` (new), `Endpoints/StackEndpoints.cs`; tests `AssistTests.cs` (new).

- `interface IChatClient { Task<string> CompleteAsync(string system, string user, AppSettings s); }` — default impl `HttpChatClient` POSTs `{baseUrl}/v1/chat/completions` with `{model, messages:[{role:system},{role:user}], response_format:{type:"json_object"}}`, bearer `AiApiKey`, returns `choices[0].message.content`. Uses `HttpClient` (register one).
- `AssistService(IChatClient chat, CatalogService catalog)`: `Task<AssistResult> AssistAsync(StackModel stack, string prompt, AppSettings settings)`:
  - Build system prompt: describe the task; embed a COMPACT catalog summary (addMethod + label + group + addParam names per resource — cap size); embed current stack JSON; instruct: return ONLY JSON `{"reply": string, "stack": <StackModel>}`, preserve node ids where unchanged, use only addMethods from the catalog.
  - Call `chat.CompleteAsync`; parse the JSON to `{reply, stack}`. If parse fails → `AssistResult` flagged invalid with the raw text as reply.
  - Return the parsed stack (endpoint persists it) + reply.
- `record AssistResult(string Reply, StackModel? Stack, bool Ok);`
- Endpoint `POST /stacks/{id}/assist` `{prompt}`:
  - 404 if stack unknown. 400 if `settings.AiBaseUrl` empty ("AI not configured").
  - Call AssistService with the stored settings. On `!Ok` → 422 `{reply}`. On Ok → run the returned stack (with Id forced to the path id) through `Persist`; if Persist 422s, return that with the reply; else return `{reply, stack}`.
  - Wrap upstream/network errors → 502 with a short message.
- Tests (fake IChatClient):
  - Fake returns `{"reply":"added","stack":<stack with an extra AddRedis node>}` → endpoint applies it (GET stack shows the node), returns reply.
  - Fake returns malformed JSON → 422, stack unchanged.
  - Fake returns a stack whose generated code is invalid (e.g. bogus so CompileErrors non-empty — hard to force via syntax; instead test the parse-fail path and the no-settings 400).
  - `/assist` with no AI configured → 400.
- Commit `feat: built-in AI assist endpoint (provider-agnostic)` + push.

Note: keep the catalog summary small (don't dump all 50 withs — addMethods + labels + addParams is enough for the model to choose; it can use raw withCalls). Document the token-size ceiling with a `ponytail:` comment.

---

### Task 3: Frontend settings page

**Files:** `web/src/api.ts`, `web/src/model.ts` (AppSettings type + pure mapper if useful), `web/src/pages/Settings.tsx` (new), `web/src/App.tsx` (route), `web/src/pages/StacksOverview.tsx` (a Settings link), `web/src/model.test.ts`.

- model.ts: `interface AppSettings { aiBaseUrl?: string|null; aiApiKey?: string|null; aiModel?: string|null; aiProviderLabel?: string|null }`.
- api.ts: `getSettings()`, `saveSettings(s)`.
- Route `/settings` → Settings page: Mantine form (TextInput baseUrl/model/label, PasswordInput apiKey with placeholder showing it's set), Save button → saveSettings → toast/inline confirm. A back link to `/`. Overview header gets a Settings (gear) button navigating to `/settings`.
- Gate: `npm run build` clean + `npm test`. Commit `feat: settings page` + push.

---

### Task 4: Frontend AssistPanel

**Files:** `web/src/api.ts` (`assist`), `web/src/editor/AssistPanel.tsx` (new), `web/src/editor/DockLayout.tsx` (register panel + default layout).

- api.ts: `assistStack(id, prompt): Promise<{reply, stack}>`.
- `AssistPanel` (consumes EditorContext stack/setStack): a Textarea prompt + Send button (loading state); on send → `assistStack(stack.id, prompt)`; on success set the returned stack (updates canvas) + show the reply; on error show the error/reply text. A short hint if AI isn't configured (link to /settings).
- Register as a dockview panel in the bottom group (Preview | Packages | Logs | Assistant); bump layout key to `aspireui.layout.v3`.
- Gate: `npm run build` clean + `npm test`. Commit `feat: AI assistant panel` + push.

---

### Task 5: E2E verify
- `dotnet test` + frontend build/test green.
- Live: PUT /settings with a dummy baseUrl; GET shows masked key. Assist without config → 400. (If a real OpenAI-compatible endpoint is available in the env — e.g. a local one — do a real assist; otherwise document that the live LLM call is untested and rely on the fake-client unit tests, which prove the apply pipeline.) Confirm the app builds and serves; settings page + assistant panel render.
- Fix issues in small pushed commits; report.

## Self-Review
- Coverage: settings store+endpoints+masking (T1), assist service+endpoint+injectable client (T2), settings UI (T3), assistant panel (T4), verify (T5). ✔
- Provider-agnostic: OpenAI-compatible HTTP, injectable for tests. ✔
- Consistency: AppSettings C# ↔ TS camelCase; assist returns {reply, stack}; stack goes through existing Persist. ✔
- Risk: live LLM call is environmental (needs a configured endpoint); unit tests use a fake client to prove the apply pipeline deterministically.
