# App manifest — put your own app in the store

An AspireUI app is one JSON object. The same object works in three places:

| Where | What it does |
|-------|--------------|
| `aspireui-app.json` in **your own repository** | **Install from Git** finds it and installs the app exactly as you defined it — no detour through your `docker-compose.yml`, nothing for us to merge |
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
