namespace LoyaltyAwards.Api;

public sealed record AwardRequest(string SourceId, string ExternalTransactionId, string WalletId, int Points);

public sealed record Award(string AwardId, string SourceId, string ExternalTransactionId, string WalletId, int Points);

public enum AwardOutcome
{
    Created,        // 201 - new award accepted, balance increased once
    Replayed,       // 200 - matching retry, original award returned, no balance change
    Conflict,       // 409 - same identity, different wallet or points
    WalletNotFound, // 404 - nothing changed, identity not consumed
}

public sealed record AwardResult(AwardOutcome Outcome, Award? Award);

/// <summary>
/// In-memory state for wallets and accepted awards.
/// A single lock makes "look up identity -> check wallet -> record award + credit balance"
/// one atomic step. That is what makes concurrent retries safe: two identical requests can
/// never both see "no award yet". At this scale (one process, in-memory, microsecond critical
/// section) one lock is simpler and easier to prove correct than per-wallet or lock-free schemes.
/// </summary>
public sealed class LoyaltyStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _balances = new(StringComparer.Ordinal);
    private readonly Dictionary<(string SourceId, string ExternalTransactionId), Award> _awards = new();

    public LoyaltyStore() => Reset();

    /// <summary>Restores the initial state: W100 = 100, W200 = 50, no awards.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _awards.Clear();
            _balances.Clear();
            _balances["W100"] = 100;
            _balances["W200"] = 50;
        }
    }

    public AwardResult Award(AwardRequest request)
    {
        var identity = (request.SourceId, request.ExternalTransactionId);

        lock (_gate)
        {
            // 1. Identity first: a retry is answered from the recorded award.
            if (_awards.TryGetValue(identity, out var existing))
            {
                var matches = existing.WalletId == request.WalletId && existing.Points == request.Points;
                return new AwardResult(matches ? AwardOutcome.Replayed : AwardOutcome.Conflict, existing);
            }

            // 2. Unknown wallet: reject without recording anything, so the identity stays reusable.
            if (!_balances.TryGetValue(request.WalletId, out var balance))
                return new AwardResult(AwardOutcome.WalletNotFound, null);

            // 3. Accept: record the award and credit the wallet in the same critical section.
            var award = new Award(
                AwardId: $"awd_{Guid.NewGuid():N}",
                request.SourceId,
                request.ExternalTransactionId,
                request.WalletId,
                request.Points);

            _awards.Add(identity, award);
            _balances[request.WalletId] = balance + request.Points;
            return new AwardResult(AwardOutcome.Created, award);
        }
    }

    public long? GetBalance(string walletId)
    {
        lock (_gate)
        {
            return _balances.TryGetValue(walletId, out var balance) ? balance : null;
        }
    }
}
