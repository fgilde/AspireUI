import { describe, it, expect } from "vitest";
import { toFlow, applyNodePosition, removeNode, readWithRows, writeWithRows, setAddArg, toLiteral, fromLiteral, configureLiteral, matchOverloadByArity, isErrorLine, pickAppHost, runStateColor, routeForStatus, buildLiveOverlay, liveStateColor, isPathParam, lintStack, buildPresetNodes, type Stack, type Node, type CatalogOverload, type CatalogParam, type AuthStatus, type LiveResource, type ContainerPreset } from "./model";

const stack: Stack = {
  id: "s1", name: "d", targetFramework: "net9.0",
  nodes: [{ id: "n1", varName: "db", addMethod: "AddPostgres", resourceName: "db", withCalls: [], x: 1, y: 2, addArgs: [] }],
  edges: [{ id: "e1", fromNodeId: "n1", toNodeId: "n1", kind: "reference" }],
  rawStatements: [],
  extraFiles: [], extraPackages: [],
};

describe("model", () => {
  it("maps nodes to flow positions", () => {
    const f = toFlow(stack);
    expect(f.nodes[0].position).toEqual({ x: 1, y: 2 });
    expect(f.edges[0].source).toBe("n1");
  });
  it("updates a node position immutably", () => {
    const next = applyNodePosition(stack, "n1", 9, 9);
    expect(next.nodes[0].x).toBe(9);
    expect(stack.nodes[0].x).toBe(1);
  });
  it("removeNode cascades: drops dangling raws + withcalls referencing the removed var", () => {
    const s: Stack = {
      id: "s", name: "ai", targetFramework: "net10.0",
      nodes: [
        { id: "la", varName: "localai", addMethod: "AddLocalAI", resourceName: "localai", withCalls: [], x: 0, y: 0, addArgs: [] },
        { id: "n8", varName: "n8n", addMethod: "AddN8n", resourceName: "n8n", x: 0, y: 0, addArgs: [],
          withCalls: [
            { method: "WithEnvironment", args: ['"OPENAI_API_BASE_URL"', "localAiOpenAiBase"] },
            { method: "WithEnvironment", args: ['"OTHER"', '"keep"'] },
          ] },
      ],
      edges: [{ id: "e", fromNodeId: "n8", toNodeId: "la", kind: "waitFor" }],
      rawStatements: ['var localAiOpenAiBase = ReferenceExpression.Create($"{localai.Resource.HttpEndpoint}/v1");'],
      extraFiles: [], extraPackages: [],
    };
    const next = removeNode(s, "la");
    expect(next.nodes.map(n => n.id)).toEqual(["n8"]);          // localai gone
    expect(next.edges).toHaveLength(0);                          // edge to it gone
    expect(next.rawStatements).toHaveLength(0);                  // raw referencing localai gone
    const n8n = next.nodes[0];
    expect(n8n.withCalls).toHaveLength(1);                       // env using localAiOpenAiBase dropped...
    expect(n8n.withCalls[0].args).toEqual(['"OTHER"', '"keep"']); // ...unrelated one kept
  });
});

const container: Node = {
  id: "n1", varName: "web", addMethod: "AddContainer", resourceName: "web",
  addArgs: ['"nginx"'], withCalls: [{ method: "WithHttpEndpoint", args: ["8080", "80"] },
                                    { method: "WithEnvironment", args: ['"KEY"', '"val"'] }],
  x: 0, y: 0,
};

describe("config transform", () => {
  it("toLiteral / fromLiteral round-trip", () => {
    expect(toLiteral("nginx", "string")).toBe('"nginx"');
    expect(toLiteral("8080", "int")).toBe("8080");
    expect(fromLiteral('"nginx"')).toBe("nginx");
    expect(fromLiteral("8080")).toBe("8080");
  });
  it("number type keeps decimal precision (no parseInt truncation)", () => {
    expect(toLiteral("1.5", "number")).toBe("1.5");
    expect(toLiteral("8080", "int")).toBe("8080");
  });
  it("reads with-rows by method", () => {
    expect(readWithRows(container, "WithHttpEndpoint")).toEqual([["8080", "80"]]);
    expect(readWithRows(container, "WithEnvironment")).toEqual([['"KEY"', '"val"']]);
  });
  it("writes with-rows preserving other methods", () => {
    const next = writeWithRows(container, "WithHttpEndpoint", [["9090", "90"], ["9091", "91"]]);
    expect(readWithRows(next, "WithHttpEndpoint")).toEqual([["9090", "90"], ["9091", "91"]]);
    expect(readWithRows(next, "WithEnvironment")).toEqual([['"KEY"', '"val"']]); // untouched
  });
  it("sets an add-arg by index", () => {
    expect(setAddArg(container, 0, '"alpine"').addArgs[0]).toBe('"alpine"');
  });
  it("configureLiteral builds a lambda from set fields only", () => {
    const fields: CatalogParam[] = [
      { name: "GitRef", type: "string", required: false, label: "Git Ref" },
      { name: "ContextSubPath", type: "string", required: false, label: "Context Sub Path" },
    ];
    const vals: Record<string, string> = { GitRef: "master" };
    expect(configureLiteral(fields, n => vals[n] ?? "")).toBe('o => { o.GitRef = "master"; }');
    expect(configureLiteral(fields, () => "")).toBe(""); // nothing set -> arg dropped
  });
});

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

