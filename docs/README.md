<img width="2547" height="1261" alt="image" src="https://github.com/user-attachments/assets/54d51236-c6c0-4cf7-a4a3-ae5b814a879a" />

# AspireUI

AspireUI is a visual canvas for [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/) AppHost
projects. Drag out resources from a reflection-driven catalog, wire up references, tweak properties
in a typed grid, and watch the generated C# update live — then run the stack and jump straight into
the Aspire dashboard. Import an existing AppHost (`.cs` / `.csproj` / `.zip`) to start from what you
already have, spin up a demo template to explore, or ask the built-in AI assistant to build it for you.

This site covers how to use the tool. For what's implemented and what's deferred, see the
[repo README](https://github.com/fgilde/AspireUI#readme) and [BACKLOG](https://github.com/fgilde/AspireUI/blob/master/BACKLOG.md).

## Where to start

- **[Getting Started](getting-started.md)** — run AspireUI locally, create your first stack.
- **[Building Stacks](building-stacks.md)** — the palette, add-resource dialog, property grid,
  references, and the live code preview.
- **[Importing](importing.md)** — bring in an existing AppHost from `.cs`, `.csproj`, or a `.zip`.
- **[AI Assistant](ai-assistant.md)** — configure a provider and let the assistant edit your stack.
- **[Running & Deploying](running-and-deploying.md)** — run a stack locally and self-host AspireUI
  with Docker.

## Notes / limitations

AspireUI is a single-user, local-first tool — there is no authentication yet, so don't expose it
directly to the internet. Running a stack shells out to `dotnet run` and Aspire resources commonly
start containers, so the .NET SDK and Docker both need to be available wherever AspireUI runs.
