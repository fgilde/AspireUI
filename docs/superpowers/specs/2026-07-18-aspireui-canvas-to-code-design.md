# AspireUI — Canvas-to-Code Core (Design)

**Date:** 2026-07-18
**Slice:** Kern von AspireUI. Visueller Editor + Ressourcen-Katalog + bidirektionaler
Code-Generator. Auth, First-Run-Wizard, Run-Orchestrierung, Deploy und Reverse-Proxy
sind **eigene, spätere Spec-Zyklen** und hier bewusst ausgeklammert.

## Ziel

Ein Aspire-`AppHost`-Projekt visuell als Graph bauen und bearbeiten: Nodes = Ressourcen
(Container, Projekte, Datenbanken, …), Edges = Referenzen (`WithReference`). Bestehende
Projekte importieren. Als ZIP exportieren. Ein Stack in der UI = ein AppHost-Projekt auf
Disk.

## Nicht-Ziele (dieser Slice)

- Kein Auth / User-Verwaltung
- Kein Wizard / Dependency-Check
- Kein `dotnet run` / Live-Status / Dashboard
- Kein Deploy / docker compose / Reverse-Proxy
- Kein Live-File-Watch (Speichern ist ein synchroner Request)

## Architektur

```
React SPA (@xyflow/react)          ASP.NET Core Backend
  Canvas (Nodes/Edges)   ── REST ──   CatalogService  (Reflection + JSON-Overlays)
  Inspector (Env/WithX)               StackStore      (SQLite)
  Palette (Katalog)                   CodeGenService  (Roslyn: Modell → C#)
                                      ImportService   (Roslyn: C# → Modell)
                                      ExportService   (ZIP)
                                            │
                                      Dateisystem: workspace/<stack>/
                                        (echtes AppHost-Projekt + aspireui.json Sidecar)
```

Backend erzwungen .NET: Roslyn (C# parsen/generieren) und Reflection über
Aspire-Assemblies gibt es nur dort. ASP.NET served das gebaute React-SPA als **eine**
Deployable. Storage = SQLite (eine Datei, kein DB-Server, trivial auf Proxmox).

### Komponenten-Grenzen

| Komponente | Aufgabe | Abhängigkeiten |
|---|---|---|
| CatalogService | kennt Ressourcen-*Typen* + Config-Schema | Aspire-DLLs, `catalog/*.json` |
| StackStore | kennt *Instanzen* (Nodes/Edges/Config) | SQLite |
| CodeGenService | Modell → kanonische `Program.cs` + `.csproj` | Roslyn |
| ImportService | `Program.cs` → Modell (Marker-Block) | Roslyn |
| ExportService | Projektordner → ZIP | — |

## Datenmodell (SQLite + Sidecar)

```
Stack { id, name, targetFramework }
Node  { id, stackId, resourceType, varName, config:json, x, y }
Edge  { id, stackId, fromNodeId, toNodeId, kind }   // kind = "reference"
```

Positionen (x/y) doppelt: in SQLite fürs schnelle Laden, gespiegelt in
`workspace/<stack>/aspireui.json`, damit Git/Export sie mitnehmen.

## Round-Trip: Marker-Block

CodeGen erzeugt eine **kanonische** Form. Nur der Marker-Block gehört dem Tool:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// >>> aspireui:begin (nicht von Hand editieren)
var db = builder.AddPostgres("db").WithDataVolume();
var api = builder.AddProject<Projects.Api>("api")
    .WithReference(db);          // aus Edge db->api
// <<< aspireui:end

builder.Build().Run();
```

- **Generieren:** deterministisch, nur der Block wird neu geschrieben. Code außerhalb
  (eigene Helper, `using`s) bleibt unberührt.
- **Importieren:** Roslyn parst `builder.AddX("name")....WithReference(y)`-Ketten
  **innerhalb** der Marker → Nodes/Edges. Code außerhalb der Marker oder unbekannte
  Muster → als **read-only Fremd-Node** auf den Canvas gepinnt (sichtbar, nicht
  editierbar). Nichts wird still verschluckt.

`ponytail:` Marker-Block-Regeneration statt chirurgischer Roslyn-Rewrites. Upgrade auf
feinere In-Place-Edits nur, falls User den Marker-Ansatz ablehnt.

## Katalog (Hybrid)

1. **Reflection:** referenzierte Aspire-Pakete laden; `public static IResourceBuilder<T>
   AddX(this IDistributedApplicationBuilder, …)` und `WithX(this IResourceBuilder<T>, …)`
   finden. Signatur → generisches Feld-Schema.
2. **Overlay:** `catalog/*.json` pro Kern-Paket (Aspire.Hosting, Nextended.*,
   CommunityToolkit.Aspire.*): Label, Icon, Gruppe, sichtbare WithX,
   Param-Hints/Defaults/Validierung.
3. **Merge:** Overlay gewinnt fürs UI, Reflection füllt den Rest. Ohne Overlay =
   generisches Formular. Unbekannte Pakete funktionieren generisch, Kern-Pakete sehen
   poliert aus.

## Datenfluss (Node-Config ändern)

```
Inspector-Edit → PATCH /stacks/{id}/nodes/{n} → StackStore(SQLite)
              → CodeGen regeneriert Marker-Block → Program.cs neu geschrieben
              → aspireui.json (Positionen) neu geschrieben
```

Speichern = ein synchroner Schritt; Disk + DB immer konsistent.

## REST-API

```
GET  /catalog                    Ressourcen-Typen + Schema
GET  /stacks                     Liste
POST /stacks                     neu (legt workspace/<stack>/ an)
GET  /stacks/{id}                Modell (Nodes/Edges)
DELETE /stacks/{id}              löschen
PATCH /stacks/{id}/nodes/{n}     Node-config
POST /stacks/{id}/edges          Referenz setzen
DELETE /stacks/{id}/edges/{e}    Referenz lösen
GET  /stacks/{id}/export         ZIP
POST /stacks/{id}/import         C#-Ordner → Modell
```

## Fehlerbehandlung

- Import trifft nicht-kompilierbaren/kaputten C#: Fremd-Node mit Fehlermarkierung,
  Stack lädt trotzdem.
- CodeGen-Ergebnis kompiliert nicht: Response 422 + Roslyn-Diagnostics, Disk-Schreiben
  wird verworfen (kein halb-geschriebenes Projekt).
- Unbekannter Ressourcen-Typ in Config: generisches Formular, keine Validierung, Warnung.

## Testing (je ein scharfer Check)

- **CodeGen kompiliert:** Modell → C#, Roslyn-Compile-Assert grün.
- **Round-Trip-Invariante:** `import(generate(model)) == model` für einen Fixture-Stack.
  Der tragende Test — bricht er, ist der Marker-Ansatz kaputt.
- **Katalog:** Reflection findet `AddPostgres` im Aspire.Hosting-Fixture.

## Offene Punkte für spätere Slices

Auth, Wizard/Dependency-Check, Run (`dotnet run` + Dashboard + Live-Status), Deploy
(`aspire deploy` / docker compose / lokal auf Host), Reverse-Proxy/Freigabe,
Install-Script für Proxmox. Jeder wird eigenständig gebrainstormt.
