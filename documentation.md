# Xental Platform — System & API Documentation

> **Living document.** This is the source of truth for how the Xental backend is used,
> written so the frontend (dashboard + docs site) can be built against it. It is updated
> as the system evolves. Last updated for **Phase 2 & 3 — virtual accounts (NUBANs) and the
> webhook reconciliation engine** (on top of Stage 4 auth). Section 3 is authoritative for
> the auth model; Section 6 for payments/reconciliation.

---

## 1. What Xental is

Xental is a payments platform. **Developers integrate only with Xental** — they never
create or manage anything on the underlying banking provider (Nomba). Xental owns the
provider relationship; a developer's entire experience is: sign up → create API keys →
call the Xental API.

There are two audiences, and therefore **two token planes**:

| Plane | Who uses it | How they authenticate | What it can do |
|-------|-------------|----------------------|----------------|
| **Dashboard** | A human, in the developer dashboard UI | Email + password (login) | Manage the account and API keys |
| **API** | Server-side integration code | API key (client id + secret) | Call the payments API |

These planes are deliberately separated. A dashboard token **cannot** call the payments
API, and an API token **cannot** manage keys. This limits blast radius if either token
leaks.

---

## 2. Core concepts

### Developer account (a "tenant")
The account a developer registers with email + password. Every piece of data in the
system belongs to exactly one account and is invisible to all others (row-level tenant
isolation, enforced in the database layer).

### API keys
Credentials the integration uses. Each key has:
- a **client id** (public-ish identifier, e.g. `xnt_live_…`)
- a **client secret** (shown **once** at creation, stored only as a hash)
- a **mode**: `test` or `live` (Stripe-style). Test keys are for development; live keys
  move real money. The mode travels inside every API token minted from the key, so the
  backend always knows which environment a request belongs to.
- a **status**: `Active` or `Revoked`.

Keys can be **rotated** (revoke the old one, mint a fresh one with the same label + mode)
and **revoked**.

### Sub-merchants
Internal Xental records a developer uses to segment **their own** customers, branches, or
tenants (e.g. "Green School", "Blue Clinic"). They are **not** created on any external
provider — they exist only inside Xental. References are unique per account (two different
accounts may reuse the same reference).

---

## 3. Authentication flows

> **Dashboard sessions are cookie-based.** Login/refresh/OAuth set two **HttpOnly, Secure,
> SameSite=Lax** cookies — `xnt_access` (short-lived, ~15 min) and `xnt_refresh` (rotating,
> ~14 days). Tokens are **never** in a response body and are not readable by JS. The browser
> sends them automatically; a frontend on a different origin must use `fetch(..., { credentials: "include" })`
> and be listed in the API's CORS allow-list. The **API plane** (server-to-server) is unchanged:
> it uses `Authorization: Bearer <api-token>` from client-credentials.

### 3.1 Register (does NOT log you in)
```
POST /api/v1/developers/register
{ "name": "Acme Dev", "email": "dev@acme.com", "password": "Str0ng-Passw0rd!" }
```
Creates an **unverified** account and emails a verification link. Returns `201` with
`{ tenantId, email, emailVerified:false, message }` and **no session** — the user must verify
their email before they can log in.
- **Strong password required**: 12–128 chars incl. an uppercase, a lowercase, a digit, and a special char (else `400` with a descriptive message).
- Email normalized + unique → `409` if taken.

### 3.2 Verify email (magic link)
The emailed link points at the API: `GET {api}/api/v1/developers/verify-email?token=…`. It
verifies the account and **redirects the browser to the frontend** at
`{frontend}/email-verified?verified=true|false`. Re-send with
`POST /api/v1/developers/resend-verification` (requires a session).

### 3.3 Log in (verified accounts only)
```
POST /api/v1/developers/login
{ "email": "dev@acme.com", "password": "Str0ng-Passw0rd!" }
```
On success: `200` with `{ tenantId, email, emailVerified }` **and `Set-Cookie` for
`xnt_access` + `xnt_refresh`**. Errors: `401` generic "invalid email or password" (no
enumeration); `403` "Email not verified" (verify first).

### 3.4 Refresh / logout
- `POST /api/v1/developers/refresh` — reads the `xnt_refresh` cookie, **rotates** it
  (single-use; the old one is invalidated), and sets fresh cookies. `401` if missing/expired.
  Call this when an API call returns `401` (access token expired).
- `POST /api/v1/developers/logout` — revokes the refresh token and clears both cookies. `204`.

