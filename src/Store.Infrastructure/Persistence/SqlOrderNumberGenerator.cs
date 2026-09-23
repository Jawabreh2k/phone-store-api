using Microsoft.EntityFrameworkCore;
using Store.Application.Abstractions;

namespace Store.Infrastructure.Persistence;

/// <summary>
/// Draws order numbers from a SQL Server sequence: short (6 digits to start), unique under
/// concurrency, and not guessable from a GUID. Gaps after rollbacks are expected and harmless.
/// </summary>
internal sealed class SqlOrderNumberGenerator(AppDbContext db) : IOrderNumberGenerator
{
    public async Task<string> NextAsync(CancellationToken cancellationToken)
    {
        // ToListAsync, not SingleAsync: composing would wrap the query in a subquery with TOP,
        // and SQL Server does not allow NEXT VALUE FOR there.
        var values = await db.Database
            .SqlQueryRaw<long>($"SELECT NEXT VALUE FOR [{AppDbContext.OrderNumberSequence}] AS [Value]")
            .ToListAsync(cancellationToken);

        return values[0].ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
