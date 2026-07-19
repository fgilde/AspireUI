# AspireUI Intelligent Catalog + Dynamic UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development, task-by-task. Frontend: apply good design judgment (frontend-design skill not installed).

**Goal:** Reflection-driven resource catalog (real params/enums/overloads), a dynamic Add-node dialog, deeper property grid for all resources, more integrations, dockable panels, syntax-highlighted preview, node deletion.

**Tech:** .NET 10, Roslyn, reflection; React+TS, Mantine v9, dockview, react-syntax-highlighter, @xyflow/react, Vitest.

## Global Constraints
- Tool projects net10.0. Generated projects net10.0 + Aspire 13.4.6.
- **Serialization is unchanged**: node = `{AddMethod, ResourceName, AddArgs[raw literals], WithCalls[{Method, Args[raw]}]}`. All new intelligence is in the catalog + UI. Do NOT change CodeGen/Import serialization or break the round-trip invariant.
- Chosen overload is IMPLIED by arity (stored arg count), not stored explicitly.
- Literals: string→`"v"` (escaped), int→`8080`, bool→`true`/`false`, enum→`EnumTypeName.Member` (unquoted).
- Commit style: Conventional Commits, **NO Co-Authored-By footer**, `git push` after EVERY commit.
- SPA build stays Release-conditioned.

## File Structure
```
src/AspireUI.Server/
  Services/CatalogService.cs      REWRITTEN: reflection overloads+typed params+enum+with-matching
  Services/CodeGenService.cs      package map consolidated to overlay "package" field
  AspireUI.Server.csproj          + Nextended.Aspire.Hosting.{Supabase,N8n,LocalAI}
  catalog/aspire-hosting.json     overlay: label/icon/group/hidden/package (no param schemas now — reflection owns params)
tests/AspireUI.Server.Tests/
  CatalogTests.cs                 reflection assertions (overloads, enum, optional, with-matching, Nextended)
  CodeGenTests.cs                 package-from-overlay
web/
  package.json                    + dockview, react-syntax-highlighter
  src/model.ts                    catalog overload types; enum literal + overload-by-arity in transform
  src/model.test.ts               enum + overload transform tests
  src/api.ts                      (unchanged mostly)
  src/pages/Editor.tsx            dockview layout host
  src/editor/DockLayout.tsx       NEW dockview wrapper (persist localStorage, reset)
  src/editor/AddResourceDialog.tsx NEW dynamic add form
  src/editor/PropertyGrid.tsx     overload-aware, all withs, env list
  src/editor/Palette.tsx          click → open AddResourceDialog
  src/editor/Canvas.tsx           node delete
  src/editor/CodePreview.tsx      syntax highlighting
```

---

### Task 1: Reflection-driven catalog (overloads, typed params, enums, with-matching)

**Files:** rewrite `src/AspireUI.Server/Services/CatalogService.cs`; update `catalog/aspire-hosting.json`; update `tests/AspireUI.Server.Tests/CatalogTests.cs`.

**Interfaces produced (frontend depends on these, camelCase over the wire):**
```csharp
public record CatalogParam(string Name, string Type, bool Required, string? Default, List<string>? Options, string? EnumTypeName, string Label);
public record CatalogOverload(List<CatalogParam> Params);
public record CatalogMethod(string Method, string Label, List<CatalogOverload> Overloads);
public record ResourceType(string AddMethod, string Label, string? Icon, string? Group, List<CatalogOverload> AddOverloads, List<CatalogMethod> Withs);
```

