// node --test scripts/sync-github-meta.test.mjs
import test from "node:test";
import assert from "node:assert/strict";
import { repoOf, apply } from "./sync-github-meta.mjs";

test("repoOf takes owner/repo from GitHub URLs only", () => {
  assert.equal(repoOf("https://github.com/fgilde/hausverwaltung"), "fgilde/hausverwaltung");
  assert.equal(repoOf("https://github.com/a/b.git"), "a/b");
  assert.equal(repoOf("https://github.com/a/b/tree/main/x"), "a/b");
  assert.equal(repoOf("https://example.com/a/b"), null);
});

test("apply takes GitHub's license, but not NOASSERTION over a typed-in one", () => {
  const wrong = { license: "MIT" };
  apply(wrong, { stargazers_count: 1, license: { name: "GNU Affero General Public License v3.0", spdx_id: "AGPL-3.0" } });
  assert.equal(wrong.license, "GNU Affero General Public License v3.0 (AGPL-3.0)");

  const typed = { license: "Elastic License 2.0 (Elastic-2.0)", topics: ["a"] };
  const changed = apply(typed, { stargazers_count: 3, license: { name: "Other", spdx_id: "NOASSERTION" }, language: null, topics: [] });
  assert.equal(typed.license, "Elastic License 2.0 (Elastic-2.0)");
  assert.deepEqual(typed.topics, ["a"]);
  assert.equal(typed.stars, 3);
  assert.equal(changed, true);

  const dual = { license: "GPL-3.0 / Bitwarden License" };
  apply(dual, { stargazers_count: 1, license: { name: "GNU General Public License v3.0", spdx_id: "GPL-3.0" } });
  assert.equal(dual.license, "GPL-3.0 / Bitwarden License");

  const none = {};
  apply(none, { stargazers_count: 1, license: { name: "Other", spdx_id: "NOASSERTION" } });
  assert.equal(none.license, undefined);
});
