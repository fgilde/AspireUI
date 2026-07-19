# Getting Started

## Run it

AspireUI is an ASP.NET Core server (net10.0) that serves the React SPA. For development:

```bash
dotnet run --project src/AspireUI.Server
```

Opens at **http://localhost:5158**.

To self-host on a server instead, see [Running & Deploying](running-and-deploying.md).

## Your first stack

1. Open AspireUI — the **Stacks overview** lists your existing stacks (create / open / delete /
   run-status badge).
2. Click **New Stack** to start blank, or use **From demo** to create a runnable example (see
   below) — either way you land in the editor.
3. In the editor, drag a resource from the **Palette** onto the canvas, or import an existing
   AppHost (see [Importing](importing.md)).
4. Select a node to edit it in the **Properties** panel, wire up references between nodes, and
   watch the **Code preview** panel update with the generated `Program.cs`.
5. Hit **Run** to start the stack and open the Aspire dashboard.

## Demo templates

The overview's **Create from demo** dropdown creates a ready-to-run stack without building one by
hand. The bundled **Local AI Demo** wires up Ollama (with a couple of models pulled via `AddModel`),
LocalAI, and n8n (waiting on both), CPU-safe out of the box — add `WithGPUSupport` yourself via the
property grid if you have a GPU.

## Layout

The workspace — Palette, Canvas, Properties, Code preview, Packages, Logs — is built from dockable
panels: split, tab, float, and drag them however you like. The arrangement is remembered between
sessions (stored in the browser); use **Reset layout** to go back to the default if it ever gets
into a bad state.
