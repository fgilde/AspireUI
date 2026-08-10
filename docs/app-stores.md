# AspireUI in other app stores (Umbrel, Unraid)

AspireUI ships the packaging files for **Umbrel** and **Unraid Community Applications** in this
repository, so this repo *is* both an Umbrel community app store and an Unraid CA template repo.

| File | Store |
|------|-------|
| `umbrel-app-store.yml` | Umbrel community store manifest (`id: fgilde`) |
| `fgilde-aspireui/umbrel-app.yml` | Umbrel app listing |
| `fgilde-aspireui/docker-compose.yml` | Umbrel app services (`app_proxy` + `server`) |
| `ca_profile.xml` | Unraid CA repository profile |
| `templates/aspireui.xml` | Unraid CA container template |

## Umbrel — install it today (community store)

No approval needed. In umbrelOS: **App Store → ⋯ → Community App Stores → Add**, paste

```
https://github.com/fgilde/AspireUI
```

then install **AspireUI** from the new "AspireUI App Store" section. umbrelOS opens it on port
`5158`; the admin user is `umbrel` with the password umbrelOS shows on the app page
(`deterministicPassword: true` → Umbrel's per-install `APP_PASSWORD`).

App data lives in `~/umbrel/app-data/fgilde-aspireui/data` and is included in Umbrel backups.

### Submitting to the official Umbrel App Store

The official store is a PR against [getumbrel/umbrel-apps](https://github.com/getumbrel/umbrel-apps)
with a top-level `aspireui/` directory. Derive it from `fgilde-aspireui/`:

1. Copy the folder as `aspireui/`, set `id: aspireui`, drop the `icon:` line (official assets are
   hosted by Umbrel), keep `gallery: []`, and set `submission:` to the PR URL.
2. In `docker-compose.yml`, change `APP_HOST` to `aspireui_server_1`.
3. Pin the image digest of the release you submit and bump `version:`.
4. Run their linter: `npm run lint:apps -- aspireui --check-images`.
5. PR body: upstream URL, image source, testing done, and a justification for the Docker socket.

**Two known blockers** for the official store (both fine for the community store above):

- **`linux/arm64` image.** The store requires amd64 **and** arm64; our image is amd64 only, because
  `Grpc.Tools` 2.80's `protoc` segfaults on arm64 builders (arm64 is disabled in
  `.github/workflows/docker-publish.yml`). Fix that build, then re-enable the platform.
- **Host Docker socket.** Umbrel's packaging rules say App Store packages must not mount
  `/var/run/docker.sock`. AspireUI's Run/Hosting features *are* Docker orchestration, so this needs an
  explicit exception from the Umbrel team — ask before investing in the PR.

## Unraid Community Applications

Templates are already in this repo, so the submission is just pointing CA at it:

1. Open [ca.unraid.net/submit](https://ca.unraid.net/submit) and sign in.
2. Repository URL: `https://github.com/fgilde/AspireUI`, then **Validate** and **Scan** — the scanner
   reads `ca_profile.xml` plus `templates/aspireui.xml`.
3. Submit and wait for moderation.

**Missing prerequisite:** CA requires an **OSI-approved `LICENSE` file at the repository root**, and
this repo has none yet. Add one (e.g. MIT or Apache-2.0) before submitting, and put the same value in
`templates/aspireui.xml` as `<License>`.

Until then, users can install the template manually: Unraid **Docker → Add Container → Template**
field accepts the raw template URL

```
https://raw.githubusercontent.com/fgilde/AspireUI/master/templates/aspireui.xml
```

## Notes for both stores

- The Docker socket mapping is required, not optional: without it Run and Hosting cannot start
  containers. It is root-equivalent host access — keep AspireUI off the public internet.
- Hosted apps get host ports from AspireUI's own range (20000–29999, occupied ports are skipped), so
  they do not collide with Umbrel/Unraid app ports.
- Set `ASPIREUI_SET_PublicHost` to the device hostname (e.g. `umbrel.local`, `tower.local`) so links
  to hosted apps point at the server instead of the container.
- Bump `version:` in `fgilde-aspireui/umbrel-app.yml` and the pinned digest in its `docker-compose.yml`
  whenever a new image is published; Unraid follows the `:latest` tag automatically.
