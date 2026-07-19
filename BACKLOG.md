# AspireUI Backlog

Slices in order. Each gets its own spec → plan → implementation cycle.

## DONE — Slice 4: Demo templates + model power ✅
## DONE — Slice 5: Import (.cs/.csproj/.zip + custom code) ✅
## DONE — Slice 6: Settings + built-in AI assistant ✅

<details><summary>Slice 4 detail (done)</summary>

### Slice 4: Demo templates + model power
- Raw statements in the model (verbatim lines in the marker block; import preserves unknown statements instead of dropping)
- `WaitFor` as edge kind (dashed edge on canvas)
- Packages: `CommunityToolkit.Aspire.Hosting.Ollama`, `Nextended.Aspire` (AddGithubRepository) discovered + overlay package map
- "Create from demo" dropdown on the stacks overview → Local AI demo template (ollama + localai + n8n wired, runs)
- NuGet packages panel per stack (which package, which version, why/by which resource)
- Run error/log panel (live log, failures highlighted, reason visible)

## Slice 5: Import
- Import a stack from `.cs` file, `.csproj`, or ZIP
- Single-file import: ask for folder permission via the JS File System Access API to find extensions/references; ZIP: search inside the archive
- Custom user code / custom extension methods survive (raw statements) and are usable
- Import parser learns chained declarations (`builder.AddX("a").WithY()` → node + withCalls)
</details>

## Next up (not yet built — needs prioritization)
- Auth + user management (first-run wizard creates admin)
- Wizard dependency check (.NET, Docker, …) + guided setup
- Deploy: aspire deploy / docker compose / run-on-host, share/expose
- Reverse proxy integration
- Proxmox/server install script
- `AddProject<T>` generic type args
- Foreign-node import UI (read-only nodes for unparseable code)
- Semantic compile check (real Aspire references)
- Log streaming (replace status polling)
- NU1903 advisory bump (transitive SQLitePCLRaw)
- Per-stack dock layout persistence
- Child resources with own variable (`var db = pg.AddDatabase(...)`) referencable as nodes
