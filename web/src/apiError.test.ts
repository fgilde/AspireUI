import { describe, expect, it, vi } from "vitest";
import { errorMessage, settle } from "./api";

describe("errorMessage", () => {
  it("never shows an html error page", () => {
    const m = errorMessage(504, "Gateway Timeout", "<!DOCTYPE html><html><head><title>nksoft.de | 504: Gateway time-out</title></head><body>...</body></html>");
    expect(m).not.toContain("<");
    expect(m).toContain("504");
    expect(m).toContain("still running");
  });
  it("prefers the server's own message", () => {
    expect(errorMessage(400, "Bad Request", JSON.stringify({ message: "name is required" }))).toBe("name is required");
  });
  it("falls back to the status line", () => {
    expect(errorMessage(500, "Internal Server Error", "")).toBe("500 Internal Server Error");
    expect(errorMessage(422, "", "plain text reason")).toBe("plain text reason");
  });
});

describe("settle", () => {
  it("waits until a deployment is no longer deploying", async () => {
    const states = ["deploying", "running"];
    vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify([{ stackId: "s", state: states.shift() }]))));
    const dep = await settle("s", { stackId: "s", state: "deploying" } as never, 0);
    expect(dep.state).toBe("running");
    vi.unstubAllGlobals();
  });
});