describe("log classifier", () => {
  it("flags error/exception/fail case-insensitively", () => {
    expect(isErrorLine("Unhandled Exception: NullReferenceException")).toBe(true);
    expect(isErrorLine("ERROR: build failed")).toBe(true);
    expect(isErrorLine("fatal: something failed to start")).toBe(true);
  });
  it("leaves normal log lines unflagged", () => {
    expect(isErrorLine("info: Now listening on http://localhost:5000")).toBe(false);
  });
});

describe("runStateColor", () => {
  it("maps run states to badge colors, NotRunning to nothing", () => {
    expect(runStateColor("NotRunning")).toBeUndefined();
    expect(runStateColor("Starting")).toBe("yellow");
    expect(runStateColor("Running")).toBe("green");
    expect(runStateColor("Failed")).toBe("red");
  });
});

describe("routeForStatus", () => {
  const admin = { id: "1", username: "admin", isAdmin: true, createdAt: "2026-01-01" };
  it("sends a fresh install to /setup", () => {
    const s: AuthStatus = { needsSetup: true, authenticated: false, user: null };
    expect(routeForStatus(s)).toBe("/setup");
  });
  it("sends an unauthenticated session to /login", () => {
    const s: AuthStatus = { needsSetup: false, authenticated: false, user: null };
    expect(routeForStatus(s)).toBe("/login");
  });
  it("allows an authenticated session into the app", () => {
    const s: AuthStatus = { needsSetup: false, authenticated: true, user: admin };
    expect(routeForStatus(s)).toBeNull();
  });
  it("needsSetup wins even if authenticated is somehow true", () => {
    const s: AuthStatus = { needsSetup: true, authenticated: true, user: admin };
    expect(routeForStatus(s)).toBe("/setup");
  });
});

describe("pickAppHost", () => {
  it("finds the file containing the CreateBuilder call", () => {
    const files = [
      { path: "Helpers.cs", content: "public class Helpers {}" },
      { path: "Program.cs", content: "var builder = DistributedApplication.CreateBuilder(args);" },
    ];
    expect(pickAppHost(files)).toBe("Program.cs");
  });
  it("returns undefined when no file matches", () => {
    expect(pickAppHost([{ path: "Helpers.cs", content: "public class Helpers {}" }])).toBeUndefined();
  });
});

describe("live overlay", () => {
  const lr = (p: Partial<LiveResource>): LiveResource =>
    ({ name: "", displayName: "", type: "Container", state: "Running", stateStyle: null, parent: null, urls: [], hidden: false, commands: [], ...p });
  const nodes = [{ id: "n1", resourceName: "supabase" }, { id: "n2", resourceName: "web" }];

  it("annotates a builder node with its matching top-level resource", () => {
    const o = buildLiveOverlay(nodes, [lr({ name: "supabase", displayName: "supabase", state: "Waiting" })]);
    expect(o.statusByNodeId.n1?.state).toBe("Waiting");
    expect(o.children).toHaveLength(0);
  });

  it("renders spawned children under their owner node with an edge to the parent", () => {
    const o = buildLiveOverlay(nodes, [
      lr({ name: "supabase", displayName: "supabase" }),
      lr({ name: "supabase-db-xyz", displayName: "supabase-db", parent: "supabase" }),
    ]);
    expect(o.children).toHaveLength(1);
    expect(o.children[0].ownerNodeId).toBe("n1");
    expect(o.children[0].parentElemId).toBe("n1"); // edge from the supabase builder node
  });

  it("chains grandchildren to the top-level owner, edge points at the live parent", () => {
    const o = buildLiveOverlay(nodes, [
      lr({ name: "supabase", displayName: "supabase" }),
      lr({ name: "supabase-db", displayName: "supabase-db", parent: "supabase" }),
      lr({ name: "supabase-db-init", displayName: "supabase-db-init", parent: "supabase-db" }),
    ]);
    const grandchild = o.children.find(c => c.live.name === "supabase-db-init")!;
    expect(grandchild.ownerNodeId).toBe("n1");             // resolves to top-level node
    expect(grandchild.parentElemId).toBe("live:supabase-db"); // but edge attaches to its live parent
  });

  it("treats a resource with no matching node/parent as an orphan (no owner, no edge)", () => {
    const o = buildLiveOverlay(nodes, [lr({ name: "monitoring-grafana", displayName: "monitoring-grafana" })]);
    expect(o.children[0].ownerNodeId).toBeNull();
    expect(o.children[0].parentElemId).toBeNull();
  });

  it("drops hidden resources (e.g. the dashboard itself)", () => {
    const o = buildLiveOverlay(nodes, [lr({ name: "aspire-dashboard", displayName: "aspire-dashboard", hidden: true })]);
    expect(o.children).toHaveLength(0);
    expect(Object.keys(o.statusByNodeId)).toHaveLength(0);
  });

  it("maps states to traffic-light colors", () => {
    expect(liveStateColor("Running")).toBe("green");
    expect(liveStateColor("FailedToStart")).toBe("red");
    expect(liveStateColor("Waiting")).toBe("yellow");
    expect(liveStateColor(null)).toBe("gray");
  });

  it("detects path-like string params", () => {
    const p = (name: string, type: CatalogParam["type"] = "string"): CatalogParam => ({ name, type, required: false, label: name });
    expect(isPathParam(p("configRootPath"))).toBe(true);
    expect(isPathParam(p("workingDirectory"))).toBe(true);
    expect(isPathParam(p("scriptPath"))).toBe(true);
    expect(isPathParam(p("name"))).toBe(false);
    expect(isPathParam(p("path", "int"))).toBe(false); // only string params
  });
});

