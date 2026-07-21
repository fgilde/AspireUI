import { IconBrandOpenai, IconTerminal2, IconPlugConnected, IconCloud, IconSettings, IconCube, IconDatabase, IconMessages, IconReportAnalytics, IconBrandAzure, IconRouter, IconLock, IconMail, IconTable, IconBrain, IconMessageChatbot, IconPhoto, IconMusic, IconSitemap, IconMicrophone, IconBrandVscode, IconActivityHeartbeat, IconRobot, IconFileText, IconGitBranch, IconChartBar, IconRoute, IconLayoutDashboard, IconPaperclip } from "@tabler/icons-react";
import type { Icon } from "@tabler/icons-react";
import {
  siRedis, siPostgresql, siDocker, siGithub, siOllama, siN8n, siSupabase, siDotnet, siMinio,
  siMysql, siMongodb, siApachekafka, siRabbitmq, siNatsdotio, siElasticsearch, siKeycloak,
  siQdrant, siMilvus, siDapr, siOpenjdk, siSpring, siGo, siPython,
  siBun, siDeno, siRust, siNgrok, siMeilisearch, siGrafana,
} from "simple-icons";

import aspireuiLogo from "./assets/logo.svg";

type Brand = { path: string };
interface Visual { si?: Brand; tabler?: Icon; img?: string; color: string }

// "currentColor"-style adaptive tone for brands whose logo is near-black (invisible on dark themes).
const TEXT = "var(--mantine-color-text)";

const MAP: Record<string, Visual> = {
  AddAspireUI:         { img: aspireuiLogo, color: "#10B981" },
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

  AddSqlServer:        { tabler: IconDatabase, color: "#CC2927" },
  AddMySql:            { si: siMysql, color: "#4479A1" },
  AddMongoDB:          { si: siMongodb, color: "#47A248" },
  AddAzureCosmosDB:    { tabler: IconDatabase, color: "#3999D6" },
  AddValkey:           { tabler: IconDatabase, color: "#AA6CF5" },
  AddGarnet:           { tabler: IconDatabase, color: "#B23A48" },
  AddKafka:            { si: siApachekafka, color: TEXT },
  AddRabbitMQ:         { si: siRabbitmq, color: "#FF6600" },
  AddNats:             { si: siNatsdotio, color: "#27AAE1" },
  AddActiveMQ:         { tabler: IconMessages, color: "#78A22F" },
  AddActiveMQArtemis:  { tabler: IconMessages, color: "#78A22F" },
  AddElasticsearch:    { si: siElasticsearch, color: "#00BFB3" },
  AddKeycloak:         { si: siKeycloak, color: TEXT },
  AddSeq:              { tabler: IconReportAnalytics, color: "#00A9E0" },
  AddGrafana:          { si: siGrafana, color: "#F46800" },

  // Container-preset ("app") icons — keyed by the preset's `icon` field.
  localagi:            { tabler: IconRobot, color: "#0EA5E9" },
  localrecall:         { tabler: IconBrain, color: "#16A34A" },
  openwebui:           { tabler: IconMessageChatbot, color: "#10A37F" },
  comfyui:             { tabler: IconPhoto, color: "#7C3AED" },
  sdnext:              { tabler: IconPhoto, color: "#EC4899" },
  acestep:             { tabler: IconMusic, color: "#F59E0B" },
  langflow:            { tabler: IconSitemap, color: "#DC2626" },
  flowise:             { tabler: IconSitemap, color: "#14B8A6" },
  whisper:             { tabler: IconMicrophone, color: "#6366F1" },
  vscode:              { tabler: IconBrandVscode, color: "#007ACC" },
  uptimekuma:          { tabler: IconActivityHeartbeat, color: "#22C55E" },
  paperclip:           { tabler: IconPaperclip, color: "#6366F1" },
  paperless:           { tabler: IconFileText, color: "#17541F" },
  nextcloud:           { tabler: IconCloud, color: "#0082C9" },
  vaultwarden:         { tabler: IconLock, color: "#175DDC" },
  gitea:               { tabler: IconGitBranch, color: "#609926" },
  metabase:            { tabler: IconChartBar, color: "#509EE3" },
  jaeger:              { tabler: IconRoute, color: "#60D0E4" },
  homepage:            { tabler: IconLayoutDashboard, color: "#0EA5E9" },
  AddQdrant:           { si: siQdrant, color: "#DC244C" },
  AddMilvus:           { si: siMilvus, color: "#00A1EA" },
  AddDaprComponent:    { si: siDapr, color: "#4756C7" },
  AddDaprPubSub:       { si: siDapr, color: "#4756C7" },
  AddDaprStateStore:   { si: siDapr, color: "#4756C7" },
  AddJavaApp:          { si: siOpenjdk, color: TEXT },
  AddJavaContainerApp: { si: siOpenjdk, color: TEXT },
  AddSpringApp:        { si: siSpring, color: "#6DB33F" },
  AddGolangApp:        { si: siGo, color: "#00ADD8" },
  AddPythonApp:        { si: siPython, color: "#3776AB" },
  AddPythonModule:     { si: siPython, color: "#3776AB" },
  AddPythonExecutable: { si: siPython, color: "#3776AB" },
  AddUvicornApp:       { si: siPython, color: "#3776AB" },
  AddMauiProject:      { si: siDotnet, color: "#512BD4" },
  AddOracle:           { tabler: IconDatabase, color: "#F80000" },
  AddYarp:             { tabler: IconRouter, color: "#68217A" },
  AddAzureStorage:     { tabler: IconBrandAzure, color: "#3999D6" },
  AddAzureServiceBus:  { tabler: IconBrandAzure, color: "#3999D6" },
  AddAzureKeyVault:    { tabler: IconLock, color: "#3999D6" },
  AddAzureApplicationInsights: { tabler: IconBrandAzure, color: "#3999D6" },
  AddAzureOpenAI:      { tabler: IconBrandOpenai, color: "#10A37F" },
  AddMinioContainer:   { si: siMinio, color: "#C72E49" },
  AddMeilisearch:      { si: siMeilisearch, color: "#FF5CAA" },
  AddRavenDB:          { tabler: IconDatabase, color: "#CE2E28" },
  AddMailPit:          { tabler: IconMail, color: "#2AA198" },
  AddAdminer:          { tabler: IconTable, color: "#5A7FB5" },
  AddNgrok:            { si: siNgrok, color: TEXT },
  AddBunApp:           { si: siBun, color: TEXT },
  AddDenoApp:          { si: siDeno, color: TEXT },
  AddDenoTask:         { si: siDeno, color: TEXT },
  AddRustApp:          { si: siRust, color: TEXT },
};
const FALLBACK: Visual = { tabler: IconCube, color: "#7C8593" };

export function resourceVisual(addMethod: string) {
  const v = MAP[addMethod] ?? FALLBACK;
  return { color: v.color === TEXT ? "#8b98a5" : v.color };  // minimap etc. need a concrete color
}

export function ResourceGlyph({ addMethod, size = 16 }: { addMethod: string; size?: number }) {
  const v = MAP[addMethod] ?? FALLBACK;
  if (v.img) return <img src={v.img} alt="" width={size} height={size} style={{ display: "block", objectFit: "contain" }} />;
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
