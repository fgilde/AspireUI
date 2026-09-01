# App manifest — put your own app in the store

An AspireUI app is one JSON object. The same object works in three places:

| Where | What it does |
|-------|--------------|
| `aspireui-app.json` in **your own repository** | **Install from Git** finds it and installs the app exactly as you defined it — no detour through your `docker-compose.yml`, nothing for us to merge |
| any URL, added as an **app source** | the apps behind that URL show up in the store of every instance that added it — see [App sources](#app-sources) |
| `catalog/presets/community/<id>.json` in a **pull request** to AspireUI | your app ships in the store for everyone |
| any `*.json` in `EXTRA_PRESETS_DIR` | private apps on your own instance, no fork needed |

Point your editor at the schema for completion and validation:

```json
{
  "$schema": "https://raw.githubusercontent.com/fgilde/AspireUI/master/src/AspireUI.Server/catalog/presets/aspireui-app.schema.json",
  "id": "my-app",
  "label": "My App",
  "group": "Tools",
  "image": "ghcr.io/acme/my-app:1.4.0",
  "port": 8080,
  "description": "What it is, and how the first login works.",
  "volumes": [["data", "/app/data"]],
  "params": [
    { "key": "app-secret", "env": "APP_SECRET", "default": "", "secret": true }
  ],
  "website": "https://example.com",
  "github": "https://github.com/acme/my-app",
  "license": "MIT",
  "submitter": "acme",
  "source": "https://github.com/acme/my-app"
}
```

That is a complete app: AspireUI generates the Aspire resource, publishes a free host port, creates
the volume, generates a 48-character `APP_SECRET` at install time and shows it in the install dialog.

## The fields that matter

- **`id`** — lowercase kebab-case, stable. It names the resource and prefixes the volumes, so
  changing it later orphans the user's data.
- **`port`** — the port **inside** the container. Hosting allocates a free host port (20000–29999)
  and maps it. Only set `"fixedPort": true` if the app truly cannot live behind another port.
- **`env`** — literal values, as `[["KEY", "value"], …]`.
- **`params`** — values the *user* owns: secrets, keys, the public URL. They show up in the install
  dialog. A `"secret": true` param with an empty `default` gets a fresh random value per install
  (never ship a real secret as a default). Outside the store dialog a param becomes an Aspire
  `AddParameter` resource, so it stays visible and editable in the builder.
  Two fields for the awkward cases:
  `"generate": false` marks a secret **only the provider can hand out** — an installation key, an API
  token, an SMTP password. The dialog then asks for it instead of inventing a value that looks right
  and breaks the app. `"hint"` is the one line under the field that says what it is and where to get
  it, e.g. `"From https://bitwarden.com/host (free, one form)."`.
- **`volumes`** — `[["name", "/container/path"], …]`, one named volume per entry, prefixed with the
  resource name. Anything the user would be upset to lose belongs here: databases, uploads,
  generated keys, config. Avoid `bindMounts` in submitted apps — host paths don't exist elsewhere.
- **`companions`** — the services your app needs. Reference them from `env` with `${key}`:

```json
{
  "id": "my-app",
  "label": "My App",
  "group": "Tools",
  "image": "ghcr.io/acme/my-app:1.4.0",
  "port": 8080,
  "env": [
    ["DB_HOST", "${db}"],
    ["DB_NAME", "myapp"],
    ["DB_USER", "myapp"],
    ["DB_PASSWORD", "myapp"]
  ],
  "companions": [
    {
      "key": "db",
      "addMethod": "AddContainer",
      "resourceName": "my-app-db",
      "image": "lscr.io/linuxserver/mariadb:latest",
      "role": "mysql",
      "env": [
        ["MYSQL_ROOT_PASSWORD", "myapp"],
        ["MYSQL_DATABASE", "myapp"],
        ["MYSQL_USER", "myapp"],
        ["MYSQL_PASSWORD", "myapp"]
      ]
    }
  ],
  "volumes": [["data", "/app/data"]]
}
```

`role` (`postgres`, `mysql`, `mariadb`, `mongo`, `redis`, `meilisearch`, `llm`) lets AspireUI offer a
matching resource that is already on the canvas instead of starting a second one.

- **Store presentation** — `icon` (a slug from
  [dashboard-icons](https://github.com/homarr-labs/dashboard-icons), e.g. `nextcloud`), `logo`,
  `card`, `screenshots` (URLs), `tags`, `website`, `github`, `license`, `language`.
- **Provenance** — `submitter` and `source` are shown in the details dialog, so users can see where
  an app came from.

A file may also be an **array** of app objects — handy for a small collection in one file.

## Installing straight from a repository

With `aspireui-app.json` committed, anyone can run **Install from Store → From Git** (or
*Import → Git repository* in the builder), paste your repository URL, and AspireUI offers your
manifest as the way in — next to the compose file or Aspire AppHost it may also find. Your defaults,
your volumes, your secrets, nothing rewritten.

## App sources

Anything reachable over http(s) that returns app JSON can be an **app source** — a single app, or an
array of them as your own little store. A raw file in a Git repository is enough:

```
https://raw.githubusercontent.com/acme/apps/main/apps.json
```

An admin adds it under **Settings → Hosting → App sources** (name + URL). AspireUI fetches it once,
right there, and tells you how many apps it found. Those apps then sit in the store next to the
built-ins, each marked **Community** with your name and URL as provenance.

Rules that keep this boring and safe:

- **Admins only.** Adding and removing sources is an admin action.
- **Manual refresh.** A source is fetched when it is added and when someone presses *Refresh
  sources* — never on a timer, never in the background. What is installable today stays installable
  tomorrow unless an admin asks for the update.
- **Cached on disk.** Every fetch is written to `<workspace>/_appsources/<id>.json`, so the store
  works offline and a source that goes away doesn't empty your store.
- **Bounded.** 20-second timeout, responses over 2 MB are rejected, only `http`/`https` URLs, and a
  broken or unreachable source shows its error in the table instead of failing the store.
- Admins can still hide individual apps from the store with the eye toggle on the app card.

If you maintain several apps, one array file is the least work for everyone: you push, your users
press Refresh.

## Submitting to the store

1. Fork AspireUI, add `src/AspireUI.Server/catalog/presets/community/<id>.json`.
2. Fill in `submitter` and `source`.
3. `dotnet test tests/AspireUI.Server.Tests` — the preset test validates every app: required fields,
   that each `${key}` resolves to a companion, and that the resource builds.
4. Open a pull request. Keep it to one app per pull request.

Rules of thumb for a merge: the image is publicly pullable with a real tag, the app opens to a
usable web UI, persistent state is on named volumes, secrets are generated rather than shipped, and
the description tells a new user what happens on first login.

## What this is not

A manifest starts a container image, which means running someone else's code on your host. AspireUI
shows you the image, ports and volumes before installing, and an admin can hide any app from the
store — but nobody scans the image for you. Install apps whose source you trust, the same way you'd
treat a `docker run` line from the internet.
