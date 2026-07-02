# Xental Backend

ASP.NET Core (.NET 10) backend built on **Clean Architecture**. Xental provides
**reusable dedicated virtual accounts + automatic reconciliation** on top of Nomba:
merchants provision NUBANs, Xental reconciles inflows against expected amounts,
scores fraud risk, fans out signed webhooks, and auto-settles collected funds to
the merchant's bank.

## What's shipped

Multi-tenant by design — row-level `TenantId` isolation enforced by EF global query
filters **and** at write time. Money is integer **kobo** end to end.

| Area | Capability |
|------|-----------|
| **Identity** | Developer email/password + Google/GitHub OAuth, email verification & password reset (Resend magic links), verify-before-login |
| **Sessions** | HttpOnly+Secure cookie sessions, short-lived access + rotating single-use refresh tokens (SHA-256 hashed) |
| **API auth** | Client-credentials → scoped API JWT; separate dashboard vs API planes; test/live API keys |
| **Virtual accounts** | Provider-backed NUBANs with optional expected-amount, per-tenant `accountRef` |
| **Reconciliation** | Signed Nomba inflow webhooks → Reconciled / Underpaid / Overpaid / PendingReview / Reversed (per the Rule Book), idempotent on provider reference |
| **Fraud/risk** | Explainable 0–100 score (name mismatch, overpayment, velocity, payer-name reuse / mule pattern); ≥70 → review queue |
| **Outbound webhooks** | Signed (HMAC-SHA256), retried with backoff, dead-lettered & replayable; AES-GCM-encrypted secrets; SSRF-guarded URLs |
| **Transactions & payouts** | Filtered statements; idempotent bank transfers (keyed on `merchantTxRef`) |
| **Settlement** | Per-tenant settlement account + auto-settle worker that sweeps fully-paid accounts (net of fees) to the merchant's bank |
| **Insights** | Collection rate, outstanding deficit, reconciliation & risk breakdown |
| **Ops** | `/health` liveness + `/ready` DB readiness, security-headers middleware, throttled 5xx email alerts, OpenTelemetry, Serilog |

Full API + concept reference: [documentation.md](documentation.md). Interactive docs
at `/swagger` (includes a quickstart).

## Solution structure

```
Xental.slnx
src/
  Xental.Domain/          # Enterprise rules: entities, value objects. No dependencies.
  Xental.Application/      # Use cases, abstractions (ports), DTOs. Depends on Domain.
  Xental.Infrastructure/   # Adapters: persistence, external services. Depends on Application.
  Xental.Api/              # Presentation: controllers, Swagger, DI composition root.
```

Dependency rule (inward only): `Api → Infrastructure → Application → Domain`.
The Domain layer depends on nothing; the Api layer composes everything.

Each layer exposes a DI entry point used by the API's composition root:
- `Xental.Application` → `AddApplication()`
- `Xental.Infrastructure` → `AddInfrastructure(IConfiguration)`

New modules register their handlers/services inside these methods.

## Running locally

```bash
dotnet run --project src/Xental.Api
```

Swagger UI is served at `/swagger`, e.g. `https://localhost:7292/swagger`.
Health endpoints: `GET /health` (liveness) and `GET /ready` (DB readiness).

## Running with Docker

```bash
docker build -t xental-api .
docker run -p 8080:8080 xental-api
# or
docker compose up --build
```

The container listens on port **8080** (Swagger at `http://localhost:8080/swagger`).

## Build & test

```bash
dotnet build Xental.slnx -c Release
```
