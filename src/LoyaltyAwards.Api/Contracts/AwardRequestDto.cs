using System.ComponentModel.DataAnnotations;

namespace LoyaltyAwards.Api.Contracts;

/// <summary>
/// POST /awards body. Properties are nullable so a missing field is reported as "required"
/// instead of silently becoming null/0. Types are enforced by System.Text.Json with
/// NumberHandling = Strict (see Program.cs), so "25" or 25.5 for points is rejected.
/// </summary>
public sealed class AwardRequestDto
{
    private const string IdPattern = "^[A-Za-z0-9_-]{1,64}$";
    private const string IdMessage = "Must be 1-64 ASCII letters, digits, underscores or hyphens.";

    [Required(ErrorMessage = "Required."), RegularExpression(IdPattern, ErrorMessage = IdMessage)]
    public string? SourceId { get; init; }

    [Required(ErrorMessage = "Required."), RegularExpression(IdPattern, ErrorMessage = IdMessage)]
    public string? ExternalTransactionId { get; init; }

    [Required(ErrorMessage = "Required."), RegularExpression(IdPattern, ErrorMessage = IdMessage)]
    public string? WalletId { get; init; }

    [Required(ErrorMessage = "Required."), Range(1, 1000, ErrorMessage = "Must be an integer from 1 to 1000.")]
    public int? Points { get; init; }

    /// <summary>Only called after model validation has passed, so every value is present.</summary>
    public AwardRequest ToAwardRequest() => new(SourceId!, ExternalTransactionId!, WalletId!, Points!.Value);
}