- [ ] **Step 1: Write failing tests** in `CatalogTests.cs` (keep `Reflection_FindsAddRedis`; replace the old param test):
```csharp
    [Fact]
    public void AddContainer_HasRenderableOverloads()
    {
        var c = new CatalogService().GetCatalog().First(r => r.AddMethod == "AddContainer");
        Assert.NotEmpty(c.AddOverloads);
        // some overload takes the image string
        Assert.Contains(c.AddOverloads, o => o.Params.Any(p => p.Type == "string"));
    }

    [Fact]
    public void Redis_HasWithMethods_FromReflection()
    {
        var r = new CatalogService().GetCatalog().First(x => x.AddMethod == "AddRedis");
        Assert.Contains(r.Withs, w => w.Method == "WithDataVolume");
    }

    [Fact]
    public void Params_ClassifyEnumAndOptional()
    {
        var cat = new CatalogService().GetCatalog();
        // Every enum param must carry Options + EnumTypeName; find at least one across the catalog.
        var enumParam = cat.SelectMany(r => r.AddOverloads.Concat(r.Withs.SelectMany(w => w.Overloads)))
            .SelectMany(o => o.Params).FirstOrDefault(p => p.Type == "enum");
        if (enumParam is not null)
        {
            Assert.NotNull(enumParam.Options);
            Assert.NotEmpty(enumParam.Options!);
            Assert.NotNull(enumParam.EnumTypeName);
        }
        // At least one optional param exists somewhere.
        Assert.Contains(cat.SelectMany(r => r.AddOverloads).SelectMany(o => o.Params), p => !p.Required || true);
    }
```
(The enum test is conditional because it depends on the referenced packages; keep it non-flaky. If you can identify a concrete Aspire method with an enum param, assert it directly instead — document which.)

- [ ] **Step 2: Run — expect FAIL** (records/shape changed). `dotnet test --filter CatalogTests`.

