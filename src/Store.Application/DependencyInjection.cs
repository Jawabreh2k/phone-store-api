using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Store.Application.Abstractions;
using Store.Application.Carts;

namespace Store.Application;

public static class DependencyInjection
{
    private static readonly Dictionary<Type, Type> Decorators = new()
    {
        [typeof(ICommandHandler<>)] = typeof(ValidatingCommandHandler<>),
        [typeof(ICommandHandler<,>)] = typeof(ValidatingCommandHandler<,>),
        [typeof(IQueryHandler<,>)] = typeof(ValidatingQueryHandler<,>),
    };

    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        // Each handler is registered as its concrete type, and its interface resolves to the
        // validating decorator wrapping it. Callers always get validation.
        var handlerTypes = assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false });
        foreach (var handlerType in handlerTypes)
        {
            foreach (var contract in handlerType.GetInterfaces().Where(i => i.IsGenericType && Decorators.ContainsKey(i.GetGenericTypeDefinition())))
            {
                services.AddScoped(handlerType);
                var decoratorType = Decorators[contract.GetGenericTypeDefinition()].MakeGenericType(contract.GetGenericArguments());
                services.AddScoped(contract, sp => ActivatorUtilities.CreateInstance(sp, decoratorType, sp.GetRequiredService(handlerType)));
            }
        }

        services.AddScoped<CartReader>();
        // Parameterised validators (e.g. LocalizedTextValidator(maxLength)) are child validators built inline, not DI services.
        services.AddValidatorsFromAssembly(
            assembly,
            includeInternalTypes: true,
            filter: r => r.ValidatorType.GetConstructors().Any(c => c.GetParameters().Length == 0));
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
