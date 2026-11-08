using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LoyaltyAwards.Api.Contracts;

/// <summary>Turns ASP.NET model-state errors into short, client-friendly field errors.</summary>
public static class ValidationErrors
{
    public static List<FieldError> From(ModelStateDictionary modelState)
    {
        var entries = modelState.Where(e => e.Value!.Errors.Count > 0).ToList();

        // JSON-level errors (malformed JSON, non-object body, wrong type) have keys starting with "$".
        // Their raw messages mention .NET types, so replace them with plain explanations.
        var jsonErrors = entries.Where(e => e.Key.StartsWith('$')).ToList();
        if (jsonErrors.Count > 0)
            return jsonErrors.Select(e => e.Key.TrimStart('$', '.') switch
            {
                "" => new FieldError("body", "Request body must be a valid JSON object."),
                "points" => new FieldError("points", "Must be an integer from 1 to 1000."),
                var field => new FieldError(field, "Must be a string."),
            }).ToList();

        // ASP.NET also adds a generic "The body field is required." entry next to the real cause; drop it.
        var meaningful = entries.Count > 1 ? entries.Where(e => e.Key != "body").ToList() : entries;

        return meaningful
            .SelectMany(e => e.Value!.Errors.Select(error =>
                new FieldError(e.Key is "" or "body" ? "body" : e.Key, error.ErrorMessage)))
            .ToList();
    }
}
