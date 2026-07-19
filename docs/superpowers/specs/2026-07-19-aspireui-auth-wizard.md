# AspireUI — Auth + First-Run Wizard + Dependency Check (Design)

**Date:** 2026-07-19 (Slice 8). Foundational + security-critical. No shortcuts on the auth boundary.

## Goals

1. **First-run wizard**: on startup the app detects it's fresh (no users) and opens a setup wizard that
   (a) creates the first **admin** user, and (b) runs a **dependency check** (.NET SDK, Docker) with
   guidance.
2. **Auth**: username/password login with securely hashed passwords and cookie-based sessions. All app
   API endpoints require authentication; setup/login/status/health are anonymous.
3. **User management** (admin): list, create, and delete users; mark admin. A user can't delete
   themselves into a no-admin state (keep ≥1 admin).

## Non-Goals (this slice)

- OAuth/SSO, email verification, password reset flows, MFA (later if wanted).
- Per-stack ownership/multi-tenant isolation (all authenticated users see all stacks — single-team tool).
- Encryption of the AI api key at rest (still the documented local-tool ceiling).
- Deploy (Slice 9).

## Security design (do it right)

- **Password hashing:** `Microsoft.AspNetCore.Identity.PasswordHasher<T>` (PBKDF2, per-password salt,
  versioned, constant-time verify) via the lightweight `Microsoft.Extensions.Identity.Core` package.
  Never store or log plaintext; never return the hash.
- **Sessions:** ASP.NET Core cookie authentication (`AddAuthentication().AddCookie`). Cookie:
  `HttpOnly`, `SameSite=Lax` (SPA is same-origin; Lax blocks cross-site CSRF for the state-changing
  POSTs while allowing top-level nav), `Secure` when the request is HTTPS. Sliding expiration.
- **Authorization:** all `/stacks*`, `/catalog`, `/templates`, `/settings`, `/users`, `/assist`,
  run/stop/status, export, import, preview — `RequireAuthorization()`. Anonymous only:
  `/auth/status`, `/auth/login`, `/auth/setup` (first-run), `/env/health` (dep check needs to run
  pre-login so the wizard can show it), and the SPA static files + fallback.
- **First-run guard:** `/auth/setup` (create first admin) succeeds ONLY when the users table is empty;
  once any user exists it returns 409. Adding further users goes through admin-only `/users`.
- **Rate/lockout:** out of scope beyond a small note; single-user local tool. Login returns a generic
  "invalid credentials" (no user-enumeration).
- **Password policy:** minimum length 8, enforced server-side on setup/create; surfaced in the UI.

## Data model

```
record User(string Id, string Username, string PasswordHash, bool IsAdmin, string CreatedAt);
record UserDto(string Id, string Username, bool IsAdmin, string CreatedAt);   // never carries the hash
```
`UserStore` (SQLite table `users(id,username UNIQUE,password_hash,is_admin,created_at)`) in the same
LocalApplicationData/AspireUI dir. Methods: `Count()`, `FindByUsername`, `Get(id)`, `List()`,
`Create(username, hash, isAdmin)`, `Delete(id)`, `AdminCount()`.

## Endpoints

```
GET  /auth/status   → { needsSetup: bool, authenticated: bool, user: UserDto|null }   (anon)
POST /auth/setup    { username, password }  → creates first admin + signs in; 409 if users exist (anon, first-run only)
POST /auth/login    { username, password }  → sign-in cookie; 401 generic on bad creds (anon)
POST /auth/logout   → clears cookie
GET  /env/health    → { dotnet:{ok,version}, docker:{ok,detail} }  (anon; runs `dotnet --version` + `docker info`)
GET  /users         → UserDto[]                    (admin)
POST /users         { username, password, isAdmin } (admin) → UserDto
DELETE /users/{id}  (admin; refuse if it would remove the last admin, and refuse self-delete of last admin)
```
All other existing endpoints: authenticated.

## Frontend

- On load, call `GET /auth/status`:
  - `needsSetup` → **Setup wizard** route `/setup`: step 1 dependency check (`GET /env/health`, show
    .NET + Docker status with ✓/✗ + hints), step 2 create admin (username + password + confirm,
    min-length validated) → `POST /auth/setup` → enter app.
  - not `needsSetup` and not `authenticated` → **Login** route `/login`.
  - authenticated → app (existing routes), guarded.
- A route guard: unauthenticated access to app routes redirects to `/login`; `/setup` only reachable
  when needsSetup.
- Header: current username + **Logout**; admin sees a **Users** link.
- **Users page** (`/users`, admin): list users, add (username/password/admin), delete (with the
  last-admin guard surfaced), can't-delete-self-into-no-admin.
- api.ts: `authStatus`, `setup`, `login`, `logout`, `envHealth`, `listUsers`, `createUser`, `deleteUser`.
  All fetches include credentials (same-origin cookie) — add `credentials: "include"` (or rely on
  same-origin default) and on 401 from any app call, redirect to `/login`.

## Dependency check

`GET /env/health` runs `dotnet --version` and `docker info`/`docker version` (short timeout each),
returns `{ dotnet: { ok, version }, docker: { ok, detail } }`. Used by the wizard (and available from
Help/Settings later). Never executes user input; fixed commands only.

## Error handling

- Setup when users exist → 409 (wizard shouldn't offer it then).
- Login bad creds → 401 generic.
- Any app endpoint without auth cookie → 401; SPA catches → redirect `/login`.
- Delete last admin → 400 with a clear message.
- env/health: a missing tool → `ok:false` with a short reason, never a 500.

## Testing

Backend (xUnit): PasswordHasher round-trip (hash≠plaintext, verify true/false); setup creates admin +
sets cookie + second setup → 409; login good/bad; an app endpoint (e.g. GET /stacks) → 401 without
cookie, 200 with; users CRUD admin-gated (non-admin → 403); delete-last-admin → 400; /env/health returns
the two tools' status (dotnet ok on this box). Use the isolated-DB test factory + a cookie-carrying
HttpClient (WebApplicationFactory handles cookies).
Frontend (Vitest + build gate): a pure guard/status→route mapper; build gate for setup/login/users pages.

## Deferred (BACKLOG)

OAuth/SSO, password reset, MFA, per-user stack ownership, login lockout, api-key encryption; Deploy (Slice 9).
