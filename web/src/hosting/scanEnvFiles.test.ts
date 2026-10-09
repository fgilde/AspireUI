import { describe, it, expect } from "vitest";
import { scanEnvFiles } from "./GitImportModal";

describe("scanEnvFiles", () => {
  const compose = ["services:\n  app:\n    env_file: .env\n    environment:\n      DATABASE_URL: x\n      PW: ${POSTGRES_PASSWORD}\n"];
  const files = [
    { path: ".env.example", content: "DATABASE_URL=a\nAUTH_SECRET=change-me\nAPP_URL=http://localhost:3000\nPOSTGRES_PASSWORD=\n" },
    { path: ".env.test", content: "E2E=1\n" },
  ];

  it("offers the env_file keys compose does not set itself, with a fresh secret", () => {
    const vars = scanEnvFiles(compose, files, [{ name: "POSTGRES_PASSWORD", def: "", secret: true }]);
    expect(vars.map(v => v.name)).toEqual(["AUTH_SECRET", "APP_URL"]);
    expect(vars[0].def).not.toBe("change-me");
    expect(vars[0].def).toHaveLength(48);
    expect(vars[1].def).toBe("http://localhost:3000");
  });

  it("offers nothing without env_file", () => {
    expect(scanEnvFiles(["services:\n  app:\n    image: x\n"], files, [])).toEqual([]);
  });
});
