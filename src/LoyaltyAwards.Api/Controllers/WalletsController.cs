using LoyaltyAwards.Api.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace LoyaltyAwards.Api.Controllers;

[ApiController]
[Route("wallets")]
[Produces("application/json")]
public sealed class WalletsController(LoyaltyStore store) : ControllerBase
{
    [HttpGet("{walletId}")]
    [ProducesResponseType<WalletResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public IActionResult Get(string walletId) =>
        store.GetBalance(walletId) is { } balance
            ? Ok(new WalletResponse(walletId, balance))
            : NotFound(new ErrorResponse("wallet_not_found", $"Wallet '{walletId}' does not exist."));
}
