# Phone Store API

.NET 9 API for a mobile phone shop: catalog, cart, guest checkout, customer accounts, and an admin API. Cash on delivery. The storefront lives in the separate `phone-store-web` repository.

The store name, logo, contact numbers, currency, and shipping fee are data (`StoreSettings`), seeded as "PhoneStore" / JOD.

## Layout

```
src/Store.Domain             entities and business rules
src/Store.Application        use cases, DTOs, FluentValidation
src/Store.Infrastructure     EF Core (SQL Server), ASP.NET Identity, file storage, seeding
src/Store.Api                thin controllers, auth, localization, rate limiting
tests/                       domain, application (SQLite in-memory), architecture
```

Dependencies point inwards only (`Api → Infrastructure → Application → Domain`). `Store.ArchitectureTests` enforces it.

## Run locally

Prerequisites: .NET 9 SDK and SQL Server LocalDB (or any SQL Server; change `ConnectionStrings:Store`).

```bash
cd src/Store.Api
dotnet user-secrets set "Seed:AdminEmail" "admin@phonestore.local"
dotnet user-secrets set "Seed:AdminPassword" "<a strong password>"
dotnet run --launch-profile http
```

The API listens on http://localhost:5011. API docs are at `/scalar/v1`. In Development it applies migrations, creates the admin account, and loads a demo catalogue on first start.

## Tests

```bash
dotnet test
```

## Decisions

| Topic | Decision |
|---|---|
| Auth | ASP.NET Identity, HttpOnly `SameSite=Lax` cookie; deny-by-default; `Admin` / `Customer` roles |
| Stock | Reserved when an order is placed, released on cancel; `rowversion` turns concurrent checkouts into a 409 |
| Orders | Snapshot names, variant, price, address, and currency; order numbers from a SQL sequence starting at 100001 |
| Guest tracking | Random 128-bit token returned once at checkout; only its SHA-256 is stored |
| Phones | Stored as E.164 (`+962791234567`); a trunk zero after the store country code is dropped |
| Status | `Pending → Confirmed → Shipped → Delivered`; cancel from Pending (customer) or Confirmed (staff) |
| Money | `decimal(18,3)`, single store currency, snapshotted onto the order |
| Validation | FluentValidation around every handler; RFC 9457 problem details with a stable `code` |
