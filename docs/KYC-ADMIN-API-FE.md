# Xental Onboarding & Admin API — Frontend Integration Guide

Everything the FE team needs to build the **developer onboarding wizard** and the **admin console**.
All endpoints are live on the backend (Swagger at `/swagger`). Base URLs:
- Staging: `https://api.staging.xental.online`
- Production: `https://api.xental.online`

---

## 1. The model in one paragraph

**Signup gives a developer immediate Sandbox access** (test API keys, no KYC). To go **Live** (real money,
live keys), the developer completes **Developer KYC** *and* **Business KYB**; an admin reviews and approves
both. Automated checks (BVN/NIN, CAC, bank name-match) run on submit and pre-fill the admin's decision, but
**a human always approves** — the applicant sees **"Under review"** in the meantime.

## 2. Two auth planes

| Plane | Who | Auth | Used by |
|-------|-----|------|---------|
| **Dashboard** | developer | existing **cookie session** (`xnt_access` — send with `credentials: 'include'`) | all `/onboarding/*` + `/api-keys` |
| **Admin** | admin/superadmin | **Bearer token** from admin login | all `/admin/*` |

## 3. Status enums (drive the UI)

- **Tier:** `Sandbox` · `Live`
- **Track status** (developerKycStatus, businessKybStatus): `NotStarted` · `InProgress` · `UnderReview` · `MoreInfoNeeded` · `Approved` · `Rejected`

Suggested UI labels: `NotStarted → "Not started"`, `InProgress → "Draft"`, `UnderReview → "Under review"`,
`MoreInfoNeeded → "Action needed"` (show `decisionReason`), `Approved → "Approved"`, `Rejected → "Rejected"` (show reason).

## 4. Errors

All errors are RFC-7807 **ProblemDetails**: `{ "status", "title", "detail" }`.
Key ones: `400` validation, `401` not authenticated, **`403` `"Onboarding not approved"`** (live key before approval)
or `"Forbidden"` (non-SuperAdmin), `404`, `409` conflict.

---

## 5. Developer onboarding endpoints (dashboard/cookie auth)

### `GET /api/v1/onboarding` — current state (poll this to render the wizard)
```json
{
  "tier": "Sandbox",
  "developerKycStatus": "UnderReview",
  "businessKybStatus": "InProgress",
  "canIssueLiveKeys": false,
  "submittedAtUtc": "2026-07-03T10:00:00Z",
  "decidedAtUtc": null,
  "decisionReason": null
}
```

### `POST /api/v1/onboarding/developer` — submit Developer KYC → returns the status object
```json
{
  "fullName": "Ada Obi",
  "dateOfBirth": "1990-01-01",
  "country": "Nigeria",
  "address": "1 Marina, Lagos",
  "idType": "Bvn",                     // "Bvn" | "Nin"
  "idNumber": "22222222222",           // exactly 11 chars
  "bankName": "EMK Bank",
  "bankCode": "011",                   // NUBAN bank code (map from a bank list)
  "bankAccountName": "Ada Obi",
  "bankAccountNumber": "0123456789",
  "portfolioUrl": "https://github.com/ada",   // optional
  "projectDescription": "A payments app"       // optional
}
```
On success the developer-KYC track moves to `UnderReview`. The id number is encrypted at rest; never returned.

### `POST /api/v1/onboarding/business` — submit Business KYB (steps 1 & 3) → status object
```json
{
  "legalName": "Acme Ltd",
  "registrationNumber": "RC123456",
  "businessType": "LLC",
  "industry": "Finance",
  "country": "Nigeria",
  "address": "1 Marina, Lagos",
  "contactCountryCode": "+234",
  "contactPhone": "7035678999",
  "website": "https://acme.example",   // optional
  "settlementBankName": "EMK Bank",
  "settlementBankCode": "011",
  "settlementAccountName": "Acme Ltd",
  "settlementAccountNumber": "0123456789"
}
```
Moves KYB to `InProgress` (not submitted yet — documents + attestation still required).

### `POST /api/v1/onboarding/documents` — upload a KYB document (**multipart/form-data**)
Fields: `file` (binary), `type` = `CertificateOfIncorporation` | `ProofOfAddress`.
Constraints: **PDF/JPG/PNG, ≤10 MB**. Re-uploading the same `type` replaces it. → `204 No Content`.
```
POST /api/v1/onboarding/documents
Content-Type: multipart/form-data
  file: <binary>
  type: CertificateOfIncorporation
```

### `POST /api/v1/onboarding/submit` — attest + submit KYB for review → status object
```json
{ "attestationAccepted": true }
```
Requires **both** documents present and `attestationAccepted: true`; else `400`. Moves KYB to `UnderReview`.

### `POST /api/v1/api-keys` — issue an API key (existing endpoint, now gated)
```json
{ "label": "prod key", "mode": "live" }   // "test" | "live"
```
- `mode: "test"` → always allowed (Sandbox).
- `mode: "live"` → **`403 "Onboarding not approved"`** until `tier == "Live"`. Gate the "Create live key" button on `canIssueLiveKeys`.

