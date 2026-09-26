import type { HookParam } from "../api";

export const callParams = (params?: HookParam[] | null): HookParam[] =>
  (params ?? []).filter(p => p.mode === "required" || p.mode === "optional");

export function curlFor(origin: string, webhookPath: string, params?: HookParam[] | null): string {
  const url = `curl -X POST "${origin}${webhookPath}"`;
  const required = (params ?? []).filter(p => p.mode === "required");
  const optional = (params ?? []).filter(p => p.mode === "optional");
  const lines = required.length === 0 ? [url] : [
    `${url} \\`,
    '  -H "content-type: application/json" \\',
    `  -d '${JSON.stringify(Object.fromEntries(required.map(p => [p.key, `<${p.key}>`])))}'`,
  ];
  if (optional.length > 0)
    lines.push(`# optional: ${optional.map(p => p.value ? `${p.key} (default: ${p.secret ? "••••" : p.value})` : p.key).join(", ")}`);
  return lines.join("\n");
}
