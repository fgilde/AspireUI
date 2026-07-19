import { describe, it, expect } from "vitest";
import { toFlow, applyNodePosition, readWithRows, writeWithRows, setAddArg, toLiteral, fromLiteral, matchOverloadByArity, isErrorLine, type Stack, type Node, type CatalogOverload } from "./model";

const stack: Stack = {
  id: "s1", name: "d", targetFramework: "net9.0",
  nodes: [{ id: "n1", varName: "db", addMethod: "AddPostgres", resourceName: "db", withCalls: [], x: 1, y: 2, addArgs: [] }],
  edges: [{ id: "e1", fromNodeId: "n1", toNodeId: "n1", kind: "reference" }],
  rawStatements: [],
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
