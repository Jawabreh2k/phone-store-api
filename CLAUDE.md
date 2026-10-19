# Phone Store API: working notes

See README.md for layout and run instructions.

## Backend conventions
- HTTP layer is thin controllers: one controller per resource in `Store.Api/Controllers` (admin under `Controllers/Admin`, `[Authorize(Policy = Policies.Admin)]`), inheriting `ApiControllerBase`. An action binds the request (bodies are records in `Contracts/Requests.cs`), injects exactly one handler with `[FromServices]`, and returns `FromResult(...)` / `CreatedFromResult(...)`. No business logic, EF or Infrastructure in controllers (architecture test enforces it).
- Everything is deny-by-default (fallback policy); public controllers/actions need `[AllowAnonymous]`.
- Phone input: validators use `.Phone()` (structural check); handlers convert with `PhoneInput.Normalize(settings, value, "field")` and store `PhoneNumber.Value` (E.164). Never persist raw phone strings.
- Money on orders is always shown with the order's own `CurrencyCode`, never the current store setting.
- A use case is a `sealed internal` handler implementing `ICommandHandler<>` / `IQueryHandler<,>`, with its command/query record and validator in the same file. Handlers are auto-registered and always wrapped in the validation decorator, so never validate by hand in endpoints.
- Business rules live in domain entities and return `Result` / `Error`. Throw only for programming errors. Add new error codes to the `*Errors` static classes; the frontend translates by `code`.
- Aggregates reference each other by id only (no navigation between Product / Order / Cart). Change a child (variant, image, spec) through its aggregate root.
- Entity ids are UUIDv7 set in the constructor and mapped `ValueGeneratedNever()`; don't change that, or EF will UPDATE new children instead of INSERTing them.
- Storefront DTOs return text already localized (`ILanguageProvider`); admin DTOs return `LocalizedTextDto { En, Ar }`.
- Side effects go through the outbox. Never send email / SMS / WhatsApp or call a provider from a handler: enqueue with `IOutbox.Enqueue(new OutboxEntry(type, payload, deduplicationKey, ...))` before the handler's single `SaveChangesAsync`, so the message commits (or rolls back) with the business change. Delivery is at least once: an `IOutboxMessageDeliverer` per type (in Application, registered in `AddApplication`) must be safe to repeat and returns `DeliveryResult.Sent / Temporary / Permanent` rather than throwing. Choose a deduplication key that names the logical message (`order:{id}:status:{status}:{historyEntryId}:customer:{channel}`); a repeat is dropped, never an error. Secrets (tracking / reset tokens) go in `OutboxEntry.Secret` (encrypted, cleared once sent or parked), never in the payload; payloads, recipients and tokens never go in logs or `LastError`.
- Order status notifications come from the `OrderStatusChanged` domain event: `DomainEventsInterceptor` hands every pending event to `IDomainEventDispatcher` while the unit of work saves, so any code path that changes status notifies once per change without extra calls. Placement is explicit (`OrderNotifications.Placed` in `PlaceOrderHandler`), because the raw tracking token only exists there. New side effects of an aggregate change: raise a domain event and translate it in `DomainEventDispatcher`.
- Warnings are errors and analyzers run at `latest-recommended`. Use `[LoggerMessage]` for logging.
- Schema changes: `dotnet ef migrations add <Name> -p src/Store.Infrastructure -s src/Store.Api -o Persistence/Migrations` from the repository root.
- Tests: `dotnet test` from the repository root. Application tests run real handlers on SQLite in-memory (`TestHost`); SQLite-only value conversions live in `AppDbContext.ConfigureConventions`.
