# Xental Backend

ASP.NET Core (.NET 10) backend built on **Clean Architecture**. This is the
foundation onto which feature modules are added one at a time.

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

Swagger UI is served at the app root path, e.g. `https://localhost:7292/swagger`.
Health endpoints: `GET /api/health` (controller) and `GET /health` (probe).

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