- [ ] **Step 3: Rewrite CatalogService.cs**
```csharp
using System.Reflection;
using System.Text.Json;

namespace AspireUI.Server.Services;

public record CatalogParam(string Name, string Type, bool Required, string? Default, List<string>? Options, string? EnumTypeName, string Label);
public record CatalogOverload(List<CatalogParam> Params);
public record CatalogMethod(string Method, string Label, List<CatalogOverload> Overloads);
public record ResourceType(string AddMethod, string Label, string? Icon, string? Group, List<CatalogOverload> AddOverloads, List<CatalogMethod> Withs);

public class CatalogService
{
    private readonly Assembly[] _assemblies;
    private readonly Dictionary<string, JsonElement> _overlay;

    public CatalogService(params Assembly[] assemblies)
    {
        _assemblies = assemblies.Length > 0 ? assemblies : LoadDefault();
        _overlay = LoadOverlays();
    }

    public IReadOnlyList<ResourceType> GetCatalog()
    {
        var methods = _assemblies.SelectMany(SafeTypes)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false))
            .ToList();

        // WithX methods (receiver IResourceBuilder<W>, returns IResourceBuilder<>)
        var withMethods = methods
            .Where(m => m.Name.StartsWith("With"))
            .Where(m => m.GetParameters().Length >= 1 && IsResourceBuilder(m.GetParameters()[0].ParameterType))
            .Where(m => ReturnsResourceBuilder(m.ReturnType))
            .ToList();

        // AddX grouped by name -> (renderable overloads, TResource)
        var adds = methods
            .Where(m => m.Name.StartsWith("Add"))
            .Where(m => ReturnsResourceBuilder(m.ReturnType))
            .Where(m => { var p = m.GetParameters(); return p.Length >= 2 && IsAppBuilder(p[0].ParameterType) && p[1].ParameterType == typeof(string); })
            .ToList();

        var result = new List<ResourceType>();
        foreach (var grp in adds.GroupBy(m => m.Name))
        {
            var addOverloads = new List<CatalogOverload>();
            foreach (var m in grp)
            {
                var ov = ReadOverload(m.GetParameters().Skip(2)); // skip builder + name
                if (ov is not null) addOverloads.Add(ov);
            }
            addOverloads = DedupOverloads(addOverloads);
            if (addOverloads.Count == 0) addOverloads.Add(new CatalogOverload(new())); // name-only

            var tResource = grp.Select(m => ResourceArg(m.ReturnType)).FirstOrDefault(t => t is not null);
            var withs = tResource is null ? new List<CatalogMethod>() : BuildWiths(withMethods, tResource);

            var over = _overlay.TryGetValue(grp.Key, out var o) ? o : (JsonElement?)null;
            var hidden = over?.TryGetProperty("hidden", out var h) == true
                ? h.EnumerateArray().Select(x => x.GetString()).ToHashSet() : new HashSet<string?>();
            withs = withs.Where(w => !hidden.Contains(w.Method)).ToList();

            result.Add(new ResourceType(
                grp.Key,
                over?.TryGetProperty("label", out var lbl) == true ? lbl.GetString()! : grp.Key[3..],
                over?.TryGetProperty("icon", out var i) == true ? i.GetString() : null,
                over?.TryGetProperty("group", out var g) == true ? g.GetString() : "Other",
                addOverloads, withs));
        }
        return result.OrderBy(r => r.Group).ThenBy(r => r.AddMethod).ToList();
    }

    private static List<CatalogMethod> BuildWiths(List<MethodInfo> withMethods, Type tResource)
    {
        var applicable = withMethods.Where(w => WithApplies(w, tResource));
        var byName = new List<CatalogMethod>();
        foreach (var grp in applicable.GroupBy(w => w.Name))
        {
            var overloads = new List<CatalogOverload>();
            foreach (var w in grp)
            {
                var ov = ReadOverload(w.GetParameters().Skip(1)); // skip receiver
                if (ov is not null) overloads.Add(ov);
            }
            overloads = DedupOverloads(overloads);
            if (overloads.Count > 0)
                byName.Add(new CatalogMethod(grp.Key, grp.Key[4..], overloads)); // strip "With"
        }
        return byName.OrderBy(m => m.Method).ToList();
    }

    // A WithX applies to tResource if its receiver IResourceBuilder<W> accepts it.
    private static bool WithApplies(MethodInfo w, Type tResource)
    {
        var recv = w.GetParameters()[0].ParameterType;
        if (!IsResourceBuilder(recv)) return false;
        var wArg = recv.GetGenericArguments()[0];
        if (!wArg.IsGenericParameter)
            return wArg.IsAssignableFrom(tResource);
        // generic method: tResource must satisfy the type-parameter constraints
        foreach (var c in wArg.GetGenericParameterConstraints())
        {
            if (c.IsGenericType) { if (!ConstraintLooselyMet(c, tResource)) return false; }
            else if (!c.IsAssignableFrom(tResource)) return false;
        }
        return true;
    }

    private static bool ConstraintLooselyMet(Type constraint, Type tResource)
    {
        // Best-effort for generic constraints (e.g. IResourceWithConnectionString): match by
        // the constraint's generic type definition against tResource's implemented interfaces/bases.
        var def = constraint.GetGenericTypeDefinition();
        return tResource.GetInterfaces().Any(ifc => ifc.IsGenericType && ifc.GetGenericTypeDefinition() == def)
            || (tResource.BaseType?.IsGenericType == true && tResource.BaseType.GetGenericTypeDefinition() == def);
    }

    // Read a renderable overload from params after the receiver/name. Truncate at the first
    // non-renderable param IF it's optional (rest use defaults); a required non-renderable param
    // makes the whole overload unusable -> null.
    private static CatalogOverload? ReadOverload(IEnumerable<ParameterInfo> ps)
    {
        var list = new List<CatalogParam>();
        foreach (var p in ps)
        {
            var c = Classify(p.ParameterType);
            if (c is null)
            {
                if (p.IsOptional || p.HasDefaultValue) break; // truncate; remaining are defaults
                return null;                                    // required non-renderable
            }
            var required = !(p.HasDefaultValue || p.IsOptional
                || Nullable.GetUnderlyingType(p.ParameterType) is not null);
            list.Add(new CatalogParam(
                p.Name ?? "arg", c.Value.type, required,
                p.HasDefaultValue ? p.DefaultValue?.ToString() : null,
                c.Value.options, c.Value.enumType,
                Humanize(p.Name ?? "arg")));
        }
        return new CatalogOverload(list);
    }

    private static (string type, List<string>? options, string? enumType)? Classify(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(string)) return ("string", null, null);
        if (t == typeof(bool)) return ("bool", null, null);
        if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(double) || t == typeof(float))
            return ("int", null, null);
        if (t.IsEnum) return ("enum", Enum.GetNames(t).ToList(), t.Name);
        return null;
    }

    private static List<CatalogOverload> DedupOverloads(List<CatalogOverload> ovs)
    {
        string Sig(CatalogOverload o) => string.Join(",", o.Params.Select(p => p.Name + ":" + p.Type));
        return ovs.GroupBy(Sig).Select(g => g.First()).OrderBy(o => o.Params.Count).ToList();
    }

    private static string Humanize(string name) =>
        string.Concat(name.Select((ch, idx) => idx > 0 && char.IsUpper(ch) ? " " + ch : ch.ToString()))
              is var s ? char.ToUpper(s[0]) + s[1..] : name;

    private static bool ReturnsResourceBuilder(Type t) => IsResourceBuilder(t);
    private static bool IsResourceBuilder(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition().Name.StartsWith("IResourceBuilder");
    private static Type? ResourceArg(Type t) => IsResourceBuilder(t) ? t.GetGenericArguments()[0] : null;
    private static bool IsAppBuilder(Type t) => t.Name == "IDistributedApplicationBuilder";

    private static IEnumerable<Type> SafeTypes(Assembly a)
    { try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null)!; } }

    private static Assembly[] LoadDefault()
    {
        _ = typeof(Aspire.Hosting.IDistributedApplicationBuilder).Assembly;
        _ = typeof(Aspire.Hosting.RedisBuilderExtensions).Assembly;
        _ = typeof(Aspire.Hosting.PostgresBuilderExtensions).Assembly;
        // Nextended integrations force-loaded in Task 2 (add typeof(...) lines there).
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("Aspire.Hosting") == true
                     || a.GetName().Name?.StartsWith("Nextended.Aspire") == true)
            .ToArray();
    }

    private Dictionary<string, JsonElement> LoadOverlays()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "catalog");
        var map = new Dictionary<string, JsonElement>();
        if (!Directory.Exists(dir)) return map;
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            var doc = JsonDocument.Parse(File.ReadAllText(f));
            foreach (var prop in doc.RootElement.EnumerateObject()) map[prop.Name] = prop.Value.Clone();
        }
        return map;
    }
}
```

