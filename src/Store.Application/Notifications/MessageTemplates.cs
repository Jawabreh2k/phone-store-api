using System.Globalization;
using System.Net;
using System.Text;
using Store.Domain.Common;

namespace Store.Application.Notifications;

/// <summary>A message in one language: the email (text + HTML) and a short text for SMS / WhatsApp.</summary>
public sealed record RenderedMessage(string Subject, string Text, string Html, string ShortText);

public sealed record OrderMessageLine(string Name, int Quantity, decimal LineTotal);

/// <summary>
/// What an order message may mention. <paramref name="Link"/> is the tracking link (guests) or the account order page;
/// null when there is none to give (no storefront address configured, or a retried guest message whose token was cleared).
/// </summary>
public sealed record OrderMessageModel(
    string StoreName,
    string CustomerName,
    string OrderNumber,
    IReadOnlyList<OrderMessageLine> Lines,
    decimal Total,
    string CurrencyCode,
    string? Link,
    bool IsGuest,
    bool CancelledByCustomer,
    string? Reason);

public sealed record StaffOrderModel(string OrderNumber, string CustomerName, string Phone, string City, decimal Total, string CurrencyCode, int ItemCount);

public sealed record PasswordResetModel(string StoreName, string FullName, string Link, int ValidForHours);

/// <summary>
/// Every message the store sends, in English and Arabic, as code so the compiler and one test keep both languages
/// complete. Money is always the order's own currency. Every value that can come from a user is HTML-encoded.
/// </summary>
public static class MessageTemplates
{
    private sealed record OrderCopy(
        Func<OrderMessageModel, string> Subject,
        string Headline,
        Func<OrderMessageModel, string> Intro);

    private sealed record Layout(
        string Code,
        bool RightToLeft,
        Func<string, string> Greeting,
        string GreetingNoName,
        string ItemsHeading,
        string Total,
        string TrackLink,
        string AccountLink,
        Func<string, string> NoLink,
        Func<string, string> Reason,
        Func<string, string> Footer);

    private static readonly Dictionary<Language, Layout> Layouts = new()
    {
        [Language.En] = new(
            "en", false,
            name => $"Hi {name},", "Hi,",
            "Your items", "Total",
            "Track your order", "View your order",
            number => $"Quote order number {number} whenever you contact us.",
            reason => $"Reason: {reason}",
            store => $"{store} · You receive this because it is about your order."),
        [Language.Ar] = new(
            "ar", true,
            name => $"مرحباً {name}،", "مرحباً،",
            "منتجاتك", "المجموع",
            "تتبّع طلبك", "عرض طلبك",
            number => $"اذكر رقم الطلب {number} عند التواصل معنا.",
            reason => $"السبب: {reason}",
            store => $"{store} · تصلك هذه الرسالة لأنها تخصّ طلبك."),
    };

