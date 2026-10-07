using Microsoft.Extensions.Options;

namespace SemanticRouter.AspNetCore;

/// <summary>Surfaces <see cref="SemanticRouterOptions.GetValidationErrors"/> through the options pipeline (fails at start-up).</summary>
internal sealed class SemanticRouterOptionsValidator : IValidateOptions<SemanticRouterOptions>
{
    public ValidateOptionsResult Validate(string? name, SemanticRouterOptions options)
    {
        IReadOnlyList<string> errors = options.GetValidationErrors();
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
