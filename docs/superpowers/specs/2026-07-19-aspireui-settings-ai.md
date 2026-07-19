# AspireUI — Global Settings + Built-in AI Assistant (Design)

**Date:** 2026-07-19 (Slice 6)
**Builds on:** import slice. Last of the currently-enumerated backlog items.

## Provider decision

The built-in AI is **provider-agnostic**: it calls an OpenAI-compatible `POST {baseUrl}/v1/chat/completions`
endpoint configured in settings. This works with the user's own LocalAI (which serves `/v1`), Ollama,
OpenAI, and Anthropic's OpenAI-compat endpoint. We deliberately do NOT hardcode a single vendor SDK —
the provider is a user setting. (The Anthropic `claude-api` skill was consulted and set aside for this
reason: it produces vendor-specific SDK code, wrong for a configurable-provider tool.)

## Goals

1. **Global app settings** stored app-level (SQLite, NOT per stack): a small key-value store, surfaced
   in a Settings page. First-class fields: AI `baseUrl`, `apiKey`, `model`, `providerLabel`.
2. **Built-in AI assistant**: in the editor, the user types a request ("add Coolify", "wire n8n to a
   new postgres", "build a stack from https://github.com/x/y"); the assistant calls the configured LLM
   with the catalog + current stack as context and applies the returned changes to the stack.

## Non-Goals (this slice)

- Streaming the assistant reply (single request/response; a spinner suffices).
- Multi-turn assistant memory (each request is stateless: current stack + prompt).
- Auth/users, deploy, reverse-proxy, wizard (later backlog).
- Secret encryption at rest (apiKey stored plain in the local SQLite; single-user local tool — documented ceiling).

## Architecture

```
Backend (net10.0)
  SettingsStore     SQLite key-value (settings table); separate from StackStore
  AssistService     builds prompt (catalog + stack + JSON-schema instruction), calls the configured
                    OpenAI-compatible chat endpoint via an injectable IChatClient, parses the returned
                    StackModel, returns it. IChatClient default = HttpClient POST /v1/chat/completions.
  endpoints         GET/PUT /settings ; POST /stacks/{id}/assist
Frontend (React + Mantine)
  /settings route   form for AI baseUrl/apiKey/model/label + save
  AssistPanel       dockview panel: prompt box + Send + reply text; applies returned stack live
```

## Settings

```
record AppSettings(string? AiBaseUrl, string? AiApiKey, string? AiModel, string? AiProviderLabel);
```
- `SettingsStore` (SQLite table `settings(key TEXT PRIMARY KEY, value TEXT)`), stored in the same
  LocalApplicationData/AspireUI dir as the stacks DB. `Get(): AppSettings`, `Save(AppSettings)`.
- `GET /settings` → AppSettings (apiKey returned masked as `"***"` if set, empty if not — never echo the
  real key to the browser after it's saved). `PUT /settings` → save; a `"***"` apiKey means "keep
  existing" (don't overwrite with the mask).

## Assistant

`POST /stacks/{id}/assist` body `{ prompt: string }`:
1. Load the stack + the catalog (resource types with addMethods/labels/params — a compact summary).
2. Build messages: a system message describing the tool ("You edit an Aspire stack model. Here are the
   available resource types: … Current stack JSON: … Return ONLY a JSON object matching this schema:
   {reply: string, stack: <StackModel>}. Preserve node ids where unchanged.") + the user prompt.
3. Call the configured chat endpoint (`AiBaseUrl` + `/v1/chat/completions`, model `AiModel`, bearer
   `AiApiKey`) via `IChatClient.Complete(messages)`. Request JSON response format if supported
   (`response_format: {type: "json_object"}`).
4. Parse the returned content as `{ reply, stack }`. Validate the stack: run it through the existing
   `Persist` path (syntax compile-check → 422 if the AI produced invalid code → surfaced as the reply so
   the user sees why). On success, save + materialize; return `{ reply, stack }`.
5. If settings have no `AiBaseUrl`, return 400 "AI not configured — set it in Settings".

`IChatClient` is injectable (like RunService's command factory) so tests use a fake returning a canned
JSON stack instead of a live LLM.

The assistant's power to add git repos / arbitrary containers comes for free: the catalog already
exposes `AddGithubRepository`, `AddContainer`, `AddOllama`, the Nextended resources, etc. — the model
picks from them and emits nodes/addArgs/withCalls, which the existing CodeGen turns into C#.

## Error handling

- AI endpoint unreachable / non-2xx → assist returns 502 with the upstream status + a short message.
- AI returns non-JSON or a malformed stack → assist returns 422 with the raw reply so the user can retry
  or rephrase (don't crash; don't apply a partial stack).
- AI-produced stack fails the syntax compile-check → 422 with the diagnostics as the reply; stack not saved.
- Prompt too long / model context exceeded → surfaced from the upstream error.
- apiKey mask handling: a PUT with `apiKey == "***"` keeps the stored key; empty string clears it.

## Testing

Backend (xUnit):
- SettingsStore round-trips AppSettings; GET masks the apiKey; PUT with `"***"` keeps the old key.
- AssistService with a fake IChatClient returning `{reply, stack}` → applies the stack (new node
  present), returns the reply; a fake returning invalid stack C# → 422, stack unchanged.
- `/stacks/{id}/assist` with no AI configured → 400.

Frontend (Vitest + build gate):
- Settings form maps to/from AppSettings (pure mapper).
- Build gate for the Settings page + AssistPanel.

## Deferred (BACKLOG)

Streaming replies, multi-turn assistant, secret encryption, auth/wizard, deploy, reverse-proxy — see BACKLOG.md.
