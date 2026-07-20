import {
  IconDatabase, IconBrandDocker, IconBrandGithub, IconRobot, IconBrandOpenai,
  IconBolt, IconBox, IconTerminal2, IconPlugConnected, IconCloud, IconSettings, IconCube,
} from "@tabler/icons-react";
import type { Icon } from "@tabler/icons-react";

// AddMethod -> a distinctive icon + brand-ish color, for nodes / palette / property header.
// Redis has no tabler brand icon, so it reuses the DB icon with the Redis red.
const MAP: Record<string, { Icon: Icon; color: string }> = {
  AddRedis:            { Icon: IconDatabase, color: "#D82C20" },
  AddPostgres:         { Icon: IconDatabase, color: "#336791" },
  AddContainer:        { Icon: IconBrandDocker, color: "#2496ED" },
  AddDockerfile:       { Icon: IconBrandDocker, color: "#2496ED" },
  AddGithubRepository: { Icon: IconBrandGithub, color: "#6e5494" },
  AddOllama:           { Icon: IconRobot, color: "#0EA5E9" },
  AddLocalAI:          { Icon: IconBrandOpenai, color: "#10A37F" },
  AddN8n:              { Icon: IconPlugConnected, color: "#EA4B71" },
  AddSupabase:         { Icon: IconBolt, color: "#3ECF8E" },
  AddProject:          { Icon: IconBox, color: "#512BD4" },
  AddCSharpApp:        { Icon: IconBox, color: "#512BD4" },
  AddDotnetTool:       { Icon: IconTerminal2, color: "#512BD4" },
  AddExecutable:       { Icon: IconTerminal2, color: "#64748B" },
  AddExternalService:  { Icon: IconCloud, color: "#0891B2" },
  AddParameter:        { Icon: IconSettings, color: "#64748B" },
  AddConnectionString: { Icon: IconPlugConnected, color: "#64748B" },
  AddMinioS3OnNfs:     { Icon: IconDatabase, color: "#C72E49" },
};

const FALLBACK = { Icon: IconCube, color: "#7C8593" };

export function resourceVisual(addMethod: string) {
  return MAP[addMethod] ?? FALLBACK;
}
