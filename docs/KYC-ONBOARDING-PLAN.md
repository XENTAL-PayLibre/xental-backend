# KYC / Onboarding, Live-Key Provisioning & Admin Reconciliation Terminal — Implementation Plan (v2)

> **Status:** Draft for a second review. Nothing built yet.
> **Target (confirmed):** **Xental**. Build begins after your review sign-off.
> **Source:** KYC spec — *Business KYB (4 steps)* + *Developer KYC (4 sections)* + sandbox→live gating.

---

## 0. The rule (as you refined it)

```
Email/password signup ─────────────▶  SANDBOX access (test keys) — immediate, no KYC
                                       │
                       Developer KYC + Business KYB
                       (auto-verify what we can; everything
                        else → ADMIN REVIEW, user sees "Under Review")
                                       │
                                       ▼
                                     LIVE access (live keys, real money)
```

- **Simple signup is enough for the sandbox.** No KYC to get test keys. (Xental already issues test keys on a verified developer account — we just formalize it as the *Sandbox* tier.)
- **Live requires BOTH Developer KYC and Business KYB, approved.** Real money is disabled until then.
- **Automate everything automatable** (BVN/NIN, CAC/RC, NUBAN name-match). **Anything we can't automate — documents, name mismatches, edge cases — goes to admin review, and the applicant sees a clear "Under Review" state** until an admin decides.

---

## 1. Two accounts, two tracks

An **account = a business** registering on the infrastructure (the `Tenant`). Going live needs proof of **both** the operator and the company:

### Track A — Developer KYC (the human operating the account)
| Section | Fields | Auto-check |
|---|---|---|
| Personal identity | Full legal name, DOB, country + address | — |
| Regulatory ID | **BVN or NIN** | Identity API → name/DOB match vs govt DB → **auto**; mismatch/error → **review** |
| Developer bank | Bank name, account name, account number | **NUBAN lookup** (existing Nomba client) → name must match BVN/NIN → **auto**; mismatch → **review** |
| Profile & intent | GitHub/GitLab/portfolio URL, "what are you building?" | Heuristic risk signal (filters bad actors) → feeds **review** |

### Track B — Business KYB (the company) — *the business signup*
| Step | Fields | Auto-check |
|---|---|---|
| 1. Business info | Legal name, **RC/registration number**, business type, industry, country, address, contact (country code, phone, website) | **CAC/RC verification API** → **auto**; not found/mismatch → **review** |
| 2. Documents | **Certificate of Incorporation**, **Proof of Address** (PDF/JPG/PNG, ≤10 MB, encrypted) | **Cannot be fully automated → always admin review** (OCR/authenticity as an assist only) |
| 3. Account details | Settlement bank name, account name, account number | **NUBAN lookup** → name vs business/CAC name → **auto**; mismatch → **review** |
| 4. Review & submit | **Attestation** checkbox (accuracy + ToS/Privacy) | — |

> The business signup is a **guided wizard** mirroring these 4 steps + the developer-KYC step, with progress saved between steps and a final attestation. Applicants can start in sandbox and complete this whenever they want to go live.

---

## 2. Onboarding state machine (+ the "Under Review" UX)

One `OnboardingApplication` per tenant. Each **track** (DevKyc, Kyb) and each **check** has a status; the application's overall status is what the user sees.

```
Track status:  NotStarted → InProgress → Submitted → UnderReview → Approved
                                                          │            └─(both tracks Approved ⇒ tier=LIVE)
                                              ┌───────────┼───────────┐
                                              ▼           ▼           ▼
                                          Approved   MoreInfoNeeded  Rejected
                                                     (re-open to user) (locked; appeal)
```

- **Auto-verifiable items** flip to `Verified` without a human. If **every** required item for a track is auto-`Verified`, the *documents* still force the track into **`UnderReview`** (docs are never auto-approved), so **live always has a human final sign-off** — matching the spec's 1–2 business-day review, but fast because all automated evidence is pre-attached.
- **User-facing status** is deliberately simple: `Not started` · `In progress` · **`Under review`** · `Action needed` (MoreInfoNeeded, with the specific field) · `Approved` · `Rejected`.
- Every transition is written to an **append-only audit log** (actor, reason, timestamp).

