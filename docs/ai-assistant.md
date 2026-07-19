# AI Assistant

AspireUI has a built-in assistant that edits your stack for you: describe what you want ("add
Coolify", "wire n8n to a new postgres", "add a Redis cache and reference it from the api") and it
applies the change directly to the canvas.

## Configure a provider

Open **Settings** and fill in:

- **Base URL** — an OpenAI-compatible endpoint, e.g. your own LocalAI or Ollama instance, OpenAI
  itself, or any other service exposing `POST {baseUrl}/v1/chat/completions`.
- **API key** — sent as a bearer token. Once saved it's never echoed back to the browser (the field
  shows as masked); leave it as-is to keep the stored key, or clear it to remove it.
- **Model** — the model name to request from that endpoint.
- **Provider label** — a friendly name shown in the UI.

AspireUI deliberately doesn't hardcode a single AI vendor — any OpenAI-compatible endpoint works,
so you can point it at a local model instead of a cloud provider. Settings are stored server-side
(SQLite), separate from your stacks, so they apply across all of them.

Until a Base URL is configured, the assistant tells you to set it up in Settings rather than doing
anything.

## Using the assistant

In the editor, open the **Assistant** panel, type a request, and send it. AspireUI sends the
current stack plus a compact summary of the catalog (available resource types and their parameters)
to the configured model and asks for an updated stack back. On success, the change is applied to the
canvas and code preview immediately; the assistant's reply text (plus any error) shows in the panel.

A few things to know:

- Each request is **stateless** — there's no multi-turn memory, so include whatever context the
  model needs in the prompt itself.
- Replies are **not streamed** — you'll see a spinner until the full response comes back.
- If the model returns something that doesn't compile or doesn't parse as a valid stack, AspireUI
  rejects it (same syntax check as saving by hand) and shows you why in the reply instead of
  silently corrupting your stack.
- Because the assistant picks from the same catalog the palette uses, it can add anything already
  available there — containers, databases, GitHub repositories, Ollama models, and so on — without
  any extra wiring on your part.
