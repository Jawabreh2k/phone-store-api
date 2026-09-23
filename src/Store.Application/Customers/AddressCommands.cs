using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Store.Application.Abstractions;
using Store.Application.Common;
using Store.Application.Orders;
using Store.Domain.Common;
using Store.Domain.Customers;

namespace Store.Application.Customers;

public sealed record AddressDto(Guid Id, string Label, string RecipientName, string Phone, AddressInput Address, bool IsDefault);

public sealed record GetMyAddressesQuery(Guid CustomerId) : IQuery<IReadOnlyList<AddressDto>>;

internal sealed class GetMyAddressesHandler(IAppDbContext db) : IQueryHandler<GetMyAddressesQuery, IReadOnlyList<AddressDto>>
{
    public async Task<Result<IReadOnlyList<AddressDto>>> Handle(GetMyAddressesQuery query, CancellationToken cancellationToken)
    {
        var addresses = await db.CustomerAddresses.AsNoTracking()
            .Where(a => a.CustomerId == query.CustomerId)
            .OrderByDescending(a => a.IsDefault).ThenBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken);

        return addresses.Select(ToDto).ToList();
    }

    internal static AddressDto ToDto(CustomerAddress a) => new(
        a.Id, a.Label, a.RecipientName, a.Phone,
        new AddressInput(a.Address.City, a.Address.Area, a.Address.Street, a.Address.Building, a.Address.Details),
        a.IsDefault);
}

/// <summary>Creates an address when <see cref="AddressId"/> is null, otherwise updates it.</summary>
public sealed record SaveAddressCommand(Guid CustomerId, Guid? AddressId, string Label, string RecipientName, string Phone, AddressInput Address, bool IsDefault)
    : ICommand<AddressDto>;

public sealed class SaveAddressValidator : AbstractValidator<SaveAddressCommand>
{
    public SaveAddressValidator()
    {
        RuleFor(x => x.Label).NotEmpty().MaximumLength(40);
        RuleFor(x => x.RecipientName).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Phone).NotEmpty().Phone();
        RuleFor(x => x.Address).NotNull().SetValidator(new AddressInputValidator());
    }
}

internal sealed class SaveAddressHandler(IAppDbContext db) : ICommandHandler<SaveAddressCommand, AddressDto>
{
    public async Task<Result<AddressDto>> Handle(SaveAddressCommand command, CancellationToken cancellationToken)
    {
        var settings = await db.StoreSettings.AsNoTracking().FirstAsync(cancellationToken);
        var phone = PhoneInput.Normalize(settings, command.Phone, "phone");
        if (phone.IsFailure)
        {
            return phone.Error;
        }

        var addresses = await db.CustomerAddresses.Where(a => a.CustomerId == command.CustomerId).ToListAsync(cancellationToken);

        CustomerAddress? address;
        if (command.AddressId is { } id)
        {
            address = addresses.Find(a => a.Id == id);
            if (address is null)
            {
                return CustomerErrors.AddressNotFound;
            }

            address.Update(command.Label, command.RecipientName, phone.Value, command.Address.ToDomain());
        }
        else
        {
            if (addresses.Count >= CustomerAddress.MaxPerCustomer)
            {
                return CustomerErrors.TooManyAddresses;
            }

            address = new CustomerAddress(command.CustomerId, command.Label, command.RecipientName, phone.Value, command.Address.ToDomain());
            db.CustomerAddresses.Add(address);
            addresses.Add(address);
        }

        // Exactly one default: the requested one, or the first address a customer saves.
        if (command.IsDefault || addresses.Count == 1)
        {
            addresses.ForEach(a => a.ClearDefault());
            address.MarkDefault();
        }

        await db.SaveChangesAsync(cancellationToken);
        return GetMyAddressesHandler.ToDto(address);
    }
}

public sealed record DeleteAddressCommand(Guid CustomerId, Guid AddressId) : ICommand;

internal sealed class DeleteAddressHandler(IAppDbContext db) : ICommandHandler<DeleteAddressCommand>
{
    public async Task<Result> Handle(DeleteAddressCommand command, CancellationToken cancellationToken)
    {
        var addresses = await db.CustomerAddresses.Where(a => a.CustomerId == command.CustomerId).ToListAsync(cancellationToken);
        var address = addresses.Find(a => a.Id == command.AddressId);
        if (address is null)
        {
            return CustomerErrors.AddressNotFound;
        }

        db.CustomerAddresses.Remove(address);
        if (address.IsDefault)
        {
            addresses.Where(a => a.Id != address.Id).OrderBy(a => a.CreatedAt).FirstOrDefault()?.MarkDefault();
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
