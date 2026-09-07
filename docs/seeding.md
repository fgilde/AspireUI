# Seeding an install

Everything a fresh install needs can be handed to it at start: accounts, deploy targets, api tokens,
store sources, settings, stacks and apps. Two ways in, one model behind both — a **seed file** for
anything structured, and **environment variables** for the short forms, because a compose file or an
Aspire AppHost passes env vars, not documents.

Seeding is **idempotent by name**. An account, target, token, source or stack that is already there
is left exactly as it is, so restarting with the same configuration changes nothing and adding one
entry adds exactly that one.

## The short forms

| Variable | Meaning |
| --- | --- |
| `ASPIREUI_USERS` | Accounts: `name:password[:permissions]`, `;`-separated |
| `ASPIREUI_TARGETS` | Deploy targets: `name=uri`, `;`-separated |
| `ASPIREUI_API_TOKENS` | Bearer tokens: `name:username:token`, `;`-separated |
| `ASPIREUI_APP_SOURCES` | Store sources: `name=url`, `;`-separated |
| `ASPIREUI_SEED_APPS` | Apps from the catalog: `id[=name]`, `,`/`;`-separated |
| `ASPIREUI_SEED_DIR` | Directories (or compose files) to import as stacks, `;`-separated |
| `ASPIREUI_SEED_GIT` | Repositories to import as stacks: `url[#branch][\|subdir]`, `;`-separated |
| `ASPIREUI_SEED_FILE` | A seed file, or a directory holding `aspireui.seed.json` |
| `ASPIREUI_SEED` | A whole seed document inline, as JSON — what the Aspire integration writes |
| `ASPIREUI_SEED_DEPLOY` | `true` = deploy the seeded stacks and apps once hosting is up |

The older `ASPIREUI_ADMIN_USERNAME` / `ASPIREUI_ADMIN_PASSWORD` (first admin, first run only),
`ASPIREUI_SEED_STACK_NAME` / `ASPIREUI_SEED_STACK_PROJECTS` and `ASPIREUI_SET_<Key>` all still work
and can be mixed with these.

### Users

```
ASPIREUI_USERS=boss:bosspassword:admin;ops:opspassword:operator;kim:kimpassword:deploy,files
```

The third field is `admin`, a preset (`all`, `operator`, `app-user`, `viewer`, `none`, `default`) or
a comma-separated list of [permission ids](users-and-permissions.md). Left out, the account gets the
default set (builder, install, configure). A password containing a colon needs the JSON form:

```
ASPIREUI_USERS=[{"username":"kim","password":"pa:ss","permissions":["files"],"viewModes":["simple"]}]
```

### Deploy targets

One URI per target — the scheme picks the kind:

```
ASPIREUI_TARGETS=nas=ssh://deploy@nas.local:22?key=/run/secrets/id_ed25519&publicHost=apps.example.com&default=true
ASPIREUI_TARGETS=box=tcp://10.0.0.5:2376?ca=/certs/ca.pem&cert=/certs/cert.pem&key=/certs/key.pem
ASPIREUI_TARGETS=cluster=k8s://prod-context?namespace=apps&expose=ingress&ingressHost={service}.example.com
```

| Scheme | Kind |
| --- | --- |
| `ssh://user@host:port` | Docker over SSH |
| `tcp://host:2376` | Docker over TCP (mTLS) |
| `k8s://context` | Kubernetes (Helm) |

Query keys: `key`, `passphrase`, `hostKey`, `ca`, `cert`, `kubeconfig`, `namespace`, `expose`,
`ingressHost`, `storageClass`, `publicHost`, `default`, `notes`. Every piece of key material is
either the text itself or **a path to a file holding it** — a mounted secret is the usual way in —
and it goes straight into the encrypted secret store, never into the target row.

### Api tokens

```
ASPIREUI_API_TOKENS=pipeline:ci:aspireui_the_value_your_ci_already_knows
```

The value is given rather than generated: a token nobody knows is no use to a pipeline. The user has
to exist (seed it in the same run) and the token inherits that user's permissions.

### Apps and stacks

```
ASPIREUI_SEED_APPS=vaultwarden,gitea=Code
ASPIREUI_SEED_DIR=/seed/edge;/seed/tools/docker-compose.yml
ASPIREUI_SEED_GIT=https://github.com/acme/app.git#main|deploy
ASPIREUI_SEED_DEPLOY=true
```

