# Phone Store API: working notes

See README.md for layout and run instructions.

## Backend conventions
- HTTP layer is thin controllers (Emdad-style shape): one controller per resource in `Store.Api/Controllers` (admin under `Controllers/Admin`, `[Authorize(Policy = Policies.Admin)]`), inheriting `ApiControllerBase`. An action binds the request (bodies are records in `Contracts/Requests.cs`), injects exactly one handler with `[FromServices]`, and returns `FromResult(...)` / `CreatedFromResult(...)`. No business logic, EF or Infrastructure in controllers (architecture test enforces it).
- Everything is deny-by-default (fallback policy); public controllers/actions need `[AllowAnonymous]`.
- Phone input: validators use `.Phone()` (structural check); handlers convert with `PhoneInput.Normalize(settings, value, "field")` and store `PhoneNumber.Value` (E.164). Never persist raw phone strings.
- Money on orders is always shown with the order's own `CurrencyCode`, never the current store setting.
- A use case is a `sealed internal` handler implementing `ICommandHandler<>` / `IQueryHandler<,>`, with its command/query record and validator in the same file. Handlers are auto-registered and always wrapped in the validation decorator, so never validate by hand in endpoints.
- Business rules live in domain entities and return `Result` / `Error`. Throw only for programming errors. Add new error codes to the `*Errors` static classes; the frontend translates by `code`.
- Aggregates reference each other by id only (no navigation between Product / Order / Cart). Change a child (variant, image, spec) through its aggregate root.
- Entity ids are UUIDv7 set in the constructor and mapped `ValueGeneratedNever()`; don't change that, or EF will UPDATE new children instead of INSERTing them.
- Storefront DTOs return text already localized (`ILanguageProvider`); admin DTOs return `LocalizedTextDto { En, Ar }`.
- Warnings are errors and analyzers run at `latest-recommended`. Use `[LoggerMessage]` for logging.
- Schema changes: `dotnet ef migrations add <Name> -p src/Store.Infrastructure -s src/Store.Api -o Persistence/Migrations` from the repository root.
- Tests: `dotnet test` from the repository root. Application tests run real handlers on SQLite in-memory (`TestHost`); SQLite-only value conversions live in `AppDbContext.ConfigureConventions`.
