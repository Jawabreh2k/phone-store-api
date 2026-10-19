# Phone Store API

Backend for a bilingual (Arabic / English) online store selling mobile phones and accessories: catalog, cart, cash-on-delivery checkout, customer accounts, order tracking, and a staff admin API.

Built with .NET 9, ASP.NET Core, EF Core (SQL Server), and ASP.NET Identity, following Clean Architecture. The Next.js storefront and admin panel live in [phone-store-web](https://github.com/Jawabreh2k/phone-store-web).

## Features

**Storefront**
- Catalog of phones and accessories with brands, categories, specs, photos, and variants (colour / storage), each with its own price and stock
- Accessories list the phones they are compatible with
- Search with server-side filters (brand, category, price range, storage, in stock, compatible phone), sorting, and paging
- Cart for guests and signed-in customers; checkout with cash on delivery
- Guest order tracking through a private link; customers see their order history and can cancel pending orders
- Sign-in with email and password or Google, plus password reset by email
- Text comes back already localized in the request's language (Arabic or English)

**Admin**
- Dashboard: today's sales, the order queue, 14-day sales, top sellers, low stock, and catalog health, computed in the store's own time zone
- Order management: `Pending → Confirmed → Shipped → Delivered`, cancellation, and returns, with a full status history
- Product, brand, and category management with photo uploads
- Store settings: name, logo, contact and WhatsApp numbers, currency, shipping fee, time zone, and SMTP for email alerts

**Notifications**
- Customer and staff emails go through a transactional outbox: a message is saved in the same transaction as the business change, then delivered by a background worker with retries, backoff, and deduplication, so nothing is lost if the process crashes
- Secrets inside messages (tracking and reset links) are encrypted at rest and cleared once sent

## Architecture

```
src/Store.Domain             entities, value objects, business rules (no dependencies)
src/Store.Application        use cases (command / query handlers), DTOs, FluentValidation
src/Store.Infrastructure     EF Core, ASP.NET Identity, outbox worker, email, file storage, seeding
src/Store.Api                thin controllers, auth, localization, rate limiting
tests/                       domain, application (SQLite in-memory), and architecture tests
```

- Dependencies point inwards only (`Api → Infrastructure → Application → Domain`). `Store.ArchitectureTests` (NetArchTest) fails the build if a layer breaks that rule.
- Each use case is one handler with its command or query and validator in the same file. A decorator validates every request before the handler runs.
- Business rules live in the domain entities and return a `Result` instead of throwing. Errors reach the client as RFC 9457 problem details with a stable `code`, which the frontend translates.
- Aggregates (Product, Order, Cart) reference each other by id only. Domain events (for example `OrderStatusChanged`) are dispatched while the unit of work saves, so every path that changes an order notifies the customer exactly once.
- Warnings are treated as errors, and the .NET analyzers run at `latest-recommended`.

## Tech stack

| Area | Tools |
|---|---|
| Runtime | .NET 9, ASP.NET Core (controllers), Scalar API docs |
| Data | EF Core 9, SQL Server, `rowversion` concurrency, UUIDv7 ids |
| Auth | ASP.NET Identity (cookie), Google OAuth, Data Protection |
| Validation | FluentValidation |
| Email | MailKit (SMTP); provider-agnostic messaging for SMS / WhatsApp |
| Tests | xUnit, Shouldly, SQLite in-memory, `Microsoft.AspNetCore.Mvc.Testing`, NetArchTest, `FakeTimeProvider` |

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

The suite runs without a database server. Application tests run the real handlers against SQLite in-memory, including Identity flows such as password reset, and outbox delivery. Domain tests cover the business rules, and architecture tests enforce the layering.

## Design decisions

| Topic | Decision |
|---|---|
| Auth | ASP.NET Identity, HttpOnly `SameSite=Lax` cookie; deny-by-default; `Admin` / `Customer` roles |
| Password reset | Identity's single-use token (default lifespan); `forgot-password` answers 204 whether or not the account exists |
| Google sign-in | Linked to the account with the same Google-verified email, else a new customer account; a password can be added later with "forgot password" |
| Stock | Reserved when an order is placed, released on cancel; `rowversion` turns concurrent checkouts into a 409 |
| Orders | Snapshot names, variant, price, address, and currency; order numbers from a SQL sequence starting at 100001 |
| Guest tracking | Random 128-bit token returned once at checkout; only its SHA-256 is stored |
| Phones | Stored as E.164 (`+962791234567`); a trunk zero after the store country code is dropped |
| Status | `Pending → Confirmed → Shipped → Delivered → Returned`; cancel from Pending (customer) or Confirmed (staff) |
| Money | `decimal(18,3)`, single store currency, snapshotted onto the order |
| Notifications | Transactional outbox, at-least-once delivery, idempotent deliverers keyed by a deduplication key |
| Validation | FluentValidation around every handler; RFC 9457 problem details with a stable `code` |
| Abuse | Fixed-window rate limits on sign-in and other auth endpoints, checkout, and guest order tracking |
