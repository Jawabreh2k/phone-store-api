using Store.Application.Catalog.Admin;
using Store.Application.Common;
using Store.Application.Orders;
using Store.Domain.Catalog;
using Store.Domain.Common;
using Store.Domain.Orders;

namespace Store.Api.Contracts;

// HTTP request bodies. They exclude anything the server decides (current user, cart owner, route ids),
// so a client can never set those; controllers combine them with that context into application commands.

// ---- Cart & checkout ----
public sealed record AddCartItemRequest(Guid VariantId, int Quantity);

public sealed record UpdateCartItemRequest(int Quantity);

public sealed record PlaceOrderRequest(string FullName, string Phone, string? Email, AddressInput Address, string? Note, bool SaveAddress);

// ---- Auth & account ----
public sealed record RegisterRequest(string Email, string Password, string FullName, string? PhoneNumber, Language PreferredLanguage);

public sealed record LoginRequest(string Email, string Password, bool RememberMe);

public sealed record UpdateProfileRequest(string FullName, string? PhoneNumber, Language PreferredLanguage);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Email, string Token, string NewPassword);

public sealed record SaveAddressRequest(string Label, string RecipientName, string Phone, AddressInput Address, bool IsDefault);

public sealed record CancelOrderRequest(string? Reason);

// ---- Admin ----
public sealed record SaveBrandRequest(string Name, string? Slug, string? LogoUrl, int SortOrder, bool IsActive);

public sealed record SaveCategoryRequest(LocalizedTextDto Name, string? Slug, ProductType ProductType, int SortOrder, bool IsActive);

public sealed record UpdateSettingsRequest(
    LocalizedTextDto StoreName,
    string? SupportPhone,
    string? WhatsAppNumber,
    string? SupportEmail,
    string CurrencyCode,
    string PhoneCountryCode,
    string TimeZoneId,
    decimal FlatShippingFee,
    decimal? FreeShippingThreshold,
    bool AllowGuestCheckout,
    string? SmtpHost,
    int SmtpPort,
    string? SmtpFrom,
    string? SmtpUser,
    string? SmtpPassword);

public sealed record SaveProductRequest(ProductType Type, Guid BrandId, Guid CategoryId, LocalizedTextDto Name, string? Slug, LocalizedTextDto Description, bool IsFeatured);

public sealed record AddVariantRequest(VariantInput Variant, int StockQuantity);

public sealed record UpdateVariantRequest(VariantInput Variant, bool IsActive);

public sealed record AdjustStockRequest(int Delta);

public sealed record ChangeOrderStatusRequest(OrderStatus Status, string? Note);
