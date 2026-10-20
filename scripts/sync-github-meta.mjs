// Refreshes stars, license, language and topics of every store preset that names a GitHub repo,
// from the GitHub API, and writes container-presets.json back in its own format. Run by
// .github/workflows/store-metadata.yml every week; locally:
//   GITHUB_TOKEN=$(gh auth token) node scripts/sync-github-meta.mjs
// Without a token GitHub allows 60 requests an hour, which is fewer than the store has apps.
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";

const FILE = path.resolve(fileURLToPath(import.meta.url), "../../src/AspireUI.Server/catalog/presets/container-presets.json");
const headers = { Accept: "application/vnd.github+json", "User-Agent": "aspireui-store-metadata" };
if (process.env.GITHUB_TOKEN) headers.Authorization = `Bearer ${process.env.GITHUB_TOKEN}`;

/** "https://github.com/owner/repo[/...]" to "owner/repo", anything else to null. */
export function repoOf(url) {
  const m = /^https?:\/\/github\.com\/([^/\s]+)\/([^/\s#?]+)/i.exec(url ?? "");
  return m ? `${m[1]}/${m[2].replace(/\.git$/, "")}` : null;
}

/** Applies one /repos response to a preset; returns true when something changed. */
export function apply(preset, repo) {
  const before = JSON.stringify(preset);
  preset.stars = repo.stargazers_count;
  // GitHub says NOASSERTION for licenses it does not recognise (Elastic-2.0, for one), and it
  // reports one license where a project has two ("GPL-3.0 / Bitwarden License"). In both cases
  // the value typed into the preset says more, so it stays.
  const lic = repo.license;
  const dual = preset.license?.includes(" / ");
  if (lic?.spdx_id && lic.spdx_id !== "NOASSERTION" && !dual) preset.license = `${lic.name} (${lic.spdx_id})`;
  if (repo.language) preset.language = repo.language;
  if (repo.topics?.length) preset.topics = repo.topics;
  return JSON.stringify(preset) !== before;
}

async function main() {
  const presets = JSON.parse(fs.readFileSync(FILE, "utf8"));
  let changed = 0, failed = 0;
  for (const p of presets) {
    const name = repoOf(p.github);
    if (!name) continue;
    const res = await fetch(`https://api.github.com/repos/${name}`, { headers });
    if (!res.ok) {
      // Gone, renamed into nothing or private: keep what the preset says.
      console.warn(`${p.id}: ${name} -> HTTP ${res.status}`);
      failed++;
      if (res.status === 403 || res.status === 429) break; // rate limit, the rest would fail too
      continue;
    }
    if (apply(p, await res.json())) changed++;
  }
  fs.writeFileSync(FILE, JSON.stringify(presets, null, 2) + "\n");
  console.log(`${changed} presets updated, ${failed} not reachable`);
}

if (process.argv[1] === fileURLToPath(import.meta.url)) await main();