describe("lintStack", () => {
  const mk = (nodes: Node[], edges = stack.edges.filter(() => false)): Stack =>
    ({ id: "s", name: "s", targetFramework: "net10.0", nodes, edges, rawStatements: [], extraFiles: [], extraPackages: [] });
  const node = (id: string, name: string, withCalls: Node["withCalls"] = []): Node =>
    ({ id, varName: name, addMethod: "AddContainer", resourceName: name, withCalls, x: 0, y: 0, addArgs: [] });

  it("flags duplicate resource names as error", () => {
    const issues = lintStack(mk([node("n1", "db"), node("n2", "db")]));
    expect(issues.some(i => i.severity === "error" && i.message.includes("db"))).toBe(true);
  });
  it("flags colliding fixed ports as warning", () => {
    const hp = [{ method: "WithHttpEndpoint", args: ["port: 8080"] }];
    const issues = lintStack(mk([node("n1", "a", hp), node("n2", "b", hp)]));
    expect(issues.some(i => i.severity === "warning" && i.message.includes("8080"))).toBe(true);
  });
  it("flags dangling edges", () => {
    const s = mk([node("n1", "a")]);
    s.edges = [{ id: "e", fromNodeId: "n1", toNodeId: "gone", kind: "reference" }];
    expect(lintStack(s).some(i => i.message.includes("Dangling"))).toBe(true);
  });
  it("clean stack has no issues", () => {
    expect(lintStack(mk([node("n1", "a"), node("n2", "b")]))).toHaveLength(0);
  });
});

describe("buildPresetNodes", () => {
  const preset: ContainerPreset = {
    id: "app", label: "App", group: "Tools", image: "app:latest", port: 8080,
    env: [["DB", "${db}"], ["MODE", "prod"]],
    companions: [{ key: "db", addMethod: "AddContainer", resourceName: "app-db", image: "postgres:16", env: [["POSTGRES_DB", "app"]] }],
  };

  it("drops main + companions and wires reference + waitFor edges", () => {
    const { nodes, edges } = buildPresetNodes(preset, new Set());
    expect(nodes).toHaveLength(2);
    expect(edges).toHaveLength(2); // reference + waitFor main->db
    expect(edges.map(e => e.kind).sort()).toEqual(["reference", "waitFor"]);
  });

  it("expands ${key} env tokens to the companion var (raw expr) and quotes plain values", () => {
    const { nodes } = buildPresetNodes(preset, new Set());
    const main = nodes[0];
    const dbVar = nodes[1].varName;
    const env = main.withCalls.filter(w => w.method === "WithEnvironment");
    expect(env.find(e => e.args[0] === '"DB"')!.args[1]).toBe(dbVar);       // raw, no quotes
    expect(env.find(e => e.args[0] === '"MODE"')!.args[1]).toBe('"prod"');  // quoted literal
  });

  it("dedupes names against existing", () => {
    const { nodes } = buildPresetNodes(preset, new Set(["app"]));
    expect(nodes[0].resourceName).toBe("app2");
  });

  it("single-container preset (no companions) drops just one node", () => {
    const { nodes, edges } = buildPresetNodes({ id: "x", label: "X", group: "T", image: "x:1", port: 80 }, new Set());
    expect(nodes).toHaveLength(1);
    expect(edges).toHaveLength(0);
  });
});
