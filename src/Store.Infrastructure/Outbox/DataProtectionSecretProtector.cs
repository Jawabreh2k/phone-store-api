using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Store.Application.Abstractions;

namespace Store.Infrastructure.Outbox;

/// <summary>
/// Protects secrets waiting in the outbox with ASP.NET Data Protection under a purpose of their own, so nothing else
/// protected by the app (cookies, reset tokens) can be decrypted with it, nor this with theirs.
/// </summary>
internal sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    public const string Purpose = "Store.Outbox.Secret.v1";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(string secret) => _protector.Protect(secret);

    public string? Unprotect(string protectedSecret)
    {
        try
        {
            return _protector.Unprotect(protectedSecret);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