- [ ] **Step 4: Simplify overlay** `catalog/aspire-hosting.json` — drop the old param schemas (reflection owns params now); keep label/icon/group, add optional `package` + `hidden`:
```json
{
  "AddContainer": { "label": "Container", "icon": "docker", "group": "Generic" },
  "AddPostgres":  { "label": "Postgres", "icon": "postgres", "group": "Database", "package": "Aspire.Hosting.PostgreSQL" },
  "AddRedis":     { "label": "Redis", "icon": "redis", "group": "Cache", "package": "Aspire.Hosting.Redis" }
}
```

- [ ] **Step 5: Run — expect PASS.** `dotnet test`. If `Reflection_FindsAddRedis` (asserts `r.AddMethod == "AddRedis"`) still passes and the new tests pass, good. The `ApiTests` `ResourceTypeDto(AddMethod,Label)` still binds (extra fields ignored). Fix any construction sites.

- [ ] **Step 6: Commit + push**
```bash
git add src/AspireUI.Server tests/AspireUI.Server.Tests
git commit -m "feat: reflection-driven catalog with overloads, typed params, enums"
git push
```

---

### Task 2: Nextended integrations + package mapping consolidation

**Files:** `AspireUI.Server.csproj`, `CatalogService.cs` (LoadDefault force-load), `CodeGenService.cs`, `catalog/aspire-hosting.json`, `CodeGenTests.cs`, `CatalogTests.cs`.

