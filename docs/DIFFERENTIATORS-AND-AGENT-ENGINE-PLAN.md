# Xental — Differentiators & Agent-Native Engine Plan

Final implementation plan for the next build wave on top of the shipped Nomba
engine. It covers **three differentiator features** plus an **agent-native
integration layer** so developers can point their AI agents at Xental and wire it
into their own apps.

> **Scope for this wave: the backend engine only.**
> We build domain + application + API (the northbound engine and the MCP/agent
> surface). We do **not** build the production/UX layer yet — no hosted pages, no
> dashboard/admin UI, no agent marketplace. Those are called out under
> [§8 Out of scope](#8-out-of-scope-deferred-to-the-production-layer) and come in
> the following wave. The FE team consumes the endpoints; agents consume the MCP
> tools + OpenAPI.

> Builds on: [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md) (core engine),
> [KYC-ONBOARDING-PLAN.md](KYC-ONBOARDING-PLAN.md) (live-access gating),
> and `documentation.md` §10 (the original differentiator sketch, now superseded
> by this document).

---

## 1. Foundation we build on (already shipped)

| Capability | Component we extend |
|---|---|
| Inbound reconciliation + classification | `NombaWebhookService.ProcessAsync` (idempotent on reference) |
| Reconciliation/domain events fan-out | `OutboundEventPublisher` (emits `deposit.reconciled`, etc.) |
| Auto-settlement sweep of collected funds | `SettlementWorker` (background sweep → Nomba transfers) |
| Payment lifecycle | `PaymentState` (UNPAID · PARTIALLY_PAID · FULLY_PAID · OVERPAID) |
| Money model | integer **kobo**, never floats |
| Multi-tenancy | row-level `TenantId` + EF global query filters + write-time enforcement |
| Auth planes | Dashboard (cookie), **API** (bearer `scope=api`), Admin (bearer `scope=admin`) |
| Abuse control | per-key/per-tenant rate limiting (`api-key` policy), sandbox = strictly zero money |
| API discoverability | Swagger/OpenAPI `v1` already served |
| Live-access gating | API keys mint Live only after admin-approved KYC/KYB |

**These are our extension points. We subscribe to and extend them — we never alter
the verdict path, the money model, tenant isolation, or the Nomba webhook
signature/response contract.**

---

## 2. Design invariants (apply to every item below)

1. **Additive-only migrations.** New tables via incremental migrations (like
   `AddSettlement`/`AddOnboarding`); existing tables gain at most **nullable**
   columns. Never regenerate `InitialCreate`.
2. **Opt-in per tenant.** Each feature sits behind a per-tenant flag. A tenant that
   has not opted in behaves **exactly as today**.
3. **Hook, don't rewrite.** Extend `SettlementWorker` / `OutboundEventPublisher` /
   `NombaWebhookService` at their existing seams; the reconciliation verdict is
   computed once and never re-derived by a new feature.
4. **Post-commit side effects.** Anything reactive (rules, notifications, splits)
   runs **after** the reconciliation transaction commits, so a failure in a new
   feature can never corrupt a balance.
5. **Idempotent by construction.** Every money-moving action carries a
   deterministic idempotency key; retries are safe.
6. **Sandbox = zero real money**, always. New tools/endpoints honour the same
   sandbox isolation and rate limits as the rest of the API.
7. **Each item ships with its own flag, its own migration, and its own tests.**

---

## 3. Feature 1 — Split & Escrow Settlement

Fan a reconciled deposit out to multiple beneficiaries (percentage or flat, with an
optional platform-fee skim), optionally holding funds in **escrow** until a release
condition fires.

**Domain / tables (new, additive)**
- `settlement_splits` — `tenantId, virtualAccountId?, beneficiaryType, beneficiaryBank, beneficiaryAccount, shareBps|flatKobo, priority, enabled`.
  A rule set at tenant level (default) or overridden per virtual account.
- `escrow_holds` — `tenantId, virtualAccountId, amountKobo, state (Held|Released|Cancelled), releaseCondition, heldAtUtc, releasedAtUtc`.

**Integration (one branch inside `SettlementWorker`)**
- When splits exist for the account, the sweep creates **N idempotent transfers**
  (`settle-{accountId}-{seq}-{i}`) whose sum equals the net collected amount.
- No splits configured → the current single-sweep path runs **unchanged**.
- An account with an active escrow hold is **skipped** by the sweep; releasing the
  hold re-enqueues it.

**Safety**
- Per-leg idempotency keys.
- **All-or-nothing assertion:** `Σ(legs) == net` is asserted before *any* leg is
  dispatched; a mismatch aborts the whole settlement and alerts (never partial).
- Platform-fee skim is just another leg — it goes through the same assertion.

**API surface (backend)**
- `GET /api/v1/settings/splits` · `PUT /api/v1/settings/splits`
- `GET /api/v1/settlements/{ref}` (now includes leg breakdown)
- `POST /api/v1/settlements/{ref}/release` (release an escrow hold)

**Feature flag:** `tenant.features.split_settlement`.

**Tests:** split sums to net; fee skim; priority ordering; escrow skip→release;
mismatch aborts atomically; no-splits path byte-for-byte unchanged; idempotent retry.

---

## 4. Feature 2 — Live Checkout (real-time reconciliation status)

A payer/integrator can subscribe to an account's reconciliation status and see
**"Payment received ✓"** the instant the webhook reconciles — no polling.

> **Backend-only scope:** we build the **checkout-session API + the SSE stream
> endpoint**. The *hosted HTML pay-page* is production-layer UX and is deferred.

**Integration (read-only, never writes money)**
- New in-process `IReconciliationNotifier` subscribes to the `deposit.reconciled`
  event that `OutboundEventPublisher` **already emits** and pushes it to any open
  streams for that account ref.
- `GET /api/v1/checkout/{token}/stream` — **SSE**, anonymous, scoped to a single
  account ref by an opaque, expiring token. Emits `state` events
  (`unpaid`→`partially_paid`→`fully_paid`/`overpaid`).
- Fallback: `GET /api/v1/checkout/{token}` returns the current `PaymentState`
  snapshot (for clients that can't hold an SSE connection).

**Safety**
- Pure read/notify path — a failure here **cannot** affect money movement.
- Token is scoped, expiring, and reveals only the payment state (no PII, no amounts
  beyond expected/received for that one account).
- Streams are capped per token and per tenant (rate-limit + max-connections) to keep
  it abuse-resistant.

**Domain / tables (new, additive)**
- `checkout_sessions` — `tenantId, virtualAccountId, token, expiresAtUtc, state` (metadata for the session; optional).

**API surface (backend)**
- `POST /api/v1/checkout/sessions` → `{token, streamUrl, snapshotUrl, expiresAtUtc}`
- `GET  /api/v1/checkout/{token}` · `GET /api/v1/checkout/{token}/stream`

**Feature flag:** `tenant.features.live_checkout`.

**Tests:** SSE emits on reconcile; expired/invalid token → 404; snapshot fallback;
no money-path interaction (reconciliation output identical with/without a subscriber);
connection cap enforced.

---

## 5. Feature 3 — Money Rules Engine

Declarative *if-this-then-that* on inflows: overpaid → auto-refund the excess;
underpaid within X% → auto-accept; risk ≥ N → hold; fully paid → notify.

**Domain / table (new, additive)**
- `money_rules` — `tenantId, trigger, conditionJson, action, actionParamsJson, enabled, priority`.

**Integration (append-only, post-commit)**
- `RuleEngine.EvaluateAsync(account, txn)` runs at the **end** of `ProcessAsync`,
  **after** the reconciliation transaction commits. Rules react to the outcome; they
  **never** change the classification.
- Actions **reuse existing primitives** — they add no new money mechanics:
  - `Refund` → a `Transfer` (idempotent).
  - `Hold` → an escrow hold (Feature 1).
  - `Notify` → an `OutboundEventPublisher` event.
- No rules configured → the engine is a **no-op**.

**Safety**
- Post-commit evaluation: a rule failure can't corrupt reconciliation.
- Each action is independently idempotent (`rule-{ruleId}-{txnRef}`) and
  **audit-logged** (which rule fired, inputs, action, result).
- Rules are evaluated in `priority` order; a `stop` outcome short-circuits.

**API surface (backend)**
- `GET /api/v1/rules` · `POST /api/v1/rules` · `DELETE /api/v1/rules/{id}`
- `POST /api/v1/rules/{id}/simulate` — dry-run a rule against a synthetic txn (no side effects) — doubles as an agent tool (see §6).

**Feature flag:** `tenant.features.money_rules`.

**Tests:** each trigger/action; priority + stop; refund excess exactness; risk-hold;
idempotent re-fire; rule failure isolated from reconciliation; simulate has zero side effects.

---

## 6. Agent-Native Integration Layer ("the agents part")

**Goal:** a developer (or their AI agent — Claude Code, Cursor, etc.) can connect to
Xental and **wire payments into their app without reading a PDF**. The agent
discovers Xental's capabilities, integrates against the **sandbox** end-to-end
(strictly zero money), and hands back working code. This is a **backend engine
component** — an MCP server + a machine-readable API surface. No UI.

### 6.1 Xental MCP server
A Model Context Protocol server that exposes the northbound engine as **typed agent
tools**. Deployed as a first-class backend service (streamable-HTTP MCP transport),
authenticated with a **scoped agent token** minted from an API key.

Tool catalogue (thin, safe wrappers over existing endpoints — no new money paths):

| MCP tool | Wraps | Notes |
|---|---|---|
| `xental.capabilities` | OpenAPI + feature flags | what this key can do, which features are on |
| `xental.create_tenant` | `POST /tenants` | sub-merchant provisioning |
| `xental.create_virtual_account` | `POST /virtual-accounts` | persistent NUBAN + optional expected amount |
| `xental.get_account_state` | `GET /virtual-accounts/{ref}` | balance + `PaymentState` |
| `xental.list_transactions` | `GET /transactions` | reconciled inflows/outflows |
| `xental.configure_splits` / `configure_rules` | Features 1 & 3 | set up split/escrow + money rules |
| `xental.simulate_payment` | **sandbox sim** (§6.3) | drive a fake inflow, watch it reconcile |
| `xental.subscribe_status` | Feature 2 SSE | stream reconciliation status |
| `xental.get_openapi` | OpenAPI `v1` | hand the agent the full contract |

**Guardrails:** tools are **sandbox-first** and gated by scope — money-moving tools
(transfers, live keys) are unavailable to an agent token unless the tenant has passed
KYC/KYB *and* explicitly granted the scope. Every tool call inherits the existing
per-key rate limits and is audit-logged.

### 6.2 LLM-friendly API surface
- **Rich OpenAPI** (extend the existing Swagger doc): every operation gets a
  `summary`, `description`, request/response **examples**, and an
  `x-agent-hints` extension (idempotency key location, retry semantics, sandbox test
  data). Agents ingest this directly.
- **`/.well-known/llms.txt`** and **`/.well-known/ai-plugin`-style manifest** — a
  compact, link-rich capability map pointing agents at the OpenAPI + quickstart.
- **Structured error envelope** (already partially present): stable
  `{ code, message, details, docsUrl }` so an agent can recover programmatically.
- **Deterministic idempotency**: document + enforce `Idempotency-Key` on all
  money-moving POSTs so an agent's retries are safe.

### 6.3 Sandbox simulation (makes agents self-sufficient)
- `POST /api/v1/sandbox/simulate/deposit` — inject a **fake Nomba inflow** against a
  virtual account (sandbox only), running the *real* reconciliation path so the agent
  sees a genuine `deposit.reconciled` event, split execution, and rule firing —
  **with zero real money and no call to Nomba**.
- This is the keystone: an agent can build *and verify* a full integration
  (create account → simulate payment → observe reconciliation → configure a rule →
  re-simulate) entirely in sandbox before the developer ever touches live keys.

**Security for the agent layer**
- Scoped, revocable **agent tokens** (a constrained API-key grant); never the raw key.
- Sandbox isolation + per-key rate limiting (already built) apply unchanged.
- No PII in tool outputs; KYC/KYB data is never exposed to agent tools.
- Full audit trail of agent tool calls (reuse the admin audit-log pattern).

**Feature flag:** `tenant.features.agent_access`.

**Tests:** MCP tool ↔ endpoint parity; scope enforcement (money tools blocked in
sandbox/without KYC); `simulate_payment` drives real reconciliation with zero money;
OpenAPI examples validate; rate-limit + audit on tool calls.

---

## 7. Sequencing

Build order chosen for lowest risk first and maximum reuse last:

1. **Feature 2 — Live Checkout** — pure read/notify, no money-path change, highest
   demo payoff. Proves the event-subscription seam.
2. **Feature 1 — Split & Escrow** — extends the settlement worker; the biggest
   product leap; introduces escrow (reused by rules).
3. **Feature 3 — Money Rules Engine** — composes refund/escrow/notify primitives
   from the engine + Feature 1, so it reuses everything and lands last.
4. **Agent-Native Layer** — lands **incrementally alongside** 1–3: each feature adds
   its MCP tools + OpenAPI examples as it ships, and the **sandbox simulator + MCP
   server** are built early (right after Feature 2) so every subsequent feature is
   agent-testable from day one.

Each is independently shippable behind its own flag.

---

## 8. Out of scope (deferred to the production layer)

Explicitly **not** in this backend-engine wave:
- Hosted checkout **pay-page UI** (we ship its API + SSE stream only).
- Dashboard / admin **console UI** (endpoints only; FE team owns UI).
- Any agent-facing **marketplace / portal UI**.
- Live Nomba **production** money movement for the new features until the tenant is
  KYC/KYB-approved (gating already enforced).

---

## 9. Cross-cutting: testing & security

- **Testing:** unit tests per service + integration tests through the real HTTP loop
  (the existing xUnit + SQLite-in-memory + `XentalApiFactory` pattern). Every feature
  proves both its happy path **and** that the untouched reconciliation/settlement
  output is byte-for-byte identical when the feature is off.
- **Security:** additive nullable columns only; per-tenant opt-in; post-commit side
  effects; idempotent money actions; scoped agent tokens; sandbox zero-money
  invariant; per-key rate limiting; full audit logging of splits/rules/agent calls.
- **Migrations:** one incremental migration per feature
  (`AddSplitSettlement`, `AddCheckoutSessions`, `AddMoneyRules`, `AddAgentAccess`),
  auto-applied on startup.

---

## 10. New tables & flags at a glance

| Feature | New tables | Feature flag |
|---|---|---|
| Split & Escrow | `settlement_splits`, `escrow_holds` | `split_settlement` |
| Live Checkout | `checkout_sessions` | `live_checkout` |
| Money Rules | `money_rules` | `money_rules` |
| Agent layer | *(none — scoped grants + audit reuse)* | `agent_access` |

All columns added to existing tables are **nullable and additive**. No existing
behaviour changes when a flag is off.