---

## 3. Data model (new EF entities; incremental migrations, never regenerate InitialCreate)

Tenant-owned (row-level `TenantId` + existing global query filters + write-time enforcement). PII columns encrypted at rest.

- **`OnboardingApplication`** — `TenantId`, `Tier` (Sandbox/Live), `DevKycStatus`, `KybStatus`, `SubmittedAtUtc`, `ReviewedByAdminId`, `DecisionReason`.
- **`DeveloperKyc`** — full name, DOB, country, address, **BVN/NIN (encrypted)**, id-type, bank {name, accountName, accountNumber}, portfolio/GitHub URL, project description.
- **`BusinessKyb`** — legal name, RC number, business type, industry, country, address, contact {countryCode, phone, website}, settlement bank {name, accountName, accountNumber}, attestation {accepted, atUtc, ip}.
- **`KycDocument`** — `TenantId`, type, **object key + SHA-256 content hash**, mime, size, uploadedAtUtc, reviewStatus. **Bytes live in object storage (MinIO now / S3 later — §7c), never Postgres.**
- **`VerificationCheck`** — `TenantId`, provider, kind (BVN/NIN/NUBAN/CAC/Face), requestId, **encrypted raw result**, `Outcome` (Verified/Mismatch/Error/ManualNeeded), score, checkedAtUtc. Immutable — the evidence trail for approvals/appeals.
- **`AdminUser`** + **`AdminAuditLog`** — admin identities (separate from tenants), RBAC roles, MFA; every admin action logged (actor, action, target, before/after, atUtc).
- **Extend existing:** `ApiKey` already carries test/live `Mode` — add the **live-issuance guard**. `Tenant` gains `Tier` + optional per-tenant `TierLimit`. `SettlementConfig` is **pre-filled from the approved KYB** settlement account (single source of truth for payouts).

Value objects for `Bvn`, `Nin`, `Rc`, `Nuban` (validation + normalization at the type boundary), consistent with the existing `Money` value type.

---

## 4. Sandbox vs Live gate

- **Signup → Sandbox tier immediately.** Test API keys issuable at once; sandbox uses the Nomba **sandbox** only.
- **Sandbox is strictly ZERO real money.** Live keys cannot be issued, and the live money paths refuse a sandbox-tier tenant outright (not "capped" — **disabled**). No production Nomba credentials are ever reachable from a sandbox key.
- **Live keys are hard-gated:** `POST /api/v1/api-keys {mode:"live"}` → **403 `OnboardingNotApproved`** unless `Tier == Live`. On admin approval, unlock live-key issuance (optionally auto-mint the first live key).
- **Live volume caps:** per-tenant configurable daily/monthly limits (default high); a deposit (`NombaWebhookService`) or payout (`TransferService`) over the cap → routed to `PendingReview` / blocked, never silently processed.
- **Proper API rate limiting (all tiers):** move beyond today's per-IP auth throttle to **per-API-key + per-tenant** rate limits on the API plane (sliding/token-bucket, e.g. req/sec + burst + daily quota), with `429` + `Retry-After` and standard `RateLimit-*` headers. Sandbox keys get tighter default quotas than live. Identity-provider calls are separately metered per tenant (§7). Built on the existing `AddRateLimiter` infrastructure with a keyed partition per credential.
- **Kill-switch:** admin `SuspendTenant` → all keys inert immediately.

---

## 5. Automation — and what falls to admin review

An `IIdentityVerifier` **port** (mirrors `INombaClient`), so providers are swappable and fully mockable in tests.

### Providers (Nigeria)
| Provider | BVN | NIN | CAC/RC | Role |
|---|---|---|---|---|
| **Dojah** | ✅ | ✅ | ✅ | **Recommended primary** (BVN/NIN/CAC data checks) |
| Prembly / QoreID / Youverify | ✅ | ✅ | ✅ | Redundancy / fallback |
| **Nomba (existing)** | — | — | — | **NUBAN account-name lookup** (already integrated) |