    private static readonly Dictionary<(NotificationTemplate, Language), OrderCopy> OrderCopies = new()
    {
        [(NotificationTemplate.OrderPlaced, Language.En)] = new(
            m => $"We received your order {m.OrderNumber}",
            "Thanks for your order",
            m => $"We've received order {m.OrderNumber} and will call you soon to confirm it. You pay in cash on delivery."),
        [(NotificationTemplate.OrderPlaced, Language.Ar)] = new(
            m => $"استلمنا طلبك رقم {m.OrderNumber}",
            "شكراً لطلبك",
            m => $"استلمنا الطلب رقم {m.OrderNumber} وسنتصل بك قريباً لتأكيده. الدفع نقداً عند الاستلام."),

        [(NotificationTemplate.OrderConfirmed, Language.En)] = new(
            m => $"Your order {m.OrderNumber} is confirmed",
            "Your order is confirmed",
            m => $"Good news: we've confirmed order {m.OrderNumber} and are getting it ready."),
        [(NotificationTemplate.OrderConfirmed, Language.Ar)] = new(
            m => $"تم تأكيد طلبك رقم {m.OrderNumber}",
            "تم تأكيد طلبك",
            m => $"أخبار سارّة: أكّدنا الطلب رقم {m.OrderNumber} ونعمل على تجهيزه."),

        [(NotificationTemplate.OrderShipped, Language.En)] = new(
            m => $"Your order {m.OrderNumber} is out for delivery",
            "Your order is on its way",
            m => $"Order {m.OrderNumber} is out for delivery. Please keep your phone close: the courier will call before arriving. Have {Money(m.Total, m.CurrencyCode, Language.En)} ready in cash."),
        [(NotificationTemplate.OrderShipped, Language.Ar)] = new(
            m => $"طلبك رقم {m.OrderNumber} في الطريق إليك",
            "طلبك في الطريق",
            m => $"الطلب رقم {m.OrderNumber} خرج للتوصيل. يُرجى إبقاء هاتفك قريباً، فسيتصل بك المندوب قبل الوصول. جهّز مبلغ {Money(m.Total, m.CurrencyCode, Language.Ar)} نقداً."),

        [(NotificationTemplate.OrderDelivered, Language.En)] = new(
            m => $"Your order {m.OrderNumber} was delivered",
            "Delivered",
            m => $"Order {m.OrderNumber} has been delivered. Thank you for shopping with {m.StoreName}!"),
        [(NotificationTemplate.OrderDelivered, Language.Ar)] = new(
            m => $"تم توصيل طلبك رقم {m.OrderNumber}",
            "تم التوصيل",
            m => $"تم توصيل الطلب رقم {m.OrderNumber}. شكراً لتسوّقك من {m.StoreName}!"),

        [(NotificationTemplate.OrderCancelled, Language.En)] = new(
            m => $"Your order {m.OrderNumber} was cancelled",
            "Order cancelled",
            m => m.CancelledByCustomer
                ? $"As you asked, we've cancelled order {m.OrderNumber}."
                : $"We're sorry: we had to cancel order {m.OrderNumber}."),
        [(NotificationTemplate.OrderCancelled, Language.Ar)] = new(
            m => $"تم إلغاء طلبك رقم {m.OrderNumber}",
            "تم إلغاء الطلب",
            m => m.CancelledByCustomer
                ? $"ألغينا الطلب رقم {m.OrderNumber} بناءً على طلبك."
                : $"نعتذر، اضطررنا إلى إلغاء الطلب رقم {m.OrderNumber}."),

        [(NotificationTemplate.OrderReturned, Language.En)] = new(
            m => $"Your order {m.OrderNumber} was returned",
            "Return recorded",
            m => $"We've recorded the return of order {m.OrderNumber}. Contact us if you have any questions."),
        [(NotificationTemplate.OrderReturned, Language.Ar)] = new(
            m => $"تم تسجيل إرجاع طلبك رقم {m.OrderNumber}",
            "تم تسجيل الإرجاع",
            m => $"سجّلنا إرجاع الطلب رقم {m.OrderNumber}. تواصل معنا إن كان لديك أي سؤال."),
    };

    private static readonly Dictionary<Language, (Func<StaffOrderModel, string> Subject, string[] Labels)> StaffCopies = new()
    {
        [Language.En] = (m => $"New order {m.OrderNumber}", ["Order number", "Customer", "Phone", "City", "Total", "Items", "Call", "WhatsApp"]),
        [Language.Ar] = (m => $"طلب جديد رقم {m.OrderNumber}", ["رقم الطلب", "العميل", "الهاتف", "المدينة", "المجموع", "عدد القطع", "اتصال", "واتساب"]),
    };

    private static readonly Dictionary<Language, (Func<PasswordResetModel, string> Subject, Func<PasswordResetModel, string> Body)> PasswordResetCopies = new()
    {
        [Language.En] = (
            m => $"Reset your {m.StoreName} password",
            m => $"""
                {(m.FullName.Length > 0 ? $"Hi {m.FullName}," : "Hi,")}

                We received a request to reset the password for your {m.StoreName} account. Open this link to choose a new one:

                {m.Link}

                The link works once and expires in {m.ValidForHours.ToString(CultureInfo.InvariantCulture)} hours. If you didn't ask for this, you can ignore this email; your password stays the same.
                """),
        [Language.Ar] = (
            m => $"إعادة تعيين كلمة المرور في {m.StoreName}",
            m => $"""
                {(m.FullName.Length > 0 ? $"مرحباً {m.FullName}،" : "مرحباً،")}

                تلقّينا طلباً لإعادة تعيين كلمة المرور لحسابك في {m.StoreName}. افتح هذا الرابط لاختيار كلمة مرور جديدة:

                {m.Link}

                يعمل الرابط مرة واحدة فقط، وتنتهي صلاحيته خلال {m.ValidForHours.ToString(CultureInfo.InvariantCulture)} ساعة. إذا لم تطلب ذلك، فتجاهل هذه الرسالة وستبقى كلمة مرورك كما هي.
                """),
    };

    /// <summary>The customer order templates, one per status a customer hears about.</summary>
    public static IReadOnlyList<NotificationTemplate> OrderTemplates { get; } =
    [
        NotificationTemplate.OrderPlaced,
        NotificationTemplate.OrderConfirmed,
        NotificationTemplate.OrderShipped,
        NotificationTemplate.OrderDelivered,
        NotificationTemplate.OrderCancelled,
        NotificationTemplate.OrderReturned,
    ];

    /// <summary>Whether <paramref name="template"/> has copy in <paramref name="language"/>.</summary>
    public static bool IsDefined(NotificationTemplate template, Language language) => template switch
    {
        NotificationTemplate.StaffNewOrder => StaffCopies.ContainsKey(language) && Layouts.ContainsKey(language),
        NotificationTemplate.PasswordReset => PasswordResetCopies.ContainsKey(language) && Layouts.ContainsKey(language),
        _ => OrderCopies.ContainsKey((template, language)) && Layouts.ContainsKey(language),
    };

