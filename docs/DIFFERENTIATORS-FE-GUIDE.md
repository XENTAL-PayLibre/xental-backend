# Xental Differentiators — Frontend Integration Guide

How the FE team consumes the four new backend capabilities. All endpoints are live on
**staging** (`https://api.staging.xental.online`). Money is always integer **kobo** (₦1 = 100 kobo).

Auth planes (unchanged):
- **Dashboard plane** — the logged-in merchant session (HttpOnly cookie). Used for *configuration*
  (splits, rules).
- **API plane** — `Authorization: Bearer <api-token>` from `POST /api/v1/auth/token`. Used for
  *runtime* actions (checkout sessions, escrow, sandbox).

> **Local dev cookies:** on staging, a login sent with `Origin: http://localhost:3000` gets a
> host-only, non-secure, `SameSite=Lax` cookie so your Next.js dev server/proxy can hold the
> session. Call the API with `credentials: 'include'` (fetch) / `withCredentials: true` (axios).

---

## 1. Live Checkout — real-time "Payment received ✓"

Show a payer a hosted status that flips to paid the instant the deposit reconciles — no polling.

**Create a session** (API plane) for one of your virtual accounts:
```
POST /api/v1/checkout/sessions
{ "accountRef": "inv-100", "ttlSeconds": 3600 }
→ 201 { token, snapshotUrl, streamUrl, expiresAtUtc, snapshot }
```

**Snapshot** (anonymous — safe to hit from the payer's browser):
```
GET /api/v1/checkout/{token}
→ 200 { accountRef, accountNumber, bankName, accountName, paymentState, amountPaidKobo, expectedAmountKobo }
```
`paymentState` ∈ `Unpaid | PartiallyPaid | FullyPaid | Overpaid`. `404` = unknown/expired token.

**Live stream** (Server-Sent Events, anonymous) — the recommended UX:
```js
const es = new EventSource(`https://api.staging.xental.online/api/v1/checkout/${token}/stream`);
es.onmessage = (e) => {
  const s = JSON.parse(e.data); // same shape as the snapshot
  if (s.paymentState === 'FullyPaid') showPaidTick();
};
```
The stream emits the current snapshot immediately, then a new event on every status change.
`EventSource` reconnects automatically; close it when the component unmounts.

---

## 2. Split & Escrow settlement

**Splits** (dashboard plane) — when configured, a fully-paid account's net is fanned out across
beneficiaries instead of a single sweep. Legs are percentage (basis points; 1% = 100 bps) or flat
kobo, in `priority` order; the first leg absorbs any rounding remainder so legs always sum to net.
```
GET /api/v1/settings/splits
PUT /api/v1/settings/splits
{ "splits": [
  { "beneficiaryName": "Merchant", "accountNumber": "0123456789", "bankCode": "011", "basis": "Percentage", "shareBps": 8000, "flatKobo": 0, "priority": 0 },
  { "beneficiaryName": "Platform fee", "accountNumber": "0987654321", "bankCode": "058", "basis": "Percentage", "shareBps": 2000, "flatKobo": 0, "priority": 1 }
]}
```
An **empty** `splits` list clears the plan (back to single sweep).

**Escrow** (API plane) — hold an account's funds until you release them:
```
POST /api/v1/settlements/{accountRef}/hold      { "releaseCondition": "await delivery" }  → 200 hold
POST /api/v1/settlements/{accountRef}/release                                            → 204
```
While held, the settlement worker will not sweep/split that account.

---

## 3. Money Rules

Declarative reactions to reconciled deposits (dashboard plane). Evaluated *after* reconciliation —
they never change the verdict.
```
GET    /api/v1/rules
POST   /api/v1/rules
{ "trigger": "Overpaid", "action": "Hold", "thresholdKobo": 100000, "minRiskScore": null, "priority": 0 }
DELETE /api/v1/rules/{id}
```
- `trigger` ∈ `AnyDeposit | Overpaid | Underpaid | HighRisk | FullyPaid`
  (`thresholdKobo` gates the amount triggers; `minRiskScore` 0–100 gates `HighRisk`).
- `action` ∈ `Hold` (escrow the account) · `Notify` / `ReviewFlag` (emit a `rule.notify` /
  `rule.review_flag` outbound webhook event).

UI idea: a rule builder (trigger dropdown + threshold + action) listing existing rules with delete.

---

## 4. Sandbox simulator (test integrations with zero money)

Build and demo the whole flow without a real bank transfer. **Test-mode API keys only.**
```
POST /api/v1/sandbox/simulate/deposit
{ "accountRef": "inv-100", "amountKobo": 500000, "senderName": "Ada", "reversal": false }
→ 200 { status, reference, reconciliation, paymentState, reason }
```
It runs the **real** reconciliation engine (splits + rules included), so you can wire the Live
Checkout stream to a real state change in a demo. `reversal: true` backs a prior credit out. A live
key returns `403`; an unknown `accountRef` returns `404`.

For AI agents: `GET /.well-known/llms.txt` is a compact capability map; the full contract is the
OpenAPI at `/swagger/v1/swagger.json`.

---

## Errors
All endpoints return JSON problem details with a stable `status` + message: `400` validation,
`401` unauthenticated, `403` wrong plane / live key on sandbox, `404` not found. Money-moving calls
are idempotent on their reference.
