using LoyaltyAwards.Api.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace LoyaltyAwards.Api.Controllers;

[ApiController]
[Route("awards")]
[Produces("application/json")]
public sealed class AwardsController(LoyaltyStore store) : ControllerBase
{
    /// <summary>
    /// Awards points to a wallet. Safe to retry: the pair (sourceId, externalTransactionId) identifies the award.
    /// [ApiController] has already returned 400 before this runs if the body is malformed or invalid.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<Award>(StatusCodes.Status201Created)]
    [ProducesResponseType<Award>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status409Conflict)]
    public IActionResult Create([FromBody] AwardRequestDto body)
    {
        var request = body.ToAwardRequest();
        var result = store.Award(request);

        return result.Outcome switch
        {
            AwardOutcome.Created => StatusCode(StatusCodes.Status201Created, result.Award),
            AwardOutcome.Replayed => Ok(result.Award),
            AwardOutcome.Conflict => Conflict(new ErrorResponse(
                "award_conflict",
                "An award with this sourceId and externalTransactionId already exists with a different walletId or points. " +
                $"Existing award: {result.Award!.AwardId}.")),
            AwardOutcome.WalletNotFound => NotFound(new ErrorResponse(
                "wallet_not_found",
                $"Wallet '{request.WalletId}' does not exist. No award was recorded.")),
            _ => throw new InvalidOperationException($"Unhandled outcome {result.Outcome}."),
        };
    }
}