    /// <summary>
    /// "JOD 1,049.000" in English and "1,049.000 JOD" in Arabic: always the order's own ISO code, with the minor
    /// units that currency uses, and Western digits in both languages (as the storefront shows them).
    /// </summary>
    public static string Money(decimal amount, string currencyCode, Language language)
    {
        var digits = currencyCode switch
        {
            "BHD" or "IQD" or "JOD" or "KWD" or "LYD" or "OMR" or "TND" => 3,
            "JPY" or "KRW" or "VND" => 0,
            _ => 2,
        };
        var number = amount.ToString("N" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return language == Language.Ar ? $"{number} {currencyCode}" : $"{currencyCode} {number}";
    }

    public static RenderedMessage RenderOrder(NotificationTemplate template, Language language, OrderMessageModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!OrderCopies.TryGetValue((template, language), out var copy))
        {
            throw new ArgumentOutOfRangeException(nameof(template), template, $"No {language} copy for this order template.");
        }

        var layout = Layouts[language];
        var subject = copy.Subject(model);
        var intro = copy.Intro(model);
        var greeting = string.IsNullOrWhiteSpace(model.CustomerName) ? layout.GreetingNoName : layout.Greeting(model.CustomerName.Trim());
        var reason = template == NotificationTemplate.OrderCancelled && !string.IsNullOrWhiteSpace(model.Reason) ? layout.Reason(model.Reason.Trim()) : null;
        var linkLabel = model.IsGuest ? layout.TrackLink : layout.AccountLink;
        var noLink = model.Link is null ? layout.NoLink(model.OrderNumber) : null;
        var total = Money(model.Total, model.CurrencyCode, language);
        var footer = layout.Footer(model.StoreName);

        var text = new StringBuilder()
            .AppendLine(greeting).AppendLine()
            .AppendLine(intro);
        if (reason is not null)
        {
            text.AppendLine(reason);
        }

        text.AppendLine().AppendLine(layout.ItemsHeading);
        foreach (var line in model.Lines)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {line.Quantity} × {line.Name}: {Money(line.LineTotal, model.CurrencyCode, language)}");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"{layout.Total}: {total}").AppendLine();
        text.AppendLine(model.Link is null ? noLink : $"{linkLabel}: {model.Link}").AppendLine();
        text.Append(footer);

        var body = new StringBuilder()
            .Append(Paragraph(greeting))
            .Append(Paragraph(intro));
        if (reason is not null)
        {
            body.Append(Paragraph(reason));
        }

        body.Append(CultureInfo.InvariantCulture, $"""<h2 style="font-size:15px;margin:24px 0 8px;">{Encode(layout.ItemsHeading)}</h2>""")
            .Append("""<table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="border-collapse:collapse;font-size:14px;">""");
        foreach (var line in model.Lines)
        {
            body.Append(CultureInfo.InvariantCulture, $"""<tr><td style="padding:6px 0;border-bottom:1px solid #eee;">{line.Quantity} × {Encode(line.Name)}</td><td style="padding:6px 0;border-bottom:1px solid #eee;text-align:{End(layout)};white-space:nowrap;">{MoneyHtml(line.LineTotal, model.CurrencyCode, language)}</td></tr>""");
        }

        body.Append(CultureInfo.InvariantCulture, $"""<tr><td style="padding:10px 0;font-weight:600;">{Encode(layout.Total)}</td><td style="padding:10px 0;font-weight:600;text-align:{End(layout)};white-space:nowrap;">{MoneyHtml(model.Total, model.CurrencyCode, language)}</td></tr></table>""");
        body.Append(model.Link is null
            ? Paragraph(noLink!)
            : $"""<p style="margin:24px 0;"><a href="{Encode(model.Link)}" style="display:inline-block;background:#1d1d1f;color:#ffffff;text-decoration:none;padding:12px 20px;border-radius:999px;font-weight:600;">{Encode(linkLabel)}</a></p>""");

        var shortText = model.Link is null
            ? $"{model.StoreName}: {subject}."
            : $"{model.StoreName}: {subject}. {linkLabel}: {model.Link}";

        return new RenderedMessage(subject, text.ToString(), Document(layout, subject, model.StoreName, copy.Headline, body.ToString(), footer), shortText);
    }

