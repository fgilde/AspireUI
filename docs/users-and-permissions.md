# Users & permissions

AspireUI has two kinds of account and one list.

- An **admin** may everything, including making other admins. There is always at least one: the
  last admin cannot be deleted, demoted or disabled.
- Everybody else has a **permission list**. What is on it is what they can do; what is not on it is
  hidden in the UI *and* refused by the server.

Permissions are checked on every request, read from the database, not from the login cookie. Taking
one away takes effect immediately — the user does not have to log out first.

## The permissions

| Permission | Grants |
| --- | --- |
| **Builder / editor** (`open-editor`) | Create, change and delete stacks, import code, run them locally. Without it the editor routes are blocked and only the app-store view is left. |
| **Install & run apps** (`deploy`) | Install from the store, start, stop, restart, update, undeploy, move/copy to another target, take and restore backups. |
| **Configure apps** (`configure`) | A hosted app's environment variables, ports and domain. |
| **Browse app files** (`files`) | List, view and download files in an app's volumes. |
| **Write app files** (`files-write`) | Upload, rename, delete and create folders in an app's volumes. Separate so a read-only file browser is possible. |
| **Terminal in containers** (`terminal`) | Run commands inside an app's containers. |
| **Deploy targets** (`targets`) | Add, change and remove the machines apps are deployed to. |
| **App store** (`store`) | Store sources and which apps are hidden. A hidden app stays hidden for everybody else. |
| **Global settings** (`settings`) | Proxy, notifications, backup schedule, dashboard, import settings. |
| **Docker host** (`docker`) | See and prune the host's images, containers and volumes. |
| **Users** (`users`) | Create users and grant permissions — see the limits below. |
| **Activity log** (`audit`) | Read who did what, to which app, and when. |

Anyone who is logged in can always **look**: the app list, an app's status and its logs. That is the
floor, and it is why a user with an empty list is a viewer rather than a locked-out account.

## What a user manager may not do

The `users` permission is not a way to become an admin:

- Only an admin can set or remove the **admin flag**, so a user manager cannot promote anybody
  (themselves included).
- A user manager cannot **touch an admin account** at all — no password reset, no disable, no delete.
- A user manager can only hand out **permissions they hold themselves**. Boxes for the rest are
  disabled in the dialog, and the server drops them if they are sent anyway.

## Presets

The permissions dialog has a few one-click sets:

| Preset | Contains |
| --- | --- |
| Everything | all of them |
| Operator | deploy, configure, files, delete files, terminal, builder |
| App user | deploy, configure, files |
| Viewer | files |
| Nothing | look only |

## Defaults

- A **newly created user** gets builder, deploy and configure — exactly what a non-admin could do
  before the other permissions existed. Pick a preset in the *Add user* dialog to change that.
- An **account created before permissions existed** keeps everything it had; its list is empty in
  the database and that is read as "all". Saving the dialog once turns it into a real list.
- A **disabled** account has no permissions at all, whatever its list says.

## Seeding accounts

`ASPIREUI_USERS` creates accounts at start — `name:password[:permissions]`, with a preset name or a
list of permission ids in the third field. See [Seeding an install](seeding.md).

## Two-factor authentication

Any account can add a second factor under **Profile → Two-factor authentication**: scan the QR code
with an authenticator app, type the code it shows, and write down the eight **recovery codes** that
appear once. From then on signing in asks for a code after the password.

- The QR code is drawn in your browser. The shared secret never goes to a third party to be turned
  into a picture.
- Between password and code nothing is signed in: the half-finished login is a five-minute encrypted
  ticket, not a session.
- A recovery code works wherever the app's code does, and is spent when used. The codes are kept as
  hashes, so a copy of the database is not a copy of your second factor.
- Turning it off, or asking for new recovery codes, needs the password again — a borrowed session
  cannot do either.
- **Phone gone, codes gone?** An admin can remove the second factor from the *Users* page. Only an
  admin, and never for another admin's account unless they are one themselves.

The list shows a **2FA** badge for accounts that have it on.

## Single sign-on (OpenID Connect)

**Settings → Sign-in** hands authentication to an identity provider — Keycloak, Authentik, Entra,
Google, Zitadel, anything that speaks OpenID Connect. Give it the **authority** (the issuer url), a
**client id** and, for a confidential client, a **client secret**; everything else — the authorize,
token and userinfo endpoints — is read from the provider's own discovery document, so a provider that
moves an endpoint is followed rather than configured twice. *Test discovery* says whether it answers.

Register the redirect url the page shows you (`https://your-host/api/auth/sso/callback`) with the
provider.

How it works, and why it is this way:

- **Authorization code with PKCE.** The verifier never leaves the server; the state and the verifier
  travel in a ten-minute encrypted cookie, so nothing has to be remembered between the two redirects
  and a reply that did not start here cannot be replayed.
- **Claims come from the `userinfo` endpoint**, not from a token this server parses. The access token
  arrives directly from the token endpoint over TLS, so there is nothing to check a signature
  against that is not already trusted — and no JWT library in the login path.
- **Username** comes from the claim you name, or from `preferred_username`, `email`, `name`, `sub`
  in that order.
- **Groups** are read from a claim that may be an array, a space-separated string or a
  comma-separated one, because providers disagree. Name an **admin group** and its members are
  admins — and then the provider owns that on *every* sign-in, including taking it away.
- **New accounts** are created on first sign-in with the permissions you pick (a preset or a list of
  ids), and get a password nobody knows: the provider authenticates them, not us. Turn that off and
  only people who already have an account here can use the provider.
- **Password sign-in keeps working.** An admin locked out by a broken provider still gets in.

## The activity log

**Settings → Activity** lists everything that changed something: who did it, which app it was about,
what came back and how long it took. Reading is not recorded — a log of every page view buries the
one line that matters.

It is written in one place, the request pipeline, rather than in each handler, so a new endpoint is
logged the day it exists instead of the day somebody remembers to add a line to it. A request that
was **refused** is logged too, with its status: 403 on the log is how you find out that somebody's
permissions are wrong (or that they should be).

Entries older than `AuditRetainDays` (90 by default, `0` keeps everything) are dropped. The
retention and *Prune now* need the **Global settings** permission; reading the log needs
**Activity log**.

## View modes

Independent of permissions, a user can be limited to one of the two UIs — *Full* (builder, canvas)
or *Simple* (the app store view). With both allowed the user gets an in-app toggle. This is about
which UI they see; the permission list is about what they may do in it.
