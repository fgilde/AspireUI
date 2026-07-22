import { IconBrandOpenai, IconTerminal2, IconPlugConnected, IconCloud, IconSettings, IconCube, IconDatabase, IconMessages, IconReportAnalytics, IconBrandAzure, IconRouter, IconLock, IconMail, IconTable, IconBrain, IconMessageChatbot, IconPhoto, IconMusic, IconSitemap, IconMicrophone, IconBrandVscode, IconActivityHeartbeat, IconRobot, IconLayoutDashboard, IconPaperclip, IconShare, IconFileTypePdf, IconTool, IconBell, IconBrandDocker, IconStack3, IconHeadphones, IconShieldCheck, IconServer2, IconChecklist, IconNote, IconSchema, IconSearch } from "@tabler/icons-react";
import type { Icon } from "@tabler/icons-react";
import {
  siRedis, siPostgresql, siDocker, siGithub, siOllama, siN8n, siSupabase, siDotnet, siMinio,
  siMysql, siMongodb, siApachekafka, siRabbitmq, siNatsdotio, siElasticsearch, siKeycloak,
  siQdrant, siMilvus, siDapr, siOpenjdk, siSpring, siGo, siPython,
  siBun, siDeno, siRust, siNgrok, siMeilisearch, siGrafana,
  siJellyfin, siGitea, siNextcloud, siHomeassistant, siSonarr, siRadarr, siQbittorrent,
  siImmich, siPihole, siExcalidraw, siSearxng, siPaperlessngx, siVaultwarden, siMetabase,
  siLangflow, siNodered, siJaeger, siOnlyoffice, siActualbudget, siFreshrss,
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
  flowise:             { tabler: IconSitemap, color: "#14B8A6" },
  whisper:             { tabler: IconMicrophone, color: "#6366F1" },
  vscode:              { tabler: IconBrandVscode, color: "#007ACC" },
  uptimekuma:          { tabler: IconActivityHeartbeat, color: "#22C55E" },
  paperclip:           { tabler: IconPaperclip, color: "#6366F1" },
  homepage:            { tabler: IconLayoutDashboard, color: "#0EA5E9" },
  gokapi:              { tabler: IconShare, color: "#2B6CB0" },
  stirlingpdf:         { tabler: IconFileTypePdf, color: "#E53E3E" },
  ittools:             { tabler: IconTool, color: "#4A5568" },
  ntfy:                { tabler: IconBell, color: "#16A34A" },
  dockge:              { tabler: IconStack3, color: "#06B6D4" },
  dozzle:              { tabler: IconBrandDocker, color: "#2496ED" },
  jellyfin:            { si: siJellyfin, color: "#00A4DC" },
  navidrome:           { tabler: IconMusic, color: "#2B9CE5" },
  audiobookshelf:      { tabler: IconHeadphones, color: "#F97316" },
  photoprism:          { tabler: IconPhoto, color: "#00A6A6" },
  sonarr:              { si: siSonarr, color: "#2596BE" },
  radarr:              { si: siRadarr, color: "#FFC230" },
  prowlarr:            { tabler: IconSearch, color: "#E56E2C" },
  qbittorrent:         { si: siQbittorrent, color: "#2F67BA" },
  pihole:              { si: siPihole, color: "#96060C" },
  adguard:             { tabler: IconShieldCheck, color: "#68BC71" },
  npm:                 { tabler: IconServer2, color: "#F15A2B" },
  homeassistant:       { si: siHomeassistant, color: "#18BCF2" },
  nodered:             { si: siNodered, color: "#8F0000" },
  vikunja:             { tabler: IconChecklist, color: "#1973FF" },
  memos:               { tabler: IconNote, color: "#4CA57D" },
  excalidraw:          { si: siExcalidraw, color: "#6965DB" },
  drawio:              { tabler: IconSchema, color: "#F08705" },
  anythingllm:         { tabler: IconMessageChatbot, color: "#3B82F6" },
  librechat:           { tabler: IconMessages, color: "#10A37F" },
  searxng:             { si: siSearxng, color: "#3050FF" },
  gitea:               { si: siGitea, color: "#609926" },
  nextcloud:           { si: siNextcloud, color: "#0082C9" },
  immich:              { si: siImmich, color: "#4250AF" },
  paperless:           { si: siPaperlessngx, color: "#17541F" },
  vaultwarden:         { si: siVaultwarden, color: TEXT },
  metabase:            { si: siMetabase, color: "#509EE3" },
  langflow:            { si: siLangflow, color: TEXT },
  jaeger:              { si: siJaeger, color: "#66CFE3" },
  onlyoffice:          { si: siOnlyoffice, color: "#444444" },
  actual:              { si: siActualbudget, color: "#6B46C1" },
  freshrss:            { si: siFreshrss, color: "#0062BE" },
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

export function ResourceGlyph({ addMethod, iconKey, size = 16 }: { addMethod: string; iconKey?: string | null; size?: number }) {
  const v = (iconKey && MAP[iconKey]) || MAP[addMethod] || FALLBACK;
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
