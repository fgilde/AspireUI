import { describe, expect, it } from "vitest";
import { callParams, curlFor, unmask } from "./curl";

describe("curlFor", () => {
  it("has no body when nothing comes from the call", () => {
    expect(curlFor("https://h.example", "/api/hook/abc", [{ key: "A", mode: "fixed", value: "1" }]))
      .toBe('curl -X POST "https://h.example/api/hook/abc"');
  });
  it("puts required keys in the body and names optional ones", () => {
    expect(curlFor("https://h.example", "/api/hook/abc", [
      { key: "branch", mode: "required" }, { key: "LEVEL", mode: "optional", value: "info" }, { key: "S", mode: "generated" },
    ])).toBe([
      'curl -X POST "https://h.example/api/hook/abc" \\',
      '  -H "content-type: application/json" \\',
      `  -d '{"branch":"<branch>"}'`,
      "# optional: LEVEL (default: info)",
    ].join("\n"));
  });
  it("lists only parameters that come from the call", () => {
    expect(callParams([{ key: "a", mode: "fixed" }, { key: "b", mode: "required" }, { key: "c", mode: "optional" }, { key: "d", mode: "generated" }])
      .map(p => p.key)).toEqual(["b", "c"]);
  });
});

describe("unmask", () => {
  it("drops the mask when typing after a stored secret", () => {
    expect(unmask("••••abc")).toBe("abc");
    expect(unmask("••••")).toBe("••••");
    expect(unmask("plain")).toBe("plain");
  });
});