**Wizard flow:** `GET /onboarding` to render → `POST /developer` → `POST /business` → upload 2 docs → `POST /submit` →
show **"Under review"** → poll `GET /onboarding` → when `tier == "Live"`, enable live keys.

---

## 6. Admin endpoints (Bearer token)

### `POST /api/v1/admin/auth/login` — admin login (anonymous)
```json
{ "email": "admin@xental.online", "password": "…", "totpCode": "123456" }
```
`totpCode` is required only if the admin has MFA enrolled. Response:
```json
{ "accessToken": "eyJ…", "tokenType": "Bearer", "expiresIn": 900, "email": "admin@xental.online", "role": "SuperAdmin" }
```
Send `Authorization: Bearer <accessToken>` on all subsequent admin calls. Rate-limited.

### `POST /api/v1/admin/mfa/enroll` — enroll TOTP for the current admin
Returns `{ "otpAuthUri": "otpauth://totp/…" }` — render as a QR code for Google Authenticator/Authy.
After enrollment, MFA is required at next login.

### `GET /api/v1/admin/onboarding?status=UnderReview` — review queue
`status` is an optional track-status filter (usually `UnderReview`). Response: array of
```json
{ "tenantId": "…", "tenantEmail": "dev@x.com", "tier": "Sandbox",
  "developerKycStatus": "UnderReview", "businessKybStatus": "UnderReview", "submittedAtUtc": "…" }
```

### `GET /api/v1/admin/onboarding/{tenantId}` — full review detail
```json
{
  "summary": { "tenantId": "…", "tenantEmail": "…", "tier": "Sandbox",
               "developerKycStatus": "UnderReview", "businessKybStatus": "UnderReview", "submittedAtUtc": "…" },
  "checks": [
    { "kind": "Bvn",   "outcome": "Verified", "provider": "dojah", "detail": "name match",    "checkedAtUtc": "…" },
    { "kind": "Nuban", "outcome": "Mismatch", "provider": "nomba", "detail": "name mismatch", "checkedAtUtc": "…" },
    { "kind": "Cac",   "outcome": "Error",    "provider": "dojah", "detail": "not found",      "checkedAtUtc": "…" }
  ],
  "documents": [
    { "type": "CertificateOfIncorporation", "reviewStatus": "Pending", "downloadUrl": "https://…(10-min presigned)" },
    { "type": "ProofOfAddress",             "reviewStatus": "Pending", "downloadUrl": "https://…" }
  ]
}
```
- `checks[].outcome`: `Verified` (green) · `Mismatch` (amber — look closer) · `Error` (grey — couldn't verify, e.g. provider not configured).
- `documents[].downloadUrl` is a **short-lived (10 min)** presigned URL — fetch fresh each time; don't cache.

### `POST /api/v1/admin/onboarding/{tenantId}/approve|reject|request-info`
Body for all three:
```json
{ "track": "DeveloperKyc", "reason": "id photo unclear" }   // track: "DeveloperKyc" | "BusinessKyb"
```
- `approve` → `reason` ignored; approving **both** tracks flips the tenant to **Live**.
- `reject` / `request-info` → `reason` **required** (else `400`). `request-info` sets the track to `MoreInfoNeeded`
  (applicant sees "Action needed" + the reason and can resubmit). All actions return `204` and are audit-logged.

### `POST /api/v1/admin/admins` — create an admin (**SuperAdmin only** → else `403`)
```json
{ "email": "ops@xental.online", "password": "≥12 chars", "role": "Admin" }   // "Admin" | "SuperAdmin"
```
→ `201 Created { "id": "…" }`. Duplicate email → `409`.

---

## 7. Admin console flow
1. Login (`/admin/auth/login`) → store token in memory; refresh by re-login on 401.
2. First-time admin: prompt `/admin/mfa/enroll` → QR → confirm; thereafter require `totpCode`.
3. Queue: `GET /admin/onboarding?status=UnderReview`.
4. Open a tenant: `GET /admin/onboarding/{tenantId}` → show fields, the **check results** (colour-coded), and the
   **documents** (render PDFs/images from the presigned URLs).
5. Decide **per track**: approve / reject (reason) / request-info (reason). When both tracks are approved the tenant
   is Live automatically — no separate action.

## 8. Notes for both apps
- **Cookies vs bearer:** developer/onboarding calls rely on the session cookie (`credentials: 'include'`,
  same-site under `SameSite=Lax`); admin calls use the bearer token — don't mix them.
- **Polling:** after `submit`, poll `GET /onboarding` (e.g. every 30–60 s) or refetch on focus to reflect admin decisions.
- **Provider not configured (staging/pre-prod):** identity checks may come back `Error` ("not found") — that's expected;
  the admin still approves from the documents. Nothing to handle specially in the UI beyond showing the outcome.
```
