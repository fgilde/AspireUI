# Submitted apps

One file per app: `<app-id>.json`, in the format described in
[docs/app-manifest.md](../../../../../docs/app-manifest.md) and validated by
[`../aspireui-app.schema.json`](../aspireui-app.schema.json).

Every `*.json` in this folder is merged into the store on start, so a merged pull request is all it
takes. Keep `submitter` and `source` filled in — the store shows them as provenance.

Checklist for a pull request:

- the image is publicly pullable and carries a real tag (no `build:`, no private registry)
- `port` is the port inside the container; AspireUI publishes a free host port itself
- everything the user must keep is a named `volume`
- secrets are `params` with an empty default, so each install generates its own
- the description says what the app is *and* how the first login works
- `npm run lint` in `web/` and `dotnet test tests/AspireUI.Server.Tests` stay green
