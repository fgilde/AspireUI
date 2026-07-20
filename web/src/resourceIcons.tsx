import { IconBrandOpenai, IconTerminal2, IconPlugConnected, IconCloud, IconSettings, IconCube } from "@tabler/icons-react";
import type { Icon } from "@tabler/icons-react";
import { siRedis, siPostgresql, siDocker, siGithub, siOllama, siN8n, siSupabase, siDotnet, siMinio } from "simple-icons";

type Brand = { path: string };
interface Visual { si?: Brand; tabler?: Icon; color: string }

// "currentColor"-style adaptive tone for brands whose logo is near-black (invisible on dark themes).
const TEXT = "var(--mantine-color-text)";

const MAP: Record<string, Visual> = {
  AddRedis:            { si: siRedis, color: "#FF4438" },
  AddPostgres:         { si: siPostgresql, color: "#4169E1" },
  AddContainer:        { si: siDocker, color: "#2496ED" },
  AddDockerfile:       { si: siDocker, color: "#2496ED" },
  AddGithubRepository: { si: siGithub, color: TEXT },
  AddOllama:           { si: siOllama, color: TEXT },
  AddLocalAI:          { tabler: IconBrandOpenai, color: "#10A37F" },
  AddN8n:              { si: siN8n, color: "#EA4B71" },
  AddSupabase:         { si: siSupabase, color: "#3FCF8E" },
  AddProject:          { si: siDotnet, color: "#512BD4" },
  AddCSharpApp:        { si: siDotnet, color: "#512BD4" },
  AddDotnetTool:       { si: siDotnet, color: "#512BD4" },
  AddExecutable:       { tabler: IconTerminal2, color: "#64748B" },
  AddExternalService:  { tabler: IconCloud, color: "#0891B2" },
  AddParameter:        { tabler: IconSettings, color: "#64748B" },
  AddConnectionString: { tabler: IconPlugConnected, color: "#64748B" },
  AddMinioS3OnNfs:     { si: siMinio, color: "#C72E49" },
};
const FALLBACK: Visual = { tabler: IconCube, color: "#7C8593" };

export function resourceVisual(addMethod: string) {
  const v = MAP[addMethod] ?? FALLBACK;
  return { color: v.color === TEXT ? "#8b98a5" : v.color };  // minimap etc. need a concrete color
}

export function ResourceGlyph({ addMethod, size = 16 }: { addMethod: string; size?: number }) {
  const v = MAP[addMethod] ?? FALLBACK;
  if (v.si) {
    return (
      <svg role="img" viewBox="0 0 24 24" width={size} height={size} fill={v.color} style={{ display: "block" }}>
        <path d={v.si.path} />
      </svg>
    );
  }
  const T = v.tabler ?? IconCube;
  return <T size={size} style={{ color: v.color }} />;
}