> **No face / liveness / biometrics.** Identity confidence comes from the BVN/NIN + CAC **data** checks and the **NUBAN name-match**; human assurance comes from the **admin reviewing the uploaded documents** (Cert of Incorporation, Proof of Address), not a face scan.

### Decision flow
```
Developer KYC submitted
  → BVN/NIN lookup           → Verified | Mismatch/Error → REVIEW
  → NUBAN name-match (Nomba) → Verified | Mismatch       → REVIEW
  → profile heuristic        → risk signal → REVIEW if elevated

Business KYB submitted
  → CAC/RC verification      → Verified | NotFound/Mismatch → REVIEW
  → settlement NUBAN match   → Verified | Mismatch          → REVIEW
  → Cert of Incorp + Proof of Address → ALWAYS REVIEW (docs)

If any item is REVIEW ⇒ application → "Under Review" (admin).
All auto-items Verified ⇒ still "Under Review" for the doc sign-off (live only).
Admin approves ⇒ tier=LIVE.
```
- **Name matching reuses the engine's fuzzy `NameMismatch` logic** (normalized, threshold-based — not exact string equality) so "EMMA O." ≈ "Emma Okonkwo".
- Every provider call is **idempotent, rate-limited, retried with backoff**, timeout-bounded, and its raw response stored (encrypted) — same discipline as the Nomba client.
- **Nothing auto-approves live.** Automation shrinks the manual queue and pre-attaches evidence; a human always signs off real-money access.

---

## 6. Admin backend (Xental) — KYC review + reconciliation **API** (FE builds the console)

A **separate admin plane**: new `AuthPolicies.Admin`, `AdminUser` identities (not tenants), **RBAC** (Reviewer / ReconOps / SuperAdmin), **mandatory MFA (TOTP)**, and full audit. I deliver the **API + auth + data**; the FE team builds the console UI against it. The capabilities each endpoint set must expose:

### 6.1 KYC / onboarding review (data the console needs)
- **Queue** by status (Under Review first) with SLA timers (1–2 day window) and filters.
- **Detail:** all submitted fields + **document access** (short-lived presigned MinIO/S3 URLs the UI renders) + the **auto-check results** (BVN/NIN/CAC/NUBAN outcomes + scores), so a decision takes seconds.
- **Actions:** Approve (tier up) · Reject (reason) · Request more info (re-opens the specific field to the user; status → *Action needed*) · Re-run a check · Suspend tenant. All logged; PII **access** logged too.

### 6.2 Reconciliation console (Xental's core; reads what the engine already produces)
- **Buckets:** `PendingReview` / `ManualReview` transactions, unknown-account deposits (`InvalidAccount`), **overpaid/underpaid**, **high-risk (RiskEvaluator ≥ 70)**, **reversals**.
- **Manual actions:** match an unknown deposit to a `VirtualAccount`, force-reconcile / accept, issue a refund (`Transfer`), mark reviewed, replay a failed webhook/settlement.
- **Settlements:** fully-paid-but-unsettled accounts + failed auto-settlements (from `SettlementWorker`) with one-click retry.
- **Fraud triage:** payer-name-reuse / velocity signals already computed by `RiskEvaluator`.
- **Platform analytics:** cross-tenant collection rate, outstanding, review backlog (extends `InsightsService`).

Every recon action is audited and, where it moves money, idempotent.

---

## 7. Security considerations (first-class, not bolted on)

