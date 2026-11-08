using System.Text.Json.Serialization;

namespace LoyaltyAwards.Api.Contracts;

public sealed record ErrorResponse(
    string Error,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<FieldError>? Details = null);

public sealed record FieldError(string Field, string Message);

public sealed record WalletResponse(string WalletId, long Balance);
