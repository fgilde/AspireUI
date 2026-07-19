# AspireUI Auth + Wizard Implementation Plan

> REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Security-critical — no shortcuts on the auth boundary.

**Goal:** First-run wizard (create admin + dependency check), cookie-based auth with hashed passwords, all app endpoints authenticated, admin user management.

## Global Constraints
- Tool projects net10.0. Passwords hashed with `Microsoft.AspNetCore.Identity.PasswordHasher<T>` (never plaintext, never returned/logged). Cookie auth: HttpOnly, SameSite=Lax, Secure-on-HTTPS, sliding expiry.
- Conventional Commits, NO Co-Authored-By, `git push` after every commit.
- **Do not break existing integration tests:** adding `RequireAuthorization()` to app endpoints would 401 every existing WebApplicationFactory test. Task 1 MUST update the shared test factory to auto-authenticate (test-only auth handler) so ApiTests/AssistTests/SettingsTests/TemplateTests keep passing unmodified; auth-specific tests use a non-auto-auth variant to exercise the real flow.

---

### Task 1: Backend auth core (UserStore, hashing, cookie auth, /auth + /env/health, require-auth, test-factory auto-auth)

**Files:** `Models/User.cs` (new), `Services/UserStore.cs` (new), `Services/EnvHealth.cs` (new, optional), `Endpoints/AuthEndpoints.cs` (new), `Program.cs`, `Endpoints/StackEndpoints.cs`, `AspireUI.Server.csproj` (+ Microsoft.Extensions.Identity.Core); tests `AuthTests.cs` (new), `tests/.../TestWebAppFactory.cs` (update).

- `record User(string Id, string Username, string PasswordHash, bool IsAdmin, string CreatedAt);` + `record UserDto(string Id, string Username, bool IsAdmin, string CreatedAt);`
- `UserStore` (SQLite `users(id,username UNIQUE,password_hash,is_admin,created_at)`, same dataDir/keep-alive pattern as StackStore/SettingsStore): `int Count()`, `int AdminCount()`, `User? FindByUsername(string)`, `User? Get(string id)`, `IReadOnlyList<User> List()`, `User Create(string username, string hash, bool isAdmin)`, `bool Delete(string id)`.
- Program.cs: `builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o => { o.Cookie.HttpOnly=true; o.Cookie.SameSite=SameSiteMode.Lax; o.Cookie.SecurePolicy=CookieSecurePolicy.SameAsRequest; o.SlidingExpiration=true; o.ExpireTimeSpan=TimeSpan.FromDays(7); o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode=401; return Task.CompletedTask; }; o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode=403; return Task.CompletedTask; }; })`. Add `AddAuthorization()`. `app.UseAuthentication(); app.UseAuthorization();` BEFORE MapStackEndpoints/fallback. (The redirect→401/403 events keep it API-friendly instead of redirecting to a login page.)
- `AuthEndpoints.MapAuthEndpoints`: register a `UserStore` + `PasswordHasher<User>` (dataDir like the others).
  - `GET /auth/status` (anon): `{ needsSetup: store.Count()==0, authenticated: ctx.User.Identity.IsAuthenticated, user: <UserDto or null from claims/store> }`.
  - `POST /auth/setup` {username,password} (anon): if `store.Count()>0` → 409. Validate username non-empty + password length ≥8. Hash, create admin, sign in (issue cookie via `ctx.SignInAsync` with claims: NameIdentifier=id, Name=username, Role=Admin). Return UserDto.
  - `POST /auth/login` {username,password} (anon): find user; `hasher.VerifyHashedPassword`; on success sign in (claims incl. Role=Admin if IsAdmin), return UserDto; on fail → 401 generic "invalid credentials".
  - `POST /auth/logout`: `ctx.SignOutAsync`; 204.
  - `GET /env/health` (anon): run `dotnet --version` and `docker info` (or `docker version`) via Process with a short timeout (e.g. 5s each), catch all; return `{ dotnet:{ok,version}, docker:{ok,detail} }`. Fixed commands, no user input.
