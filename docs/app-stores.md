# AspireUI in other app stores (Umbrel, Unraid, CasaOS, Cosmos)

AspireUI ships the packaging for four self-hosting platforms in this repository, so the repo *is* an
Umbrel community app store, an Unraid CA template repo, a CasaOS app source and a ready-to-PR Cosmos
servapp.

| File | Store |
|------|-------|
| `umbrel-app-store.yml` | Umbrel community store manifest (`id: fgilde`) |
| `fgilde-aspireui/umbrel-app.yml` | Umbrel app listing |
| `fgilde-aspireui/docker-compose.yml` | Umbrel app services (`app_proxy` + `server`) |
| `ca_profile.xml` | Unraid CA repository profile |
| `templates/aspireui.xml` | Unraid CA container template |
| `store/casaos/Apps/AspireUI/docker-compose.yml` | CasaOS app (compose + `x-casaos` metadata) |
| `store/casaos/*.json` | CasaOS source index (categories, featured, recommended) |
| `store/cosmos/servapps/AspireUI/` | Cosmos servapp (`cosmos-compose.json`, description, images) |

Where each one stands, and what is left to do:

| Store | Installable today | Listed in the official store |
|---|---|---|
| **Umbrel** | ✅ community app store (add this repo's URL) | ❌ blocked: their rules forbid the Docker socket |
| **Unraid CA** | ✅ template URL by hand | ⏳ one submission form, then moderation |
| **CasaOS** | ✅ custom app source (zip URL) | ⏳ PR to `IceWhaleTech/CasaOS-AppStore` |
| **Cosmos** | ✅ compose import by hand | ⏳ PR to `azukaar/cosmos-servapps-official` |

`StoreListingTests` and `StorePackagingTests` keep all of these files valid — they are hand-written
files that nothing compiles, so a typo would otherwise only surface as a rejected submission.

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

**One known blocker** for the official store (it does not affect the community store above):

- **Host Docker socket.** Umbrel's packaging rules say App Store packages must not mount
  `/var/run/docker.sock`. AspireUI's Run/Hosting features *are* Docker orchestration, so this needs an
  explicit exception from the Umbrel team — ask before investing in the PR.

The other store requirement, a **multi-arch image**, is met: `linux/amd64` and `linux/arm64` are built
by `.github/workflows/docker-publish.yml`. The trick is in the `Dockerfile` — the build stage is pinned
to `$BUILDPLATFORM` (the `protoc` shipped with `Grpc.Tools` 2.80 segfaults on arm64, emulated or not)
and only the runtime stage is built per target arch, which works because the publish output is portable
IL. Two arch-dependent downloads in that stage follow `TARGETARCH`: the Compose plugin binary and the
Aspire CLI (taken from the RID-specific NuGet package, since `dotnet tool install` aborts under QEMU).

A `smoke-arm64` job then starts the published arm64 image on a **native** `ubuntu-24.04-arm` runner and
checks that it serves and that `aspire`, `docker compose` and `dotnet` work — QEMU can't prove that,
because the emulated .NET runtime aborts on any non-trivial app.

## Unraid Community Applications

Templates are already in this repo, so the submission is just pointing CA at it:

1. Open [ca.unraid.net/submit](https://ca.unraid.net/submit) and sign in.
2. Repository URL: `https://github.com/fgilde/AspireUI`, then **Validate** and **Scan** — the scanner
   reads `ca_profile.xml` plus `templates/aspireui.xml`.
3. Submit and wait for moderation.

CA's prerequisites are in place: the repo is public, has an OSI-approved `LICENSE` (AGPL-3.0, also
declared as `<License>` in the template), a non-empty `<Profile>` in `ca_profile.xml`, and a valid
`<Container version="2">` template with `<Repository>`.

Users can also install the template manually without CA: Unraid **Docker → Add Container → Template**
field accepts the raw template URL

```
https://raw.githubusercontent.com/fgilde/AspireUI/master/templates/aspireui.xml
```

## CasaOS

CasaOS reads an app source as a zip whose root is a `build/sysroot/var/lib/casaos/appstore/default.new`
tree (that is what IceWhale's own store CI produces). `.github/workflows/docker-publish.yml` builds
exactly that from `store/casaos/` on every push to master and keeps it at a fixed release tag, so the
URL never changes:

```
https://github.com/fgilde/AspireUI/releases/download/store/casaos-appstore.zip
```

**Install it today:** CasaOS **App Store → the ⋯ menu → Add source**, paste that URL, then install
AspireUI from the *Developer* category. The app opens on port `5158`, stores its data under
`/DATA/AppData/aspireui/data`, and gets `ASPIREUI_SET_PublicHost` pointed at the device so links to
hosted apps work from other machines.

**Submitting to the official CasaOS store:** a PR against
[IceWhaleTech/CasaOS-AppStore](https://github.com/IceWhaleTech/CasaOS-AppStore) that adds
`Apps/AspireUI/`. Their CI validates the compose file and the `x-casaos` block.

1. Copy `store/casaos/Apps/AspireUI/docker-compose.yml` into their `Apps/AspireUI/`.
2. Add the image files their layout expects next to it — `icon.png` (and `icon.svg` if available),
   `thumbnail.png`, `screenshot-1..3.png` — and repoint `icon`, `thumbnail` and `screenshot_link` at
   `https://cdn.jsdelivr.net/gh/IceWhaleTech/CasaOS-AppStore@main/Apps/AspireUI/…`.
3. Pin `version:` in `x-casaos` to the released AspireUI version and replace the `:latest` tag with it.
4. Mention the Docker socket in the PR: CasaOS lists container managers (Portainer, Dockge), but the
   reviewer should not have to discover that mount on their own.

## Cosmos

`store/cosmos/servapps/AspireUI/` is a complete Cosmos servapp: `cosmos-compose.json` (with an
installer form for the optional admin account, a `SERVAPP` route to port 8080, a named `/data` volume
and the Docker socket bind), `description.json`, `icon.png` and three screenshots.

**Install it today** without waiting for the market: Cosmos **ServApps → Create → Import compose
file**, and paste the contents of `cosmos-compose.json` (Cosmos resolves the `{ServiceName}` and
`{if Context.…}` placeholders as you fill the form).

**Submitting to the official market:** a PR against
[azukaar/cosmos-servapps-official](https://github.com/azukaar/cosmos-servapps-official) that adds
`servapps/AspireUI/` — copy the folder as it is; the icon URL in the `cosmos-icon` label already points
at where the file will live once merged (`azukaar.github.io/cosmos-servapps-official/servapps/AspireUI/icon.png`).

## Notes for all four stores

- The Docker socket mapping is required, not optional: without it Run and Hosting cannot start
  containers. It is root-equivalent host access — keep AspireUI off the public internet.
- Hosted apps get host ports from AspireUI's own range (20000–29999, occupied ports are skipped), so
  they do not collide with Umbrel/Unraid app ports.
- Set `ASPIREUI_SET_PublicHost` to the device hostname (e.g. `umbrel.local`, `tower.local`) so links
  to hosted apps point at the server instead of the container.
- Bump `version:` in `fgilde-aspireui/umbrel-app.yml` and the pinned digest in its `docker-compose.yml`
  whenever a new image is published; Unraid follows the `:latest` tag automatically.
