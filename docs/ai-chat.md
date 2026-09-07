# The assistant

With an AI backend configured under **Settings → AI & Agents**, a button appears in the bottom right
corner of every page. It opens a chat that can answer questions about this instance **and operate
it** — install an app, deploy a stack, stop one, fetch the logs of the one that is unhealthy.

## What it can do is what you can do

The chat runs exactly the tools the [MCP server](../README.md#api--mcp-agents) exposes, and every one
of them checks a [permission](users-and-permissions.md) before it does anything:

| Tool | Needs |
| --- | --- |
| `list_stacks`, `get_stack`, `create_stack`, `add_resource`, `delete_stack`, `run_stack`, `stop_run` | Builder |
| `search_apps`, `install_app`, `deploy_to_hosting`, `start_hosting`, `stop_hosting` | Install & run apps |
| `list_hosting`, `hosting_logs` | Browse app files |

Two things follow from that. A tool you may not use is **not offered to the model at all**, so it
does not try it and does not have to explain itself. And a token handed to an outside agent can never
do more than the person it belongs to — the same rule, one implementation.

Every answer lists the tools it called; click one to see the arguments and what came back. Nothing is
hidden behind a summary.

## Sessions

Conversations are kept per account — a chat that can act on the system is not shared — and the first
question becomes the title. The picker at the top of the panel switches between them, **New** starts
a fresh one, and the trash icon removes one for good. The panel reopens where you left off.

## What it will not do

- It only acts on what you asked for in the conversation. It is told not to deploy, stop or delete
  anything on its own initiative, and not to do more than was asked.
- It stops after eight tool rounds and says so rather than looping.
- A refused or failed tool call goes back to the model as text, so it can tell you *why* — usually
  that you do not have that permission.

## With a local CLI backend

The CLI backends (`claude`, `gemini`, `ollama`, `llm`, `codex`) take a prompt and give back text;
there is nowhere in that to put a function call. So with one of those configured the chat answers
questions and tells you where to click, and the panel says it has no tools. Everything that operates
the instance needs an **OpenAI-compatible HTTP endpoint**, which every local server (Ollama, LocalAI,
llama.cpp, vLLM) also speaks.

## Configuring it from the AppHost

The backend does not have to be filled in by hand. From an Aspire AppHost,
[`Nextended.Aspire.Hosting.AspireUI`](seeding.md#from-aspire) points AspireUI at a model server that
lives in the same stack:

```csharp
var ollama = builder.AddOllama("ollama").WithDataVolume();

builder.AddAspireUI()
    .WithOllamaAssistant(ollama, "llama3.2");
```

AspireUI waits for that resource and talks to it over the container network, so the url does not
have to be known in advance. `WithAssistant(endpoint, model, apiKey)` takes a plain url instead, with
the key coming from an Aspire parameter so it stays out of the manifest.

## The editor's own assistant

Inside the stack editor the docked **Assistant** panel stays what it was: it rewrites the stack on
the canvas (or its code) rather than calling tools. The floating button is hidden there — two
assistants in one screen is one too many.