- StackEndpoints / app routes: wrap the app endpoints in a group with `.RequireAuthorization()` (or add `.RequireAuthorization()` per route). Simplest: `var app2 = app.MapGroup("").RequireAuthorization();` and map the app endpoints on it — OR add `.RequireAuthorization()` to each existing route. Keep `/auth/*`, `/env/health` anonymous. The SPA static files + `MapFallbackToFile` stay anonymous (the SPA itself gates via /auth/status).
- **Test factory:** `TestWebAppFactory` (shared by ApiTests/AssistTests/SettingsTests/TemplateTests) — add a test-only authentication scheme that authenticates EVERY request as a fixed admin (a minimal `AuthenticationHandler<>` returning an admin ClaimsPrincipal), registered as the default scheme in `ConfigureServices`, so those tests keep passing without cookies. Provide a way to opt OUT (e.g. a subclass `NoAuthTestFactory` or a constructor flag) for AuthTests to exercise the real cookie flow.
- Tests (AuthTests.cs, using the real-auth factory / a cookie-carrying HttpClient — WebApplicationFactory's HttpClient persists cookies by default):
  - PasswordHasher: hash≠plaintext; verify correct true, wrong false.
  - `/auth/status` fresh → needsSetup true, authenticated false.
  - `/auth/setup` creates admin + subsequent `/auth/status` authenticated true (cookie carried); a SECOND `/auth/setup` → 409.
  - `/auth/login` wrong creds → 401; right creds → 200 + authenticated.
  - An app endpoint (`GET /stacks`) WITHOUT auth (fresh client on the real-auth factory) → 401; WITH login → 200.
  - `/env/health` → dotnet.ok true on this box.
- Gate: `dotnet test` all green (existing + new). Commit `feat: cookie auth, first-run setup, dependency check` + push.

---

### Task 2: Backend user management (/users, admin-gated, last-admin guard)

**Files:** `Endpoints/AuthEndpoints.cs` (or new UserEndpoints), tests `AuthTests.cs`/`UsersTests.cs`.

- `GET /users` (admin) → UserDto[]. `POST /users` {username,password,isAdmin} (admin) → create (validate len≥8, unique username → 409 on dup) → UserDto. `DELETE /users/{id}` (admin): refuse (400) if deleting would drop AdminCount to 0; allow otherwise.
- Admin gate: `.RequireAuthorization(policy => policy.RequireRole("Admin"))` on the /users group.
- Tests (real-auth factory + logged-in admin cookie): admin can list/create/delete; a non-admin (create a 2nd non-admin user, log in as them) → 403 on /users; delete-last-admin → 400; create dup username → 409.
- Gate: `dotnet test` green. Commit `feat: admin user management endpoints` + push.

---

### Task 3: Frontend auth flow (status guard, login, setup wizard)

**Files:** `web/src/api.ts`, `web/src/model.ts` (types + a pure status→route mapper), `web/src/auth/` (AuthGate, LoginPage, SetupWizard), `web/src/App.tsx` (routes + guard), `web/src/model.test.ts`.

- api.ts: `authStatus()`, `setup(u,p)`, `login(u,p)`, `logout()`, `envHealth()`. Ensure all fetches send the session cookie (same-origin default does; if any use absolute URLs, add `credentials:"include"`). Central `ok()` on 401 → redirect to `/login` (or expose an onUnauthorized hook the app wires to navigate).
- model.ts: `interface AuthStatus { needsSetup: boolean; authenticated: boolean; user: UserDto|null }`, `interface UserDto {...}`, `interface EnvHealth {...}`. Pure `routeForStatus(s): "/setup"|"/login"|null` (null = allowed into app) + vitest.
- App.tsx: on mount fetch authStatus; an `AuthGate` wrapper: needsSetup → redirect `/setup`; !authenticated → `/login`; else render children. `/setup` and `/login` are the only routes reachable when unauthenticated (guard the app routes).
- `SetupWizard` (`/setup`): Mantine Stepper — step 1 "Environment check" (envHealth → .NET + Docker with ✓/✗ + a hint line each; a Next button, allow proceeding even if Docker missing with a warning that running stacks needs it); step 2 "Create admin" (username, password, confirm, min-8 validation) → setup() → on success navigate to `/`.
- `LoginPage` (`/login`): username/password → login() → navigate `/`; show generic error on 401.
- Header (overview + editor): show current username + Logout button (logout() → `/login`); if admin, a Users link.
- Gate: `npm run build` clean + `npm test`. Commit `feat: auth gate, login, setup wizard` + push.

---

### Task 4: Frontend user management page

**Files:** `web/src/api.ts` (listUsers/createUser/deleteUser), `web/src/pages/Users.tsx`, `web/src/App.tsx` (route, admin-only).

- Users page (`/users`, admin): table of users (username, admin badge, created), add form (username/password/admin switch), delete button (disabled/blocked for the last admin with a tooltip). Surface 400 (last admin) / 409 (dup) errors inline.
- Gate: build clean + test. Commit `feat: user management page` + push.

---

### Task 5: E2E verify
- `dotnet test` green; frontend build/test green.
- Live on a FRESH data dir (set DB_PATH/WORKSPACE_DIR to a temp dir, or delete the LocalAppData/AspireUI/*.db first — CAREFUL: back up, or just point env vars at a temp dir for the check): `/auth/status` → needsSetup; `/env/health` shows dotnet ok + docker; `POST /auth/setup` creates admin + cookie; app endpoint 401 without cookie, 200 with; second setup → 409; `/users` admin flow. Confirm the SPA serves the setup wizard when fresh and login when returning. Do NOT wipe the user's real settings/stacks — use a temp DB_PATH for the fresh-state check, then verify the normal server still works with the real DB.
- Fix issues in small pushed commits; report.

## Self-Review
- Coverage: auth core + setup + login + require-auth + dep-check + test-factory auto-auth (T1); user mgmt + last-admin guard (T2); auth gate + login + wizard (T3); users page (T4); verify (T5). ✔
- Security: hashed passwords (PasswordHasher), HttpOnly/SameSite=Lax cookie, all app routes authorized, generic login errors, first-run-only setup, last-admin guard, no hash in DTOs. ✔
- Non-breakage: shared test factory auto-authenticates so existing 55+ integration tests pass; auth tests use the real scheme. ✔
- Risk: cookie SameSite=Lax + same-origin SPA covers CSRF for this tool; full anti-forgery tokens deferred (documented). Test-auth handler must be test-project-only, never in production wiring.
