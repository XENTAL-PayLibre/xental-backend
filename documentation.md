# Xental Platform — System & API Documentation

> **Living document.** This is the source of truth for how the Xental backend is used,
> written so the frontend (dashboard + docs site) can be built against it. It is updated
> as the system evolves. Last updated for **Stage 3 — Email verification, password reset,
> and social login (Google/GitHub)**.

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

### 3.1 Register a developer account
```
POST /api/v1/developers/register
{ "name": "Acme Dev", "email": "dev@acme.com", "password": "correct-horse-battery" }
```
Returns a **dashboard token** so the UI is logged in immediately after signup.
- Password must be **≥ 12 characters**.
- Email is normalized (trimmed + lowercased) and must be unique → `409` if taken.

### 3.2 Log in
```
POST /api/v1/developers/login
{ "email": "dev@acme.com", "password": "correct-horse-battery" }
```
Returns a dashboard token. Wrong email or password both return the **same** generic
`401` (no account-enumeration leak).

### 3.3 Create an API key (dashboard token required)
```
POST /api/v1/api-keys        Authorization: Bearer <dashboard-token>
{ "label": "server-prod", "mode": "live" }
```
The response is the **only** time the client secret is ever returned — store it securely.

### 3.4 Exchange the API key for an API token (OAuth2 client-credentials)
```
POST /api/v1/auth/token
{ "clientId": "xnt_live_…", "clientSecret": "sk_live_…" }
```
Returns a short-lived **API token** (default 1 hour). Use it as `Authorization: Bearer …`
on all payments-API calls. Re-request a new token when it expires.

```
Developer ──register/login──▶ dashboard token ──create key──▶ (client id + secret)
(client id + secret) ──/auth/token──▶ API token ──▶ payments API
```

### 3.5 Verify email (magic link)
On register, Xental emails a verification link. Clicking it hits
`GET /api/v1/developers/verify-email?token=…`, which verifies the account and **redirects
the browser** to `{App.BaseUrl}/email-verified?verified=true|false`. The dashboard can
re-send with `POST /api/v1/developers/resend-verification` (dashboard token). Read
`emailVerified` from the profile or dashboard token to know current status.

### 3.6 Forgot / reset password
```
POST /api/v1/developers/forgot-password   { "email": "dev@acme.com" }   -> 202 (always)
```
Emails a reset link to `{App.BaseUrl}/reset-password?token=…`. That frontend page collects
a new password and calls:
```
POST /api/v1/developers/reset-password    { "token": "…", "newPassword": "…" }
```
Requesting a reset always returns `202` regardless of whether the email exists (no
enumeration). Resetting consumes the link and invalidates all other outstanding reset links.

### 3.7 Social login (Google / GitHub)
Browser-based OAuth. Point the browser at:
```
GET /api/v1/auth/oauth/google      (or /github)
```
Xental redirects to the provider; after consent the provider returns to
`GET /api/v1/auth/oauth/{provider}/callback`, which finds-or-creates the account (marking
its email verified) and **redirects to the app** at
`{App.BaseUrl}/auth/callback#token=<dashboard-jwt>` — or `#error=<reason>` on failure. The
app reads the token from the URL fragment. A social login for an email that already has a
password account links to that same account.

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

## 7. Security posture

- Passwords hashed with **bcrypt** (work factor ≥ 12).
- API secrets stored as **PBKDF2** hashes, never in plaintext; shown once.
- Login and token endpoints run a constant-time check against a dummy hash for unknown
  accounts/keys → no timing or enumeration leaks; all failures return one generic message.
- Tokens are HMAC-SHA256 JWTs, short-lived (default 1h), issuer/audience validated.
- Strict **tenant isolation** at the data layer: every query is filtered by the current
  account, and cross-tenant writes are blocked at save time.
- Two-plane scoping keeps dashboard and API privileges separate.

---

## 8. Configuration (environment variables)

Set via `.env.local` (local) or real secrets in deployed environments. See `.env.example`.

| Variable | Purpose |
|----------|---------|
| `ConnectionStrings__Default` | PostgreSQL connection string |
| `Jwt__SigningKey` | HMAC signing key (**≥ 32 bytes**) |
| `Jwt__Issuer`, `Jwt__Audience` | Token issuer/audience |
| `Auth__BcryptWorkFactor` | Password hashing cost (≥ 12) |
| `Auth__EmailVerificationTtlMinutes` | Magic-link validity window (Stage 2) |
| `App__BaseUrl` | Public base URL (used in magic links) |
| `Resend__ApiKey`, `Resend__FromEmail` | Transactional email (Stage 2) |
| `Auth__Google__ClientId` / `__ClientSecret` | Google OAuth login (Stage 3) |
| `Auth__GitHub__ClientId` / `__ClientSecret` | GitHub OAuth login (Stage 3) |
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
