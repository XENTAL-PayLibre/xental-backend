# Phase 0 & 1 — how to test

Phase 0 = foundation (domain, EF Core, tenant-isolation plumbing, Nomba client,
JWT). Phase 1 = tenant registration, client-credentials → JWT, sub-merchant
provisioning (real Nomba client, faked in tests). Money is in **kobo**;
isolation is enforced at the data layer.

## A. Automated tests (no Postgres needed)
Unit + integration tests run against SQLite in-memory and a fake Nomba client.

```bash
cd xental-backend
dotnet test
```
Expected: **27 unit + 6 integration = 33 passing.**

What they prove:
- **Money** — kobo conversion, banker's rounding, overflow throws (no silent wrap).
- **Secret hashing** — PBKDF2 verify, salted, constant-time, malformed input safe.
- **JWT** — correct claims/expiry; short signing keys rejected.
- **Registration** — client secret is hashed (never stored plaintext), returned once.
- **Auth** — valid creds → token; wrong secret / unknown client → 401.
- **Sub-merchant** — provisions Nomba account → Active; Nomba failure → durable
  Failed record + 502; duplicate reference (same tenant) → 409 without calling Nomba.
- **Tenant isolation** — a tenant cannot see another's data; the same reference is
  reusable across tenants (uniqueness is per-tenant); enforced end-to-end over HTTP.

## B. Run the API locally (real Postgres)

### 1. Start Postgres
```bash
docker run --name xental-pg -d -p 5432:5432 \
  -e POSTGRES_USER=xental_app -e POSTGRES_PASSWORD=xental -e POSTGRES_DB=xental \
  postgres:17-alpine
```
(The default `ConnectionStrings:Default` in appsettings.json matches this.)

### 2. Apply migrations
```bash
dotnet tool install --global dotnet-ef   # first time only
dotnet ef database update \
  --project src/Xental.Infrastructure --startup-project src/Xental.Api
```

### 3. Run (Development uses a dev JWT key from appsettings.Development.json)
```bash
dotnet run --project src/Xental.Api
```
Swagger: the URL printed on startup + `/swagger` (e.g. http://localhost:5xxx/swagger).

> Nomba: with no real `Nomba__ClientId/Secret`, sub-merchant creation will call
> the real Nomba client and fail at the provider (returns **502**, record marked
> Failed). To exercise the happy path against real Nomba, set the `Nomba__*`
> secrets. The full happy path is already proven by the integration tests via the fake.

## C. Manual end-to-end (curl)

```bash
BASE=http://localhost:5000   # use the port dotnet printed

# 1) Register a tenant -> returns clientId + clientSecret (secret shown once)
curl -s -X POST $BASE/api/v1/tenants \
  -H 'Content-Type: application/json' -d '{"name":"Acme Ltd"}'

# 2) Exchange credentials for a JWT
curl -s -X POST $BASE/api/v1/auth/token \
  -H 'Content-Type: application/json' \
  -d '{"clientId":"xnt_...","clientSecret":"sk_..."}'

# 3) Create a sub-merchant (needs the JWT). With real Nomba secrets -> 201 Active;
#    without them -> 502 (record persisted as Failed).
curl -s -X POST $BASE/api/v1/sub-merchants \
  -H "Authorization: Bearer <JWT>" -H 'Content-Type: application/json' \
  -d '{"name":"Green School","reference":"sch-001"}'

# 4) List your sub-merchants
curl -s $BASE/api/v1/sub-merchants -H "Authorization: Bearer <JWT>"
```

### Negative / security checks
```bash
# Wrong secret -> 401
curl -i -X POST $BASE/api/v1/auth/token -H 'Content-Type: application/json' \
  -d '{"clientId":"xnt_...","clientSecret":"sk_wrong"}'

# No token -> 401
curl -i -X POST $BASE/api/v1/sub-merchants -H 'Content-Type: application/json' \
  -d '{"name":"X","reference":"r1"}'

# Duplicate reference (same tenant) -> 409
# (repeat step 3 with the same reference)

# Isolation: register a SECOND tenant, get its token, list -> you will NOT see the
# first tenant's sub-merchants, and you can reuse the same reference.
```

## D. Confirm data + isolation in Postgres
```bash
docker exec -it xental-pg psql -U xental_app -d xental \
  -c "select name, client_id, status from tenants;" \
  -c "select tenant_id, reference, status, nomba_sub_account_id from sub_merchants;"
```
Every `sub_merchants` row carries a `tenant_id`; the unique index is
`(tenant_id, reference)`.

## Cleanup
```bash
docker rm -f xental-pg
```