### 3.5 Forgot / reset password
```
POST /api/v1/developers/forgot-password   { "email": "dev@acme.com" }   -> 202 (always)
```
Emails a reset link to `{frontend}/reset-password?token=…`. That page collects a new
password (same strong-password rules) and calls:
```
POST /api/v1/developers/reset-password    { "token": "…", "newPassword": "…" }
```
Always `202` on request (no enumeration). Reset consumes the link and invalidates all other
outstanding reset links.

### 3.6 Social login (Google / GitHub)
Point the browser at `GET /api/v1/auth/oauth/google` (or `/github`). After consent the
provider returns to `/api/v1/auth/oauth/{provider}/callback`, which finds-or-creates the
account (email auto-verified), **sets the session cookies**, and redirects to
`{frontend}/auth/callback#status=ok` (or `#error=…`). A social login for an existing email
links to that same account.

### 3.7 API plane: key → token → payments API
```
POST /api/v1/api-keys        (dashboard session)   { "label": "server-prod", "mode": "live" }
POST /api/v1/auth/token      { "clientId": "xnt_live_…", "clientSecret": "sk_live_…" }  -> API token (JSON)
```
The client secret is shown **once** at key creation. The API token (JSON body, ~1 h) is used
as `Authorization: Bearer …` on payments-API calls; re-request when it expires.

```
Register ─▶ verify email ─▶ login (session cookies) ─▶ create key ─▶ (client id + secret)
(client id + secret) ─▶ /auth/token ─▶ API token (Bearer) ─▶ payments API
```

---

## 4. API reference

Base URL (local): `https://localhost:7292` · Swagger UI: `/swagger`
All request/response bodies are JSON. All timestamps are ISO-8601 UTC.

### Token type & shape
Every token response looks like:
```json
{ "accessToken": "<jwt>", "tokenType": "Bearer", "expiresIn": 3600 }
```

---

### Developers (dashboard plane)

#### `POST /api/v1/developers/register`  — *anonymous*
Create a developer account.

Request:
```json
{ "name": "Acme Dev", "email": "dev@acme.com", "password": "correct-horse-battery" }
```
`201 Created`:
```json
{
  "tenantId": "3f…",
  "email": "dev@acme.com",
  "emailVerified": false,
  "accessToken": "<dashboard-jwt>",
  "tokenType": "Bearer",
  "expiresIn": 3600
}
```
`409 Conflict` — email already registered.

#### `POST /api/v1/developers/login`  — *anonymous*
Request:
```json
{ "email": "dev@acme.com", "password": "correct-horse-battery" }
```
`200 OK` — same body shape as register. `401 Unauthorized` — invalid email or password.

#### `GET /api/v1/developers/me`  — *dashboard token*
`200 OK`:
```json
{ "tenantId": "3f…", "name": "Acme Dev", "email": "dev@acme.com", "emailVerified": true, "status": "Active", "createdAtUtc": "2026-07-01T15:00:00Z" }
```

#### `GET /api/v1/developers/verify-email?token=…`  — *anonymous (magic link)*
`302 Found` → redirects to `{App.BaseUrl}/email-verified?verified=true|false`.

#### `POST /api/v1/developers/resend-verification`  — *dashboard token*
`202 Accepted` — re-sends the verification email (no-op if already verified).

#### `POST /api/v1/developers/forgot-password`  — *anonymous*
Body `{ "email": "dev@acme.com" }`. `202 Accepted` — always (no enumeration).

#### `POST /api/v1/developers/reset-password`  — *anonymous*
Body `{ "token": "…", "newPassword": "…" }` (password ≥ 12 chars).
`204 No Content` on success. `400 Bad Request` — token invalid/expired or password too weak.

---

### Social login (dashboard plane) — browser redirects, anonymous

#### `GET /api/v1/auth/oauth/{provider}`  — `provider` = `google` | `github`
`302 Found` → the provider's consent screen (sets a short-lived CSRF `state` cookie).

#### `GET /api/v1/auth/oauth/{provider}/callback?code=…&state=…`
`302 Found` → `{App.BaseUrl}/auth/callback#token=<dashboard-jwt>` on success, or
`…#error=invalid_state|login_failed` on failure.

> **Register these callback URLs** in the provider consoles (must match exactly):
> `{App.BaseUrl}/api/v1/auth/oauth/google/callback` and `…/github/callback`.

---

### API keys (dashboard plane) — require `Authorization: Bearer <dashboard-token>`

