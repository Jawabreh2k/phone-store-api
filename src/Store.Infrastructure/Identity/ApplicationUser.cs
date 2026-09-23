using Microsoft.AspNetCore.Identity;
using Store.Domain.Common;

namespace Store.Infrastructure.Identity;

/// <summary>Identity account. Business data about customers (addresses, orders) references it by id only.</summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    public ApplicationUser() => Id = Guid.CreateVersion7();

    public string FullName { get; set; } = string.Empty;
    public Language PreferredLanguage { get; set; } = Language.Ar;
    public DateTimeOffset CreatedAt { get; set; }
}
