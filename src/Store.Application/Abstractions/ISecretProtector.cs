namespace Store.Application.Abstractions;

/// <summary>
/// Encrypts a credential that has to wait in the database until a message carrying it is sent
/// (ASP.NET Data Protection in Infrastructure, with a purpose of its own).
/// </summary>
public interface ISecretProtector
{
    string Protect(string secret);

    /// <summary>The original secret, or null when it can no longer be decrypted (e.g. the key ring was lost).</summary>
    string? Unprotect(string protectedSecret);
}