#### `POST /api/v1/api-keys`
Request:
```json
{ "label": "server-prod", "mode": "live" }   // mode: "test" | "live"
```
`201 Created` — **client secret shown once**:
```json
{
  "id": "b1…",
  "clientId": "xnt_live_…",
  "clientSecret": "sk_live_…",
  "mode": "Live",
  "label": "server-prod",
  "status": "Active",
  "lastUsedAtUtc": null,
  "createdAtUtc": "2026-07-01T15:00:00Z"
}
```

#### `GET /api/v1/api-keys`
`200 OK` — array of keys **without** secrets (`clientSecret` is `null`).

#### `POST /api/v1/api-keys/{id}/rotate`
Revokes the key and issues a fresh one with the same label + mode. `200 OK` returns the
new key **with** its one-time secret. `404` if the key isn't found for this account.

#### `DELETE /api/v1/api-keys/{id}`
Revokes the key. `204 No Content`. Existing API tokens keep working until they expire;
no *new* tokens can be minted from a revoked key. `404` if not found.

---

### API auth (API plane)

#### `POST /api/v1/auth/token`  — *anonymous*
OAuth2 client-credentials. Request:
```json
{ "clientId": "xnt_live_…", "clientSecret": "sk_live_…" }
```
`200 OK` — token response (see shape above). `401` — invalid/revoked credentials
(generic message; no enumeration of client ids).

---

### Sub-merchants (API plane) — require `Authorization: Bearer <api-token>`

#### `POST /api/v1/sub-merchants`
Request:
```json
{ "name": "Green School", "reference": "sch-001" }
```
`201 Created`:
```json
{ "id": "c2…", "name": "Green School", "reference": "sch-001", "status": "Active", "createdAtUtc": "2026-07-01T15:05:00Z" }
```
`409 Conflict` — the `reference` already exists **for this account**.

#### `GET /api/v1/sub-merchants`
`200 OK` — array of this account's sub-merchants only.

---

### Health
- `GET /health` — liveness/readiness probe (plain).

---

## 5. Errors

All error responses use RFC-7807 **ProblemDetails** (`application/problem+json`):
```json
{ "status": 409, "title": "Conflict", "detail": "An account with this email already exists." }
```

| Status | Title | When |
|--------|-------|------|
| 400 | Validation failed | Bad input (e.g. password too short, missing field) |
| 401 | Authentication failed | Bad/expired token or invalid credentials |
| 403 | Forbidden | Wrong token plane (e.g. dashboard token on the API plane) |
| 404 | Not found | Resource missing for this account |
| 409 | Conflict | Duplicate email / duplicate sub-merchant reference |
| 502 | Upstream provider error | Provider (Nomba) failure |
| 500 | An unexpected error occurred | Unhandled error (no internal detail leaked) |

---

## 6. Token claims (for the frontend)

Dashboard token carries: `sub`/`tenant_id`, `email`, `email_verified`, `scope=dashboard`.
API token carries: `sub`/`tenant_id`, `scope=api`, `key_mode` (`test`/`live`), `kid`
(the API key id). The UI can read `email_verified` from the dashboard token to prompt for
verification.

---

## 6b. Payments — virtual accounts & reconciliation (Phase 2 & 3)

### Provision a NUBAN (API plane, `api` token)
```
POST /api/v1/virtual-accounts
{ "accountRef": "stu-001", "name": "Ada Payer", "email": "ada@x.com",
  "phone": null, "expectedAmountKobo": 500000, "expiryDateUtc": null }
```
Maps a stable `accountRef` to a persistent NUBAN (via Nomba) and, optionally, an
`expectedAmountKobo` used to reconcile inflows. `201` returns the account number/bank/name
plus `amountPaidKobo`, `deficitKobo`, `overpaymentKobo`, `paymentState`. `409` if the
`accountRef` already has an account. `GET /api/v1/virtual-accounts/{accountRef}` fetches it.

