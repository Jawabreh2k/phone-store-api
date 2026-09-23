using FluentValidation;
using Store.Domain.Common;

namespace Store.Application.Abstractions;

// Every handler is wrapped in one of these, so input is validated before any use case runs,
// whoever calls it (HTTP endpoint, another handler, a background job).

internal static class RequestValidator
{
    public static async Task<ValidationError?> ValidateAsync<T>(IEnumerable<IValidator<T>> validators, T request, CancellationToken cancellationToken)
    {
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(request, cancellationToken);
            failures.AddRange(result.Errors);
        }

        return failures.Count == 0
            ? null
            : new ValidationError(failures
                .GroupBy(f => ToCamelCase(f.PropertyName))
                .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).Distinct().ToArray()));
    }

    // "Address.City" -> "address.city", matching the JSON the frontend sends.
    private static string ToCamelCase(string path) =>
        string.Join('.', path.Split('.').Select(p => p.Length == 0 ? p : char.ToLowerInvariant(p[0]) + p[1..]));
}

internal sealed class ValidatingCommandHandler<TCommand>(ICommandHandler<TCommand> inner, IEnumerable<IValidator<TCommand>> validators)
    : ICommandHandler<TCommand>
    where TCommand : ICommand
{
    public async Task<Result> Handle(TCommand command, CancellationToken cancellationToken)
    {
        var error = await RequestValidator.ValidateAsync(validators, command, cancellationToken);
        return error is null ? await inner.Handle(command, cancellationToken) : Result.Failure(error);
    }
}

internal sealed class ValidatingCommandHandler<TCommand, TResponse>(ICommandHandler<TCommand, TResponse> inner, IEnumerable<IValidator<TCommand>> validators)
    : ICommandHandler<TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    public async Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken)
    {
        var error = await RequestValidator.ValidateAsync(validators, command, cancellationToken);
        return error is null ? await inner.Handle(command, cancellationToken) : Result.Failure<TResponse>(error);
    }
}

internal sealed class ValidatingQueryHandler<TQuery, TResponse>(IQueryHandler<TQuery, TResponse> inner, IEnumerable<IValidator<TQuery>> validators)
    : IQueryHandler<TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    public async Task<Result<TResponse>> Handle(TQuery query, CancellationToken cancellationToken)
    {
        var error = await RequestValidator.ValidateAsync(validators, query, cancellationToken);
        return error is null ? await inner.Handle(query, cancellationToken) : Result.Failure<TResponse>(error);
    }
}
