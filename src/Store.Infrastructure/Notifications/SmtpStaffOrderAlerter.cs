using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Store.Application.Abstractions;
using Store.Infrastructure.Identity;

namespace Store.Infrastructure.Notifications;

internal sealed partial class SmtpStaffOrderAlerter(
    IAppDbContext db,
    UserManager<ApplicationUser> users,
    IEmailSender email,
    ILogger<SmtpStaffOrderAlerter> logger) : IStaffOrderAlerter
{
    public async Task AlertNewOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var adminEmails = await GetAdminEmailsAsync(cancellationToken);
        if (adminEmails.Count == 0)
        {
            LogNoAdminRecipients(logger, orderId);
            return;
        }

        var order = await db.Orders.AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order is null)
        {
            LogOrderNotFound(logger, orderId);
            return;
        }

        var itemCount = order.Items.Sum(i => i.Quantity);
        var phoneDigits = DigitsOnly(order.Contact.Phone);
        var subject = $"New order {order.OrderNumber}";
        var body = new StringBuilder()
            .AppendLine($"Order number: {order.OrderNumber}")
            .AppendLine($"Customer: {order.Contact.FullName}")
            .AppendLine($"Phone: {order.Contact.Phone}")
            .AppendLine($"City: {order.ShippingAddress.City}")
            .AppendLine($"Total: {order.Total} {order.CurrencyCode}")
            .AppendLine($"Items: {itemCount}")
            .AppendLine()
            .AppendLine($"Call: tel:{order.Contact.Phone}")
            .AppendLine($"WhatsApp: https://wa.me/{phoneDigits}")
            .ToString();

        await email.SendAsync(new EmailMessage(adminEmails, subject, body), cancellationToken);
    }

    private async Task<List<string>> GetAdminEmailsAsync(CancellationToken cancellationToken)
    {
        var admins = await users.GetUsersInRoleAsync(Roles.Admin);
        return admins
            .Select(u => u.Email)
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string DigitsOnly(string phone) => new string(phone.Where(char.IsDigit).ToArray());

    [LoggerMessage(Level = LogLevel.Warning, Message = "No admin recipients; skipping new-order alert for {OrderId}.")]
    private static partial void LogNoAdminRecipients(ILogger logger, Guid orderId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Order {OrderId} not found when sending new-order alert.")]
    private static partial void LogOrderNotFound(ILogger logger, Guid orderId);
}
