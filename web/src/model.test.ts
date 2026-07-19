import { describe, it, expect } from "vitest";
import { toFlow, applyNodePosition, readWithRows, writeWithRows, setAddArg, toLiteral, fromLiteral, type Stack, type Node } from "./model";

const stack: Stack = {
  id: "s1", name: "d", targetFramework: "net9.0",
  nodes: [{ id: "n1", varName: "db", addMethod: "AddPostgres", resourceName: "db", withCalls: [], x: 1, y: 2, addArgs: [] }],
  edges: [{ id: "e1", fromNodeId: "n1", toNodeId: "n1", kind: "reference" }],
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
