import { describe, it, expect } from "vitest";
import { toFlow, applyNodePosition, readWithRows, writeWithRows, setAddArg, toLiteral, fromLiteral, matchOverloadByArity, isErrorLine, pickAppHost, runStateColor, routeForStatus, type Stack, type Node, type CatalogOverload, type AuthStatus } from "./model";

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