### Nomba webhook receiver
```
POST /api/v1/webhooks/nomba          (anonymous; verified by signature)
```
Verifies the `nomba-signature` header — `Base64(HMAC-SHA256(secret, payload))` over the nine
colon-delimited fields Nomba signs (incl. the `nomba-timestamp` header) — then dedupes,
matches the credited NUBAN, and reconciles. Always returns `200` for a valid signature (so
Nomba doesn't retry); `401` for a bad/missing signature. Body reports
`{ status, reference, reconciliation, paymentState, reason }`.

### Reconciliation rule book
Money is integer **kobo**. Inflows are **always credited** (never rejected); the net credit
is the amount **less provider fees**. Each deposit is recorded as an immutable `transaction`
(id, dedicated_account_id, amount/fee/net, `status`, `reconciliation`, `reason`,
nomba_reference, transfer_name, created_at, reconciled_at).

| Reconciliation status | Meaning |
|---|---|
| `Reconciled` | Amount matches expected (or account has no expectation) |
| `Underpaid` | Below expected — credited, deficit tracked |
| `Overpaid` | Above expected — credited, rolling credit tracked |
| `PendingReview` | Unknown account number → review queue |
| `Reversed` | Bank reversed the transfer → credit backed out |

Internal `reason` flags (not customer-facing): `NameMismatch`, `Underpaid`, `Overpaid`,
`Reversed`, `InvalidAccount`, `Duplicate`, `ManualReview`.

Edge cases handled: exact/under/over (reconcile + credit), **duplicate reference → ignored**
(idempotent, no double-credit), **unknown account → review queue**, **reversal → reverse the
credit**, delayed webhook → processed normally, multiple transfers → each reconciled
independently, name mismatch → credited but flagged.

---

## 7. Security posture

- **Strong passwords** enforced (12–128 chars, upper+lower+digit+special), bcrypt-hashed
  (work factor ≥ 12). API secrets stored as **PBKDF2** hashes; shown once.
- **Verify-before-login**: registration never starts a session; login is blocked (`403`)
  until the email is verified via magic link.
- **Cookie sessions**: dashboard access + refresh tokens are delivered only as HttpOnly,
  Secure, SameSite=Lax cookies (never in a body, unreadable by JS). Access tokens are
  short-lived (~15 min); refresh tokens (~14 days) are **single-use / rotating** and stored
  only as SHA-256 hashes, so replay is detectable and logout revokes them.
- **Rate limiting**: per-IP limits on auth/credential endpoints (login, register, token,
  refresh, reset, OAuth) plus a global safety net; `429` when exceeded. Real client IP is
  taken from Traefik's forwarded headers.
- **CORS**: only configured frontend origins may call the API with credentials.
- Login/token endpoints run a constant-time dummy-hash check → no timing/enumeration leaks.
- Tokens are HMAC-SHA256 JWTs, issuer/audience validated. API tokens (~1h) carry the key mode.
- Strict **tenant isolation** at the data layer; cross-tenant writes blocked at save time.
- Two-plane scoping keeps dashboard and API privileges separate.

---

## 8. Configuration (environment variables)

Set via `.env.local` (local) or real secrets in deployed environments. See `.env.example`.

| Variable | Purpose |
|----------|---------|
| `ConnectionStrings__Default` | PostgreSQL connection string |
| `Jwt__SigningKey` | HMAC signing key (**≥ 32 bytes**) |
| `Jwt__Issuer`, `Jwt__Audience` | Token issuer/audience |
| `Jwt__AccessTokenLifetimeSeconds` / `Jwt__DashboardTokenLifetimeSeconds` | API token / dashboard access-token lifetimes |
| `Auth__BcryptWorkFactor` | Password hashing cost (≥ 12) |
| `Auth__EmailVerificationTtlMinutes` / `Auth__PasswordResetTtlMinutes` | Magic-link validity windows |
| `Auth__RefreshTokenDays` | Refresh-token lifetime (days) |
| `Auth__CookieSecure` | Session cookies require HTTPS (true except plain-HTTP local dev) |
| `Auth__CookieDomain` | Cookie domain (empty = host-only; recommended) |
| `App__BaseUrl` | **Frontend** URL (email-verified, reset-password, OAuth callback pages) |
| `App__ApiBaseUrl` | This **API** URL (email-verification magic-link target) |
| `Cors__AllowedOrigins` | Comma-separated frontend origins allowed with credentials |
| `Resend__ApiKey`, `Resend__FromEmail` | Transactional email |
| `Auth__Google__ClientId` / `__ClientSecret` | Google OAuth login |
| `Auth__GitHub__ClientId` / `__ClientSecret` | GitHub OAuth login |
| `Nomba__*` | Provider credentials (parent account + sub-account) |

---

## 9. Roadmap

Shipped:
- ✅ **Stage 1 — Developer identity & API keys** (email/password, test/live keys, scopes).
- ✅ **Stage 2 — Email verification & password reset** (magic links via Resend).
- ✅ **Stage 3 — Social login** (Google + GitHub OAuth; links to existing email accounts).

Next:
- **Payments:** virtual accounts (NUBANs), charges, payouts, and webhooks (provider-backed).
- **Dashboard hardening:** session/refresh handling, audit log, rate limiting.

_When these ship, this document is updated in the same PR._