- **PII encryption at rest:** BVN/NIN and raw verification payloads via AES-GCM column encryption (reuse `AesSecretProtector`); documents in **object storage (MinIO now, S3 later — §7c)**, retrieved only via short-TTL **presigned** URLs, never public, never proxied through logs; server-side encryption enabled on the bucket.
- **Minimize raw BVN retention:** store a **hash + provider check-reference** as the durable record; keep the raw value only as long as a live review needs it, then purge.
- **NDPR (Nigeria) + GDPR posture:** documented lawful basis, **data-retention schedule**, right-to-erasure workflow, and a signed **DPA with each identity provider**.
- **Admin plane isolation:** distinct auth scheme + `AuthPolicies.Admin`, RBAC, **TOTP MFA**, IP allow-list optional, **separate from the tenant/dashboard/API planes** already in place. A "view PII" permission is separate from "review".
- **Tamper-evidence:** document content hashes; **append-only** audit logs; PII-access logging.
- **Abuse/cost control:** identity-API calls **rate-limited per tenant** (prevent enumeration + runaway spend); provider credentials are secrets (GitHub Environment secrets → rendered env, same as today), never in the repo.
- **Idempotency everywhere money or state moves:** live-key issuance, refunds, settlement retries, and each provider call are idempotent + replay-safe.
- **Least-privilege data access:** query filters keep tenants isolated; admin cross-tenant reads go through the audited admin plane only.
- **Transport & secrets:** all provider calls over TLS; webhooks from providers (if any) HMAC-verified like the Nomba receiver.

## 7b. Code quality & engineering standards (match the existing codebase)

- **Clean Architecture preserved:** Domain (entities/value objects, no deps) → Application (use-case services + ports like `IIdentityVerifier`) → Infrastructure (provider adapters, S3, EF) → Api (controllers/DTOs). No provider SDK leaks into Domain/Application.
- **Ports + adapters** so every external system (Dojah, Smile, S3, Nomba) is an interface with a **fake for tests** — no network in unit tests (SQLite + fakes, as today).
- **Incremental EF migrations only** (never regenerate `InitialCreate`); auto-migrate on startup as now.
- **Tests are part of "done":** unit tests for the state machine, gating, name-match, cap enforcement; integration tests for the onboarding + admin endpoints; the suite stays green (current: 100 tab). Security-relevant paths (gate bypass, cross-tenant, PII access) get explicit negative tests.
- **Consistent conventions:** value objects for BVN/NIN/RC/NUBAN (validation at the boundary, like `Money`); `ProblemDetails` error mapping; DTO validation; structured logging that **never logs PII/secrets**; the existing dual-plane auth pattern extended, not forked.
- **CI stays green:** gitleaks/Trivy (already fixed) cover the new code; no secrets committed; provider keys via env.
- **Small, reviewable phases** (below), each shippable behind a flag.

## 7c. Document storage — MinIO now, seamless S3 later

- **One port, `IDocumentStorage`** (Application layer): `Task<PresignedUpload> CreateUploadUrlAsync(...)`, `Task<Uri> CreateDownloadUrlAsync(key, ttl)`, `Task DeleteAsync(key)`. Domain/Application never know which backend is behind it.
- **One adapter using the AWS SDK for .NET (`AWSSDK.S3`)** — MinIO is **S3-API-compatible**, so the *same* client code talks to MinIO today and real AWS S3 later. **Switching is config only**, no code change:
  - MinIO: `ServiceURL=http://minio:9000`, `ForcePathStyle=true`, MinIO access/secret keys.
  - S3: drop `ServiceURL`/`ForcePathStyle`, set region + IAM creds (or instance role). SSE-KMS on the bucket.
- **Presigned uploads/downloads** work identically on both, so the browser uploads/downloads directly (bytes never transit the API), and download URLs are short-TTL.
- **Infra:** add a **`minio`** service to the compose stack (persistent volume, credentials as GitHub Environment secrets, private network only — never published), mirroring how `postgres` is wired. A one-time bucket-create init. When you move to S3, delete the service and flip the env vars.
- **Config surface:** `Storage__Provider` (`minio`|`s3`), `Storage__Endpoint`, `Storage__Bucket`, `Storage__AccessKey`/`Storage__SecretKey` (secrets), `Storage__Region`. That's the entire switch.

---

## 8. API surface (new) — endpoints only; the frontend/admin UI is the FE team's job