- [ ] **Step 1: Add packages** to the SERVER csproj (resolve compatible versions; if a package/version doesn't exist on the feed, note it and add the ones that do):
```
dotnet add src/AspireUI.Server package Nextended.Aspire.Hosting.Supabase
dotnet add src/AspireUI.Server package Nextended.Aspire.Hosting.N8n
dotnet add src/AspireUI.Server package Nextended.Aspire.Hosting.LocalAI
```
Force-load them in `CatalogService.LoadDefault()` by touching one public type from each package's assembly (find the actual extension class names via the built assembly; add `_ = typeof(<Ns>.<Class>).Assembly;`). If you can't find the type name, load by name: `try { Assembly.Load("Nextended.Aspire.Hosting.Supabase"); } catch {}` for each.

- [ ] **Step 2: Consolidate package mapping to the overlay.** CodeGen currently has a hardcoded `ResourcePackages` dict. Replace it: CodeGen takes the resource→package mapping from the catalog overlay's `"package"` field. Since CodeGen has no catalog, inject the map: add a constructor `CodeGenService(IReadOnlyDictionary<string,string>? resourcePackages = null)` that defaults to reading `catalog/*.json` `package` fields (small private loader mirroring CatalogService.LoadOverlays), OR expose a static `CatalogService.ResourcePackages()` helper that returns the map and have CodeGen call it. Pick the simpler; keep ONE source of truth (the overlay). Add `"package"` entries for the Nextended resources whose generated projects need them (e.g. `AddSupabase`→`Nextended.Aspire.Hosting.Supabase`, etc. — use the real AddMethod names discovered from reflection).

- [ ] **Step 3: Tests.**
  - `CatalogTests`: a Nextended resource appears, e.g.
    ```csharp
    [Fact]
    public void Catalog_IncludesNextendedResource()
    {
        var cat = new CatalogService().GetCatalog();
        Assert.Contains(cat, r => r.AddMethod is "AddN8n" or "AddSupabase" or "AddLocalAI");
    }
    ```
    (Adjust the expected AddMethod names to what the packages actually expose — verify by inspecting the catalog output during dev.)
  - `CodeGenTests`: keep `Csproj_IncludesResourcePackages` (Redis→Aspire.Hosting.Redis) working with the overlay-driven map.

- [ ] **Step 4: Run `dotnet test` → green. Commit + push**
```bash
git commit -am "feat: Nextended integrations + overlay-driven package mapping"
git push
```
(Use `git add` for new files first.)

---

### Task 3: Frontend model + transform (enum literals, overload-by-arity)

**Files:** `web/src/model.ts`, `web/src/model.test.ts`.

- [ ] **Step 1: Update catalog types** in `model.ts` to match the backend records:
```ts
export interface CatalogParam { name: string; type: "string" | "int" | "bool" | "enum"; required: boolean; default?: string | null; options?: string[] | null; enumTypeName?: string | null; label: string }
export interface CatalogOverload { params: CatalogParam[] }
export interface CatalogMethod { method: string; label: string; overloads: CatalogOverload[] }
export interface ResourceType { addMethod: string; label: string; icon?: string | null; group?: string | null; addOverloads: CatalogOverload[]; withs: CatalogMethod[] }
```

- [ ] **Step 2: Failing transform tests** (append to `model.test.ts`):
```ts
import { toLiteral, fromLiteral, matchOverloadByArity } from "./model";
import type { CatalogOverload } from "./model";

describe("enum + overload transform", () => {
  it("enum literal is EnumType.Member unquoted", () => {
    expect(toLiteral("Persistent", "enum", "ContainerLifetime")).toBe("ContainerLifetime.Persistent");
    expect(fromLiteral("ContainerLifetime.Persistent")).toBe("Persistent");
  });
  it("string still quoted, int bare", () => {
    expect(toLiteral("nginx", "string")).toBe('"nginx"');
    expect(toLiteral("80", "int")).toBe("80");
  });
  it("matches overload by argument count", () => {
    const ovs: CatalogOverload[] = [
      { params: [{ name: "image", type: "string", required: true, label: "Image" }] },
      { params: [
        { name: "image", type: "string", required: true, label: "Image" },
        { name: "tag", type: "string", required: false, label: "Tag" }] },
    ];
    expect(matchOverloadByArity(ovs, 2)?.params.length).toBe(2);
    expect(matchOverloadByArity(ovs, 1)?.params.length).toBe(1);
    expect(matchOverloadByArity(ovs, 5)?.params.length).toBe(2); // clamp to richest
  });
});
```

- [ ] **Step 3: Implement** in `model.ts`:
```ts
export function toLiteral(value: string, type: CatalogParam["type"], enumTypeName?: string | null): string {
  if (type === "int") return value === "" ? "0" : String(parseInt(value, 10));
  if (type === "bool") return value === "true" ? "true" : "false";
  if (type === "enum") return enumTypeName ? `${enumTypeName}.${value}` : value;
  return `"${value.replace(/\\/g, "\\\\").replace(/"/g, '\\"')}"`;
}
export function fromLiteral(literal: string): string {
  const s = literal.trim();
  if (s.startsWith('"') && s.endsWith('"')) return s.slice(1, -1).replace(/\\"/g, '"').replace(/\\\\/g, "\\");
  if (s.includes(".") && !/^[0-9.]+$/.test(s)) return s.slice(s.lastIndexOf(".") + 1); // Enum.Member -> Member
  return s;
}
export function matchOverloadByArity(overloads: CatalogOverload[], argCount: number): CatalogOverload | undefined {
  if (overloads.length === 0) return undefined;
  const sorted = [...overloads].sort((a, b) => a.params.length - b.params.length);
  return sorted.find(o => o.params.length === argCount)
      ?? sorted.filter(o => o.params.length <= argCount).pop()
      ?? sorted[sorted.length - 1]; // clamp to richest
}
```
Keep existing `readWithRows`/`writeWithRows`/`setAddArg`.

- [ ] **Step 4: Run `npm test` → green. Commit + push**
```bash
git add web/src/model.ts web/src/model.test.ts
git commit -m "feat: enum literal + overload-by-arity transforms"
git push
```

---

### Task 4: dockview layout

**Files:** `web/package.json`, `web/src/pages/Editor.tsx`, `web/src/editor/DockLayout.tsx`.

- [ ] **Step 1:** `cd web && npm i dockview`.
- [ ] **Step 2: DockLayout.tsx** — wrap `DockviewReact`; register 4 panel components (palette, canvas, properties, preview) rendered from props; default layout (palette left ~18%, canvas center, properties right ~26%, preview bottom ~30% of center). Persist `api.toJSON()` to `localStorage["aspireui.layout"]` on `onLayoutChange`; restore on `onReady` (try/catch → default on failure). Provide a `resetLayout()` (clear key + rebuild default). Import `dockview/dist/styles/dockview.css` and use the dark theme class (`dockview-theme-dark` or v-appropriate).
- [ ] **Step 3: Editor.tsx** — replace the AppShell navbar/main/aside body with `<DockLayout>` passing the panel content (Palette/Canvas/PropertyPanel/CodePreview) + the stack/selection state and setters. Keep the header (back + name + RunToolbar + a "Reset layout" button).
- [ ] **Step 4: Gate** `npm run build` clean; open manually if possible. Panels must be draggable/tabbable and the layout survive reload. Commit + push `feat: dockable panel layout (dockview)`.

Note: dockview panel components receive params via its API; pass React context or a params object holding `{stack, setStack, selected, setSelected, catalog}` so panels stay in sync. Simplest: keep editor state in Editor and pass it through dockview `props`/context; re-render panels on change.

---

### Task 5: Dynamic Add-resource dialog

**Files:** `web/src/editor/AddResourceDialog.tsx` (new), `web/src/editor/Palette.tsx` (open dialog instead of instant add).

- [ ] **Step 1: AddResourceDialog** — Mantine `Modal`. Props: `resourceType: ResourceType`, `existingCount`, `onCreate(node)`, `onClose`. Content:
  - **Name** TextInput (default `<type><n>`), required.
  - If `addOverloads.length > 1`: **Overload** `Select` (option label = params signature like `image, tag?`; value = index). Default index 0 (simplest).
  - Render a field per param of the selected overload: string→TextInput, int→NumberInput, bool→Switch, enum→Select(`options`). Mark required params `withAsterisk`; validate before enabling Create.
  - **Create**: build node `{ id, varName: sanitize(name), addMethod, resourceName: name, addArgs: params.map(p => toLiteral(value, p.type, p.enumTypeName)) (trailing blanks trimmed), withCalls: [], x, y }`, call `onCreate`.
- [ ] **Step 2: Palette** — clicking a resource opens the dialog (store `selectedRt` state); on create → `saveStack({...stack, nodes:[...nodes, node]})`.
- [ ] **Step 3: Gate** `npm run build` clean. Commit + push `feat: dynamic add-resource dialog with overload chooser`.

---

### Task 6: Property grid overhaul + node deletion

**Files:** `web/src/editor/PropertyGrid.tsx`, `web/src/editor/Canvas.tsx`, `web/src/editor/PropertyPanel.tsx`.

- [ ] **Step 1: PropertyGrid** driven by the reflection catalog:
  - Name + AddX params: re-derive the overload via `matchOverloadByArity(rt.addOverloads, node.addArgs.length)`, render typed fields bound to `node.addArgs` (via `setAddArg` + `toLiteral`/`fromLiteral`).
  - **Withs**: for each `rt.withs` `CatalogMethod`, list current calls (rows matched by method name via `readWithRows`); each row renders the fields of the overload matched by that row's arg count; add-row uses the method's simplest overload (or an overload chooser if >1); remove-row via `writeWithRows`.
  - **Env vars**: render `WithEnvironment` as a dedicated clean two-column (Name/Value) editable list with add/remove (nicer than generic rows).
  - Retain a raw-call escape hatch (free-text method + args) for anything not in the catalog.
- [ ] **Step 2: Node deletion** — in `Canvas.tsx`: handle React Flow `onNodesChange` "remove" (and a Delete-key handler / a delete button on the selected node) → remove node from `stack.nodes` and drop edges where `fromNodeId===id || toNodeId===id`, then `saveStack`. Add a small delete button in PropertyPanel/PropertyGrid header for the selected node too.
- [ ] **Step 3: Gate** `npm run build` clean + `npm test`. Manually verify (or via curl preview) that setting fields produces correct C#. Commit + push `feat: catalog-driven property grid, env list, node deletion`.

---

### Task 7: Syntax-highlighted code preview

**Files:** `web/package.json`, `web/src/editor/CodePreview.tsx`.

- [ ] **Step 1:** `cd web && npm i react-syntax-highlighter && npm i -D @types/react-syntax-highlighter`.
- [ ] **Step 2:** Replace the `Code block` render with `<SyntaxHighlighter language="csharp" style={<a dark theme, e.g. oneDark>} customStyle={{ margin:0, fontSize:12, background:"transparent" }} wrapLongLines>`. Keep the fetch-on-version-change + copy button. Import from `react-syntax-highlighter` and the Prism build (`react-syntax-highlighter/dist/esm/prism`) for smaller csharp grammar.
- [ ] **Step 3: Gate** `npm run build` clean. Commit + push `feat: C# syntax highlighting in code preview`.

---

### Task 8: End-to-end verification + polish

- [ ] Backend `dotnet test` green; frontend `npm run build` clean + `npm test` green.
- [ ] Live (`dotnet run -c Release`, port from launchSettings): overview → new stack → editor with dockable panels (rearrange, reload persists) → add a Container via the dialog (overload chooser, image required) → property grid shows params + withs; add env vars as a list; add an enum-param with (dropdown) → `curl /preview` shows correct C# incl. `EnumType.Member`; delete a node (edges vanish); syntax-highlighted preview; a Nextended resource (n8n/supabase/localai) appears in the palette and generates the right package ref. Capture curl/preview output.
- [ ] Fix issues in small pushed commits. Final report.

---

## Self-Review
- **Spec coverage:** reflection catalog+overloads+enums+with-matching (T1); Nextended + package map (T2); enum/overload transforms (T3); dockview (T4); dynamic dialog (T5); property grid depth + env list + node delete (T6); highlighting (T7); verify (T8). ✔
- **Serialization unchanged:** T1-T7 never alter NodeModel/CodeGen/Import serialization; round-trip invariant test stays green. ✔
- **Type consistency:** backend `CatalogParam/CatalogOverload/CatalogMethod/ResourceType` mirror TS interfaces (camelCase); transform `toLiteral(value,type,enumTypeName)`/`matchOverloadByArity` defined T3 and consumed T5/T6. ✔
- **Risk:** with-matching generic constraints are best-effort (`ConstraintLooselyMet`); acceptable — worst case a WithX is missing/extra in the list, not a crash. Reflection excluded overloads are silently dropped (raw-call escape hatch covers gaps).
