namespace Store.Api.Common;

/// <summary>Names of authorization and rate-limit policies registered in Program.cs.</summary>
internal static class Policies
{
    public const string Admin = "admin";

    /// <summary>Login, register, password change: 10/min per client IP.</summary>
    public const string AuthRateLimit = "auth";

    /// <summary>Placing orders: 20/min per client IP.</summary>
    public const string CheckoutRateLimit = "checkout";

    /// <summary>Guest order tracking: separate bucket so tracking cannot eat into checkout.</summary>
    public const string TrackingRateLimit = "tracking";
}
