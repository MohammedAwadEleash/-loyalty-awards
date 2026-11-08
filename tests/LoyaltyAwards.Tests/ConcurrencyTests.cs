using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LoyaltyAwards.Tests;

/// <summary>
/// Fires many requests at the same moment (released together by a gate) and checks the invariants.
/// Each scenario is repeated over several rounds so that races have many chances to show up.
/// </summary>
public sealed class ConcurrencyTests : IDisposable
{
    private const int Rounds = 20;

    private readonly WebApplicationFactory<Program> _factory = new();
    private readonly HttpClient _client;

    public ConcurrencyTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<HttpResponseMessage[]> FireTogether(IEnumerable<object> bodies)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = bodies.Select(body => Task.Run(async () =>
        {
            await gate.Task;
            return await _client.PostAsJsonAsync("/awards", body);
        })).ToArray();

        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    private async Task<long> Balance(string walletId)
    {
        var json = await _client.GetFromJsonAsync<JsonElement>($"/wallets/{walletId}");
        return json.GetProperty("balance").GetInt64();
    }

    private static object Body(string txn, int points, string wallet = "W100", string source = "POS_A") =>
        new { sourceId = source, externalTransactionId = txn, walletId = wallet, points };

    [Fact]
    public async Task Simultaneous_matching_requests_produce_one_201_and_the_rest_200()
    {
        const int copies = 25;
        var expected = 100L;

        for (var round = 0; round < Rounds; round++)
        {
            var responses = await FireTogether(Enumerable.Range(0, copies).Select(_ => Body($"DUP{round}", 7)));

            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
            Assert.Equal(copies - 1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));

            var awardIds = await Task.WhenAll(responses.Select(async r =>
                (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("awardId").GetString()));
            Assert.Single(awardIds.Distinct());

            expected += 7;
            Assert.Equal(expected, await Balance("W100"));
        }
    }

    [Fact]
    public async Task Distinct_concurrent_awards_to_one_wallet_are_all_credited()
    {
        const int count = 200;
        var bodies = Enumerable.Range(1, count).Select(i => Body($"D{i}", points: i % 10 + 1)).ToList();

        var responses = await FireTogether(bodies);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var sum = Enumerable.Range(1, count).Sum(i => i % 10 + 1);
        Assert.Equal(100 + sum, await Balance("W100"));
    }

    [Fact]
    public async Task Concurrent_same_identity_with_different_points_gives_one_accept_and_one_conflict()
    {
        var expected = 100L;

        for (var round = 0; round < Rounds; round++)
        {
            var responses = await FireTogether([Body($"C{round}", 10), Body($"C{round}", 20)]);

            var created = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);

            // The balance reflects whichever request won, and only that one.
            expected += (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("points").GetInt32();
            Assert.Equal(expected, await Balance("W100"));
        }
    }

    [Fact]
    public async Task Mixed_concurrent_traffic_keeps_both_wallets_consistent()
    {
        // Retries, distinct awards and both wallets interleaved together.
        var bodies = new List<object>();
        for (var i = 0; i < 50; i++)
        {
            bodies.Add(Body($"M{i}", 3, "W100"));
            bodies.Add(Body($"M{i}", 3, "W100"));          // retry of the same award
            bodies.Add(Body($"M{i}", 5, "W200", "POS_B"));  // same txn id, other source, other wallet
        }

        var responses = await FireTogether(bodies);

        Assert.Equal(100, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(50, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(100 + 50 * 3, await Balance("W100"));
        Assert.Equal(50 + 50 * 5, await Balance("W200"));
    }
}
