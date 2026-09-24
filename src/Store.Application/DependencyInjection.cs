using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Store.Application.Abstractions;
using Store.Application.Carts;
using Store.Application.Notifications;
using Store.Application.Outbox;

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

        services.AddScoped<IOutbox, Outbox.Outbox>();
        services.AddScoped<OrderNotifications>();
        services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
        services.AddScoped<IOutboxMessageDeliverer, CustomerOrderNotificationHandler>();
        services.AddScoped<IOutboxMessageDeliverer, StaffNewOrderAlertHandler>();
        services.AddScoped<IOutboxMessageDeliverer, PasswordResetEmailHandler>();
        services.AddOptions<NotificationOptions>();
        services.AddOptions<StorefrontOptions>();

        // Parameterised validators (e.g. LocalizedTextValidator(maxLength)) are child validators built inline, not DI services.
        services.AddValidatorsFromAssembly(
            assembly,
            includeInternalTypes: true,
            filter: r => r.ValidatorType.GetConstructors().Any(c => c.GetParameters().Length == 0));
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
