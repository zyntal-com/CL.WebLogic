# Changelog

All notable changes to this project will be documented in this file. Going forward, versions follow [Semantic Versioning](https://semver.org/).

## Unreleased

Breaking storage migration.

### Changed
- Replaced the legacy `CodeLogic.StorageS3` dependency with provider-neutral `CodeLogic.Storage`.
- `WebLogicBootstrapOptions.IncludeStorage` now loads `StorageLibrary`.
- Remote theme reads use a mounted storage connection and a relative path prefix; bucket/container selection belongs to the connection configuration.
- `WebStorageMode.Storage` replaces the S3-specific mode.

### Security
- Realtime hub `Replay` only returns events the connection would have received live (public,
  signed-in, its own user and access groups). Before, any anonymous client could replay
  user-targeted events such as private chat messages and notifications, and the raw snapshot
  was returned unfiltered alongside the filtered list. `Take` is clamped to 1–250.
- Request-handled and request-blocked events are `Internal` and no longer carry the client IP.
  Before, every request's path and client IP were broadcast to all public hub connections.
- `Internal` events are no longer pushed to signed-in connections; they stay on the server.
- New `WebLogicRealtimeAccess.CanReceive` holds the audience rules.

### Removed (breaking)
- `IncludeStorageS3`, `WebStorageMode.S3`, `S3ConnectionId`, `S3Bucket`, and `S3Prefix`.
- Checked-in MiniBlog copies and manifests for the legacy `CL.StorageS3` library.

## [0.3.0] - 2026-05-08

Two-layer output caching. Additive — no breaking changes.

### Added
- `WebOutputCachePolicy` on `WebRouteOptions.OutputCache` — opt a route into rendered-`WebResult` caching with a TTL, per-query-key vary list, and a `Scope` enum (`AnonymousOnly` default, `Shared`, `PerUser`).
- Conservative defaults: GET only, 200 only, never caches a response that set `Set-Cookie`. Default `Scope = AnonymousOnly` means authenticated requests bypass the cache entirely; opt up to `Shared` for genuinely non-personalised pages or `PerUser` for per-visitor entries.
- `WebOutputCache.GetOrAddFragmentAsync<T>` for handler-level fragment caching, exposed via `WebRequestContext.OutputCache`. Single-flight guard around the factory: a stampede after expiry triggers exactly one rebuild per key.
- `WebLogicLibrary.OutputCache` (page + fragment) backed by the existing in-process `MemoryCache`. Invalidation helpers: `InvalidatePageAsync`, `InvalidatePagesByPathPrefixAsync`, `InvalidateFragmentAsync`, `InvalidateFragmentsByPrefixAsync`.
- When a policy is in effect and the response is cacheable, the runtime mirrors `Cache-Control: public, max-age={ttl}` onto the response so an upstream CDN / browser sees the same expiry. Suppressed automatically for per-user entries.

### Changed
- `WebLogicRuntime` constructor now takes a `WebOutputCache`. Apps using the library through `WebLogicLibrary` need no change; direct constructors of `WebLogicRuntime` (rare) need to thread the cache through.
- `WebRequestContext` has a new required `OutputCache` field. Created automatically by the runtime — only relevant if you build `WebRequestContext` instances by hand (e.g. tests).

## [0.2.0] - 2026-04-24

DB-backed sessions. Breaking. This tag ships the library half of the session
overhaul — steps 1 through 3. The app-side integration (a persistent
`IWebSessionStore`) lands separately; without one, the library has no identity
for any request.

### Added
- `IWebSessionStore` contract with `Get`/`Create`/`Touch`/`Revoke`/`RevokeAllForUser`/`SweepExpired`/`UpdateCsrfToken`. Implementations are expected to hash the client-visible token before persistence and enforce a per-user session cap by evicting oldest rows.
- `IWebPermissionResolver` contract — returns the effective permission set for a user, invalidatable externally when grants change.
- `WebSessionRecord` + `WebSessionCreate` records, and `WebSessionFeature` (stashed on `HttpContext.Items`).
- `SessionConfig` wired onto `WebLogicConfig.Session`: cookie name/domain/secure/SameSite, idle timeout (default 120 min), remember-me days (default 30), max concurrent sessions (default 3), IP-binding toggle, sweeper interval.
- `WebSecurityService.ResolveSessionAsync` / `ResolveIdentityAsync` / `RotateAfterSignInAsync` / `SignOutAsync` / `FlushSessionCookie`. Session rotation on sign-in, revocation on sign-out.
- `WebLogicLibrary.UseSessionStore(...)` / `UsePermissionResolver(...)` and public `SessionStore` / `PermissionResolver` properties.
- 8 contract-pinning tests against a minimal in-memory `IWebSessionStore` implementation (doubles as a reference for real stores).

### Changed
- Identity is resolved from the session store only. CSRF tokens live on the session row; validation uses `CryptographicOperations.FixedTimeEquals`. Session cookie clears itself when the inbound cookie is stale / expired / fails IP binding.
- `WebLogicRealtimeHub` authenticates via the session cookie and store — no session fallback, no query-string `?userId=` / `?accessGroups=`.
- `WebLogicRuntime.CreateContextAsync` calls `ResolveSessionAsync` before building the request context; `WriteAsync` calls `FlushSessionCookie` on the response path.

### Removed (breaking)
- `IWebAuthResolver`, `DefaultWebAuthResolver`, `WebLogicLibrary.UseAuthResolver`. Identity is a `WebSecurityService` concern now.
- `AuthConfig` (`AllowHeaderUserId`, `AllowHeaderAccessGroups`, `AllowSessionUserId`, `AllowSessionAccessGroups`) and `WebLogicConfig.Auth`. The library no longer has any code path that trusts a request header for identity.
- `WebRequestIdentity.ExternalPermissionResolver` static. Apps wire up permissions through `IWebPermissionResolver` instead.

### Migration notes for consumers
1. Implement `IWebSessionStore` (and register it via `weblogic.UseSessionStore(...)`). Sessions are now your responsibility to persist — pick any backend (SQL, Redis, in-memory for dev).
2. Implement `IWebPermissionResolver` if you rely on permission gates. Register via `weblogic.UsePermissionResolver(...)`. Cache internally and call your own invalidation on role changes.
3. Sign-in flow now calls `_security.RotateAfterSignInAsync(httpContext, userId, accessGroups, rememberMe)` — the return value holds the cookie token and CSRF token. Sign-out calls `_security.SignOutAsync(httpContext)`.
4. Remove any `WebRequestIdentity.ExternalPermissionResolver = ...` lines in startup.
5. Remove any `config.Auth.Allow*` settings from persisted configuration.

## [0.1.1] - 2026-04-24

Security release — two unauthenticated auth-bypass primitives closed.

### Security
- **`DefaultWebAuthResolver` no longer reads identity from `X-WebLogic-UserId` / `X-WebLogic-AccessGroups` by default.** `AuthConfig.AllowHeaderUserId` and `AllowHeaderAccessGroups` now default to `false`. If the app was exposed behind a proxy that didn't strip these headers, any anonymous client could claim any user id and any access group (including admin). Deployments that genuinely relied on header-bootstrapped identity can re-enable the flags explicitly per environment (the field descriptions now spell out the risk).
- **`WebLogicRealtimeHub` no longer accepts `?userId=` / `?accessGroups=` query parameters for SignalR connections.** Identity is taken from the authenticated session only. Previously any anonymous WebSocket client could subscribe to another user's private event stream or any access-group stream by setting query params on the hub URL.

### Notes
- No breaking source-API changes. Consumers that were using header-bootstrapped identity intentionally must flip the two `AllowHeader*` flags to `true` in their persisted `WebLogicConfig`.
- Tests: 27/27 passing.

## [0.1.0] - 2026-04-24

First tagged release — packaging and CI groundwork.

### Added
- NuGet package metadata on `src/CL.WebLogic.csproj` (`PackageId`, `Version`, `Authors`, `Description`, `RepositoryUrl`, `License`, `PackageReadmeFile`).
- XML documentation generation (`GenerateDocumentationFile`), with CS1591 suppressed until public API is fully documented.
- Repo-root `.editorconfig` defining formatting, file-scoped namespaces, and private-field naming conventions.
- GitHub Actions `ci.yml`: builds + runs tests on push/PR to `main`; packs on `v*` tags and uploads the `.nupkg` as a build artifact. NuGet publish step is included but commented pending `NUGET_API_KEY`.
- `.gitignore` entries for `artifacts/`, `*.nupkg`, `*.snupkg`.

### Notes
- No source-code behavior changes in this release. Everything that worked before still works — this release exists to make the library packageable and give it a versioning baseline.

## 2026-04-07 Toolkit Snapshot

Current `CL.WebLogic` state before the cleanup/consolidation branch:

- Established `CL.WebLogic` as a standalone repo/library with CodeLogic lifecycle integration.
- Added ASP.NET Core handoff so WebLogic owns routing, request handling, and page rendering.
- Built the routing/contributor model for applications and plugins.
- Added plugin support for pages, APIs, widgets, widget areas, and theme-facing contributions.
- Added `WebRequestContext` / page context access for query, form, files, session, cookies, headers, auth state, and services.
- Added template engine v2 with layouts, sections, partials, conditionals, loops, widgets, and page scripts.
- Added `WebPageDocument` / metadata support for title, description, canonical, Open Graph, and Twitter tags.
- Added built-in explorer endpoints for routes, plugins, APIs, widgets, widget areas, and form providers.
- Added widget registry, widget areas, area targeting policies, persistent widget settings, widget data endpoints, and widget actions.
- Added dashboard/layout primitives plus demo dashboard composition and saved layouts.
- Added realtime/event infrastructure with SignalR integration and widget event publishing.
- Added auth/RBAC foundation with MySQL-backed identity support and access-group checks.
- Added starter login flow and RBAC demo pages.
- Added `CL.WebLogic.Client` starter runtime with navigation, head/meta syncing, AJAX forms, widget helpers, dashboard helpers, and realtime hooks.
- Added model-driven forms:
  - C# form definitions
  - generated HTML rendering
  - client schema generation
  - client/server validation
  - file and image validation
  - form-to-command/entity mapping
- Added provider-backed form fields for selects and dependent fields.
- Added autocomplete/search-backed form fields.
- Added generic HTTP/JSON lookup provider support for form options and autocomplete.
- Expanded the starter site into a broader reference/demo app for themes, plugins, widgets, dashboards, forms, auth, and realtime features.

Planned next direction after this snapshot:

- Consolidate `CL.WebLogic` as a cleaner toolkit/foundation.
- Separate core toolkit concerns from sample/demo behavior more clearly.
- Extract/package `CL.WebLogic.Client` more formally.
- Add tests and harden provider/runtime boundaries.