    public static RenderedMessage RenderStaffNewOrder(Language language, StaffOrderModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var (subjectOf, labels) = StaffCopies[language];
        var layout = Layouts[language];
        var subject = subjectOf(model);
        var digits = new string(model.Phone.Where(char.IsAsciiDigit).ToArray());
        var itemCount = model.ItemCount.ToString(CultureInfo.InvariantCulture);
        var total = Money(model.Total, model.CurrencyCode, language);

        var text = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"{labels[0]}: {model.OrderNumber}")
            .AppendLine(CultureInfo.InvariantCulture, $"{labels[1]}: {model.CustomerName}")
            .AppendLine(CultureInfo.InvariantCulture, $"{labels[2]}: {model.Phone}")
            .AppendLine(CultureInfo.InvariantCulture, $"{labels[3]}: {model.City}")
            .AppendLine(CultureInfo.InvariantCulture, $"{labels[4]}: {total}")
            .AppendLine(CultureInfo.InvariantCulture, $"{labels[5]}: {itemCount}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"{labels[6]}: tel:{model.Phone}")
            .Append(CultureInfo.InvariantCulture, $"{labels[7]}: https://wa.me/{digits}")
            .ToString();

        var rows = new[]
        {
            (labels[0], Encode(model.OrderNumber)),
            (labels[1], Encode(model.CustomerName)),
            (labels[2], $"""<a href="tel:{Encode(model.Phone)}" dir="ltr">{Encode(model.Phone)}</a>"""),
            (labels[3], Encode(model.City)),
            (labels[4], MoneyHtml(model.Total, model.CurrencyCode, language)),
            (labels[5], itemCount),
            (labels[7], $"""<a href="https://wa.me/{digits}">wa.me/{digits}</a>"""),
        };
        var body = new StringBuilder("""<table role="presentation" cellpadding="0" cellspacing="0" style="font-size:14px;">""");
        foreach (var (label, value) in rows)
        {
            body.Append(CultureInfo.InvariantCulture, $"""<tr><td style="padding:4px 0;color:#6e6e73;padding-{(layout.RightToLeft ? "left" : "right")}:16px;">{Encode(label)}</td><td style="padding:4px 0;">{value}</td></tr>""");
        }

        body.Append("</table>");
        return new RenderedMessage(subject, text, Document(layout, subject, storeName: null, subject, body.ToString(), footer: null), subject);
    }

    public static RenderedMessage RenderPasswordReset(Language language, PasswordResetModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var (subjectOf, bodyOf) = PasswordResetCopies[language];
        var layout = Layouts[language];
        var subject = subjectOf(model);
        var text = bodyOf(model);

        var html = new StringBuilder();
        foreach (var paragraph in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            html.Append(paragraph == model.Link
                ? $"""<p style="margin:24px 0;"><a href="{Encode(model.Link)}" style="display:inline-block;background:#1d1d1f;color:#ffffff;text-decoration:none;padding:12px 20px;border-radius:999px;font-weight:600;">{Encode(subject)}</a></p>"""
                : Paragraph(paragraph));
        }

        return new RenderedMessage(subject, text, Document(layout, subject, model.StoreName, subject, html.ToString(), footer: null), subject);
    }

    private static string Document(Layout layout, string subject, string? storeName, string headline, string body, string? footer)
    {
        var dir = layout.RightToLeft ? "rtl" : "ltr";
        var align = layout.RightToLeft ? "right" : "left";
        var html = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"""<!DOCTYPE html><html lang="{layout.Code}" dir="{dir}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{Encode(subject)}</title></head>""")
            .Append("""<body style="margin:0;padding:24px 12px;background:#f5f5f7;font-family:'Segoe UI',Tahoma,Arial,sans-serif;color:#1d1d1f;">""")
            .Append(CultureInfo.InvariantCulture, $"""<div dir="{dir}" style="max-width:560px;margin:0 auto;background:#ffffff;border-radius:16px;padding:28px;text-align:{align};line-height:1.6;">""");
        if (storeName is not null)
        {
            html.Append(CultureInfo.InvariantCulture, $"""<p style="font-size:13px;color:#6e6e73;margin:0 0 16px;">{Encode(storeName)}</p>""");
        }

        html.Append(CultureInfo.InvariantCulture, $"""<h1 style="font-size:20px;margin:0 0 16px;">{Encode(headline)}</h1>""").Append(body);
        if (footer is not null)
        {
            html.Append(CultureInfo.InvariantCulture, $"""<p style="font-size:12px;color:#6e6e73;margin:24px 0 0;">{Encode(footer)}</p>""");
        }

        return html.Append("</div></body></html>").ToString();
    }

    private static string Paragraph(string text) => $"""<p style="margin:0 0 12px;">{Encode(text)}</p>""";

    // Amounts are left-to-right runs even inside Arabic text, isolated so the currency code stays beside its number.
    private static string MoneyHtml(decimal amount, string currencyCode, Language language) =>
        $"""<span dir="ltr" style="unicode-bidi:isolate;">{Encode(Money(amount, currencyCode, language))}</span>""";

    private static string End(Layout layout) => layout.RightToLeft ? "left" : "right";

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
