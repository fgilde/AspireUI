import { describe, it, expect } from "vitest";
import { toFlow, applyNodePosition, Stack } from "./model";

const stack: Stack = {
  id: "s1", name: "d", targetFramework: "net9.0",
  nodes: [{ id: "n1", varName: "db", addMethod: "AddPostgres", resourceName: "db", withCalls: [], x: 1, y: 2 }],
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
