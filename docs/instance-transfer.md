# Export, import and support bundles

**Settings → Import** holds three things that treat the whole instance as one object.

## Export this instance

One zip — `aspireui-instance-<date>.zip` — holding `aspireui.instance.json` with:

- every **stack** (nodes, edges, extra files, limits, health checks, schedules),
- every **account** with its password hash, permissions, view modes and disabled flag, so a restore
  is a restore and nobody has to be given a new password,
- every **deploy target**,
- the **store sources**,
- the **settings**, minus the ones that only mean something on this machine (the last backup run,
  the pending seed list, the store's hidden-app list) and minus the secrets,
- the **deployments** as a list of what was running where.

### Secrets

By default an export carries **no secrets**: no api key, no proxy password, no ssh key, no
certificate. That is not only caution — a secret in this instance is stored as a reference into an
encrypted store whose key lives in this machine's data directory, so the reference would be
worthless anywhere else anyway.

*Include secrets* resolves them and writes them into the file **in plain text**, which is what makes
a real move to another machine possible. The file is then as sensitive as the machine it came from:
treat it that way.

## Import an instance

Pick a file and you get a summary before anything happens: what it holds, when it was written, and
which of its stacks, accounts and targets already exist here (struck through).

An import is a **merge, not a wipe**. Anything already here under that name is left exactly as it
is, and the result says what it skipped. *Overwrite what is already here* takes the other road:
accounts get the imported hash and permissions, settings get the imported value, stacks get replaced
in place — the receiving instance's own ids are kept so a deployment does not lose its stack.

Two things never travel: the receiving instance's **local target** (every instance has its own), and
its **deployments** — an import brings the definitions, not other machines' running containers. Any
key material that came in as text goes into the local secret store on the way in, so the target row
holds a reference here too.

Both directions need the **Global settings** permission, and the export is recorded in the activity
log — it is a copy of the accounts and the settings leaving the machine.

## Support bundle

`aspireui-support-<date>.zip` is what somebody needs to understand a broken install:

| In the file | What it holds |
| --- | --- |
| `report.md` | Versions, runtime and OS, the dotnet/docker/git/compose check, the deploy targets and their last probe, every app with state, health, target, urls and last error |
| `apps/<app>/docker-compose.yaml` | The compose file that was actually deployed, after all of AspireUI's post-processing |
| `apps/<app>/logs.txt` | The last 200 log lines of each running app |
| `activity.json` | The last 200 entries of the activity log |
| `settings.json` | Every setting, with anything that looks like a password, key, token, secret or webhook replaced by `***` |
| `aspireui.instance.json` | The stacks, so a problem can be reproduced |

No passwords, no api keys, no ssh keys, no certificates — a bundle is meant to be sent to somebody.
