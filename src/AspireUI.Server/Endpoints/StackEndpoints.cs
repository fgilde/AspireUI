using AspireUI.Server.Models;
using AspireUI.Server.Services;

namespace AspireUI.Server.Endpoints;

public static class StackEndpoints
{
    public static void MapStackEndpoints(this WebApplication app)
    {
        var store = new StackStore(Environment.GetEnvironmentVariable("DB_PATH") ?? "aspireui.db");
        var gen = new CodeGenService();
        var import = new ImportService();
        var export = new ExportService();
        var catalog = new CatalogService();
        var run = new RunService();
        var wsRoot = Environment.GetEnvironmentVariable("WORKSPACE_DIR") ?? "workspace";

        string Dir(string id) => Path.Combine(wsRoot, id);

        // Materialize + compile-check; returns error list (empty = ok).
        IResult Persist(StackModel s)
        {
            var errors = gen.CompileErrors(gen.GenerateProgram(s));
            if (errors.Count > 0) return Results.UnprocessableEntity(errors);
            store.Save(s);
            gen.Materialize(s, Dir(s.Id));
            return Results.Ok(s);
        }

        app.MapGet("/catalog", () => catalog.GetCatalog());
        app.MapGet("/stacks", () => store.List());
        app.MapGet("/stacks/{id}", (string id) =>
            store.Get(id) is { } s ? Results.Ok(s) : Results.NotFound());

        app.MapPost("/stacks", (StackModel body) =>
        {
            var s = body with { Id = Guid.NewGuid().ToString("n") };
            return Persist(s);
        });

        app.MapPut("/stacks/{id}", (string id, StackModel body) =>
            store.Get(id) is null ? Results.NotFound() : Persist(body with { Id = id }));

        app.MapDelete("/stacks/{id}", (string id) =>
        {
            store.Delete(id);
            if (Directory.Exists(Dir(id))) Directory.Delete(Dir(id), true);
            return Results.NoContent();
        });

        app.MapPatch("/stacks/{id}/nodes/{nodeId}", (string id, string nodeId, NodeModel patch) =>
        {
            if (store.Get(id) is not { } s) return Results.NotFound();
            var idx = s.Nodes.FindIndex(n => n.Id == nodeId);
            if (idx < 0) return Results.NotFound();
            s.Nodes[idx] = patch with { Id = nodeId };
            return Persist(s);
        });

        app.MapPost("/stacks/{id}/edges", (string id, EdgeModel edge) =>
        {
            if (store.Get(id) is not { } s) return Results.NotFound();
            s.Edges.Add(edge with { Id = "e" + Guid.NewGuid().ToString("n")[..8] });
            return Persist(s);
        });

        app.MapDelete("/stacks/{id}/edges/{edgeId}", (string id, string edgeId) =>
        {
            if (store.Get(id) is not { } s) return Results.NotFound();
            s.Edges.RemoveAll(e => e.Id == edgeId);
            return Persist(s);
        });

        app.MapGet("/stacks/{id}/export", (string id) =>
        {
            if (!Directory.Exists(Dir(id))) return Results.NotFound();
            return Results.File(export.Zip(Dir(id)), "application/zip", $"{id}.zip");
        });

        app.MapGet("/stacks/{id}/preview", (string id) =>
            store.Get(id) is { } s ? Results.Text(gen.GenerateProgram(s), "text/plain") : Results.NotFound());

        app.MapPost("/stacks/{id}/import", (string id, ImportRequest req) =>
        {
            var s = import.Import(id, req.Name, req.ProgramCs, req.SidecarJson ?? "");
            return Persist(s);
        });

        app.MapPost("/stacks/{id}/run", (string id) =>
            Directory.Exists(Dir(id)) ? Results.Ok(run.Start(id, Path.GetFullPath(Dir(id)))) : Results.NotFound());
        app.MapPost("/stacks/{id}/stop", (string id) => Results.Ok(run.Stop(id)));
        app.MapGet("/stacks/{id}/status", (string id) => Results.Ok(run.Status(id)));
    }

    public record ImportRequest(string Name, string ProgramCs, string? SidecarJson);
}