**Onboarding (dashboard plane, tenant-scoped)**
- `GET /api/v1/onboarding` — application, tier, per-track status, what's outstanding, user-facing state.
- `POST /api/v1/onboarding/developer` — submit/patch developer KYC → triggers BVN/NIN + NUBAN checks.
- `POST /api/v1/onboarding/business` — submit/patch KYB (steps 1–3).
- `POST /api/v1/onboarding/documents` — presigned upload (MinIO/S3) for cert of incorporation + proof of address.
- `POST /api/v1/onboarding/submit` — attestation + move to Under Review.
- `POST /api/v1/api-keys {mode:"live"}` — **gated** (403 until `Tier == Live`).

**Admin (new admin plane) — endpoints only; UI is built by the FE team**
- `GET /api/v1/admin/onboarding?status=` · `GET …/{tenantId}` · `POST …/approve|reject|request-info|rerun-check|suspend`.
- `GET /api/v1/admin/reconciliation?bucket=review|unknown|overpaid|highrisk|reversals` · `POST …/{txn}/match|accept|refund|reviewed`.
- `GET /api/v1/admin/settlements/failed` · `POST …/{id}/retry` · `GET /api/v1/admin/audit`.

> I deliver these as a clean, documented (Swagger) admin API + auth plane. **No admin UI from me** — the frontend team builds both the applicant onboarding wizard and the admin console against these endpoints.

---

## 9. Phased rollout (build order after sign-off)

All phases are **backend/endpoints only**. The FE team builds every UI (applicant wizard + admin console) against them.

| Phase | Deliverable |
|---|---|
| **1. Tier + gate** | `OnboardingApplication`, Tier on Tenant/ApiKey, **live-key 403 gate**, sandbox = zero-money enforcement, migrations, tests. Signup already = Sandbox. |
| **2. Developer KYC + automation** | Dev KYC endpoints, `IIdentityVerifier` (Dojah) + NUBAN match (Nomba), auto-verify → else Under Review. |
| **3. Business KYB + documents** | KYB endpoints + wizard model, **MinIO** (S3-compatible) presigned uploads via `IDocumentStorage`, CAC verification, attestation. |
| **4. Admin API — KYC review** | Admin plane + RBAC + MFA + onboarding review **endpoints** (list/detail/approve/reject/request-info/rerun/suspend) → tier=Live → live keys. |
| **5. Admin API — reconciliation** | Reconciliation **endpoints** over existing engine buckets + manual match/refund/retry + settlement retry + audit. |
| **6. Rate limiting + caps + hardening** | Per-key/per-tenant API rate limits (§4), live volume caps, kill-switch, NDPR retention/erasure, provider redundancy, PII purge job. |

Phases 1–3 are the applicant-facing backend; 4–5 are the admin backend; 6 hardens. **No phase builds UI.**

---

## 10. Reused (keeps build cost + risk low)
`Tenant`, `ApiKey` (test/live), `SubMerchant`, `SettlementConfig`, `RiskEvaluator`/`NameMismatch`, the reconciliation statuses the engine already emits, `INombaClient.LookupBankAccountAsync` (NUBAN), `AesSecretProtector`, the dual-plane auth, Resend email, and the CI/deploy pipeline.

---

## 11. Settled vs still-open

**Settled (from your reviews):** target = **Xental** · sandbox = **simple signup, zero real money** · live = **KYC + KYB, admin-reviewed** · **no face/liveness** (documents reviewed instead) · storage = **MinIO now, seamless S3 later** · **endpoints only, FE builds the UI** · **proper per-key/per-tenant rate limiting**.

**Still-open (nice to confirm, non-blocking):**
1. **Identity vendor:** go with **Dojah** (BVN/NIN/CAC), or do you have an existing contract/vendor?
2. **Admins & MFA:** how many admin users initially, and is **TOTP MFA** acceptable for the admin plane?
3. **Live volume caps:** concrete default daily/monthly ₦ limits for live tenants (I'll set sensible defaults + per-tenant override if unspecified).
4. **"Action needed" loop:** on *request more info*, re-open just the failing field(s) (my lean) or the whole track?

---

*Say the word after this review and I'll start Phase 1 (tier + live-key gate + sandbox zero-money) in Xental — endpoints only, tests included.*