- **Apps** are catalog ids — the same apps the store lists, built the same way the install dialog
  would build them. `id=name` renames the stack.
- **Directories** are imported as a **copy**: the seed directory is never written to. A directory
  with an `aspireui-app.json` is read as a manifest, one with a compose file as compose, one with an
  AppHost project as an AppHost — whichever it holds.
- **Git** repositories are cloned and then imported the same way. `#branch` and `|subdir` are both
  optional.
- `ASPIREUI_SEED_DEPLOY=true` deploys what was seeded as soon as hosting is running. Anything that
  fails to deploy stays as a stack you can look at and start by hand.

## The seed file

```json
{
  "users": [
    { "username": "boss", "password": "bosspassword", "admin": true },
    { "username": "kim", "password": "kimpassword", "permissions": ["deploy", "configure", "files"],
      "viewModes": ["simple"], "mustChangePassword": true }
  ],
  "targets": [
    { "name": "nas", "kind": "ssh", "host": "nas.local", "user": "deploy",
      "key": "/run/secrets/id_ed25519", "publicHost": "apps.example.com", "default": true }
  ],
  "tokens": [{ "name": "pipeline", "username": "kim", "token": "aspireui_known_value" }],
  "appSources": [{ "name": "acme", "url": "https://apps.acme.test/apps.json" }],
  "settings": { "PublicHost": "apps.example.com", "NpmEnabled": "true", "NpmBaseUrl": "http://npm:81" },
  "apps": [{ "id": "vaultwarden", "name": "Passwords", "deploy": true }],
  "stacks": [
    { "name": "Edge", "compose": "/seed/edge/docker-compose.yml" },
    { "name": "Tools", "path": "/seed/tools" },
    { "name": "App", "git": "https://github.com/acme/app.git", "branch": "main", "subdir": "deploy" },
    { "name": "Services", "projects": ["/src/Api/Api.csproj", "/src/Worker/Worker.csproj"] }
  ],
  "deploy": false
}
```

Point `ASPIREUI_SEED_FILE` at the file, or at a directory containing `aspireui.seed.json`. A file
that cannot be read is reported on stderr and the rest of the seed still runs — a typo in a seed
must not keep the server down.

`settings` takes the same keys as `ASPIREUI_SET_<Key>` and, like it, only fills in what is still
empty so a change made in the UI is not overwritten on the next restart.

## From Aspire

The [`Nextended.Aspire.Hosting.AspireUI`](https://www.nuget.org/packages/Nextended.Aspire.Hosting.AspireUI)
package writes all of this for you:

```csharp
builder.AddAspireUI("aspireui")
    .WithAdminUser("admin", "change-me")
    .WithUser("kim", "kimpassword", AspireUIPermissions.AppUser)
    .WithSshTarget("nas", "nas.local", "deploy", keyFile: "./keys/id_ed25519")
    .WithApps("vaultwarden", "gitea")
    .WithSeedFromDirectory("./seed")
    .WithAutoDeploy()
    // And the settings that used to need a trip through the UI:
    .WithSingleSignOn("https://id.example.com/realms/main", "aspireui", ssoSecret,
        adminGroup: "aspireui-admins")
    .WithS3Backups("aspireui-backups", accessKey, secretKey, endpoint: "https://minio.example.com")
    .WithAuditRetention(days: 90);
```

Two of those calls cover whole areas rather than one setting each:

| Call | What it writes |
| --- | --- |
| `.WithSettings(s => { s.PublicHost = "apps.example.com"; s.BackupIntervalHours = 12; })` | Every key from the table above, as typed properties instead of strings. Anything left null is not sent. |
| `.WithAssistant(endpoint, model, apiKey)` | The [assistant's](ai-chat.md) backend. `.WithOllamaAssistant(ollama, "llama3.2")` and `.WithLocalAiAssistant(…)` take a model server that lives in the same Aspire stack — AspireUI waits for it and reaches it over the container network, so no url has to be known in advance. `.WithCliAssistant("claude")` uses an agent CLI on the host instead. |

`.WithForcedSettings()` turns the fill-in-what-is-empty rule off and applies the settings on every
start, which also overwrites what somebody changed in the UI. Off by default for that reason.

The package's own reference, with every call and its overloads, is at
[fgilde.github.io/Nextended](https://fgilde.github.io/Nextended/projects/aspire-aspireui)
([Deutsch](https://fgilde.github.io/Nextended/de/projects/aspire-aspireui)).
