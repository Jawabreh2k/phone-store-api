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

### Email and sign-in options

- **Password reset emails** go through the SMTP server set in Admin > Settings > Email alerts (no SMTP host: the API logs and skips the mail). Links point at `Storefront:BaseUrl` (`http://localhost:3000` in Development; set it for every other environment).
- **Google sign-in** is on only when a client id and secret are configured. They are secrets: user-secrets in development, environment variables (`Authentication__Google__ClientId`, `Authentication__Google__ClientSecret`) or a secret store in production, never appsettings.

  ```bash
  dotnet user-secrets set "Authentication:Google:ClientId" "<client id>"
  dotnet user-secrets set "Authentication:Google:ClientSecret" "<client secret>"
  ```

  In the Google Cloud console, add the storefront origin's callback as an authorized redirect URI: `http://localhost:3000/api/auth/google/callback` in development, `https://<store domain>/api/auth/google/callback` in production. The browser reaches the API through the storefront's `/api` rewrite, so the callback lives on the storefront origin and every cookie in the flow stays first-party `SameSite=Lax`. The API builds that URI from `X-Forwarded-Host` (set by Next.js) and `X-Forwarded-Proto` (set by the TLS proxy in front), trusted from loopback proxies only.

## Tests

```bash
dotnet test
```

## Decisions

| Topic | Decision |
|---|---|
| Auth | ASP.NET Identity, HttpOnly `SameSite=Lax` cookie; deny-by-default; `Admin` / `Customer` roles |
| Password reset | Identity's single-use token (default lifespan); `forgot-password` answers 204 whether or not the account exists |
| Google sign-in | Linked to the account with the same Google-verified email, else a new customer account; a password can be added later with "forgot password" |
| Stock | Reserved when an order is placed, released on cancel; `rowversion` turns concurrent checkouts into a 409 |
| Orders | Snapshot names, variant, price, address, and currency; order numbers from a SQL sequence starting at 100001 |
| Guest tracking | Random 128-bit token returned once at checkout; only its SHA-256 is stored |
| Phones | Stored as E.164 (`+962791234567`); a trunk zero after the store country code is dropped |
| Status | `Pending → Confirmed → Shipped → Delivered`; cancel from Pending (customer) or Confirmed (staff) |
| Money | `decimal(18,3)`, single store currency, snapshotted onto the order |
| Validation | FluentValidation around every handler; RFC 9457 problem details with a stable `code` |
