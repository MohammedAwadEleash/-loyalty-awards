using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LoyaltyAwards.Tests;

/// <summary>
/// End-to-end tests through the real HTTP pipeline. Each test gets a fresh app (fresh in-memory state).
/// </summary>
public sealed class AwardsApiTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new();
    private readonly HttpClient _client;

    public AwardsApiTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // ---------- helpers ----------

    private static object Body(string sourceId = "POS_A", string txn = "T1", string wallet = "W100", int points = 25) =>
        new { sourceId, externalTransactionId = txn, walletId = wallet, points };

    private Task<HttpResponseMessage> PostAward(object body) => _client.PostAsJsonAsync("/awards", body);

    private Task<HttpResponseMessage> PostRaw(string json) =>
        _client.PostAsync("/awards", new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private async Task<long> Balance(string walletId)
    {
        var response = await _client.GetAsync($"/wallets/{walletId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await Json(response)).GetProperty("balance").GetInt64();
    }

    private static async Task AssertJsonError(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        var json = await Json(response);
        Assert.Equal(JsonValueKind.Object, json.ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("message").GetString()));
    }

    // ---------- wallets ----------

    [Fact]
    public async Task Initial_state_has_W100_100_and_W200_50()
    {
        var response = await _client.GetAsync("/wallets/W100");
        var json = await Json(response);
        Assert.Equal("W100", json.GetProperty("walletId").GetString());
        Assert.Equal(100, json.GetProperty("balance").GetInt64());
        Assert.Equal(50, await Balance("W200"));
    }

    [Theory]
    [InlineData("W999")]
    [InlineData("w100")] // case-sensitive
    public async Task Unknown_wallet_lookup_returns_404(string walletId) =>
        await AssertJsonError(await _client.GetAsync($"/wallets/{walletId}"), HttpStatusCode.NotFound);

    // ---------- award behaviour ----------

    [Fact]
    public async Task New_award_returns_201_with_record_and_credits_once()
    {
        var response = await PostAward(Body());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var award = await Json(response);
        Assert.False(string.IsNullOrEmpty(award.GetProperty("awardId").GetString()));
        Assert.Equal("POS_A", award.GetProperty("sourceId").GetString());
        Assert.Equal("T1", award.GetProperty("externalTransactionId").GetString());
        Assert.Equal("W100", award.GetProperty("walletId").GetString());
        Assert.Equal(25, award.GetProperty("points").GetInt32());
        Assert.Equal(125, await Balance("W100"));
    }

    [Fact]
    public async Task Matching_retry_returns_200_with_original_award_and_no_second_credit()
    {
        var first = await Json(await PostAward(Body()));

        var retry = await PostAward(Body());

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var replayed = await Json(retry);
        Assert.Equal(first.GetProperty("awardId").GetString(), replayed.GetProperty("awardId").GetString());
        Assert.Equal(first.ToString(), replayed.ToString());
        Assert.Equal(125, await Balance("W100"));
    }

    [Fact]
    public async Task Same_transaction_id_from_different_source_is_a_different_award()
    {
        var a = await PostAward(Body(sourceId: "POS_A"));
        var b = await PostAward(Body(sourceId: "POS_B"));

        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);
        Assert.NotEqual((await Json(a)).GetProperty("awardId").GetString(), (await Json(b)).GetProperty("awardId").GetString());
        Assert.Equal(150, await Balance("W100"));
    }

    [Fact]
    public async Task Identifiers_are_case_sensitive()
    {
        Assert.Equal(HttpStatusCode.Created, (await PostAward(Body(txn: "T1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostAward(Body(txn: "t1"))).StatusCode);
        await AssertJsonError(await PostAward(Body(txn: "T2", wallet: "w100")), HttpStatusCode.NotFound);
        Assert.Equal(150, await Balance("W100"));
    }

    [Fact]
    public async Task Conflicting_points_returns_409_and_changes_nothing()
    {
        var original = await Json(await PostAward(Body(points: 25)));

        await AssertJsonError(await PostAward(Body(points: 30)), HttpStatusCode.Conflict);

        Assert.Equal(125, await Balance("W100"));
        var replay = await PostAward(Body(points: 25)); // original award is intact
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(original.ToString(), (await Json(replay)).ToString());
    }

    [Fact]
    public async Task Conflicting_wallet_returns_409_and_neither_wallet_changes()
    {
        await PostAward(Body(wallet: "W100"));

        await AssertJsonError(await PostAward(Body(wallet: "W200")), HttpStatusCode.Conflict);

        Assert.Equal(125, await Balance("W100"));
        Assert.Equal(50, await Balance("W200"));
    }

    [Fact]
    public async Task Unknown_wallet_returns_404_changes_nothing_and_does_not_consume_identity()
    {
        await AssertJsonError(await PostAward(Body(wallet: "W999", points: 40)), HttpStatusCode.NotFound);
        Assert.Equal(100, await Balance("W100"));
        Assert.Equal(50, await Balance("W200"));

        // Corrected request reuses the same identity, with different points, and is accepted as new.
        var corrected = await PostAward(Body(wallet: "W100", points: 25));
        Assert.Equal(HttpStatusCode.Created, corrected.StatusCode);
        Assert.Equal(125, await Balance("W100"));
    }

    [Fact]
    public async Task Precedence_conflict_beats_unknown_wallet_for_an_existing_identity()
    {
        await PostAward(Body(wallet: "W100"));
        await AssertJsonError(await PostAward(Body(wallet: "W999")), HttpStatusCode.Conflict);
    }

    // ---------- validation ----------

    [Theory]
    [InlineData("{")]                     // malformed JSON
    [InlineData("")]                      // empty body
    [InlineData("[]")]                    // non-object bodies
    [InlineData("\"text\"")]
    [InlineData("123")]
    [InlineData("null")]
    [InlineData("{}")]                    // all fields missing
    [InlineData("""{"externalTransactionId":"T1","walletId":"W100","points":25}""")]
    [InlineData("""{"sourceId":"POS_A","walletId":"W100","points":25}""")]
    [InlineData("""{"sourceId":"POS_A","externalTransactionId":"T1","points":25}""")]
    [InlineData("""{"sourceId":"POS_A","externalTransactionId":"T1","walletId":"W100"}""")]
    [InlineData("""{"sourceId":"","externalTransactionId":"T1","walletId":"W100","points":25}""")]
    [InlineData("""{"sourceId":123,"externalTransactionId":"T1","walletId":"W100","points":25}""")]
    [InlineData("""{"sourceId":null,"externalTransactionId":"T1","walletId":"W100","points":25}""")]
    [InlineData("""{"sourceId":"POS A","externalTransactionId":"T1","walletId":"W100","points":25}""")]
    [InlineData("""{"sourceId":"POS.A","externalTransactionId":"T1","walletId":"W100","points":25}""")]
    [InlineData("""{"sourceId":"PÖS","externalTransactionId":"T1","walletId":"W100","points":25}""")]
    [InlineData("""{"sourceId":"POS_A\n","externalTransactionId":"T1","walletId":"W100","points":25}""")]
    [InlineData("""{"sourceId":"POS_A","externalTransactionId":"T1","walletId":["W100"],"points":25}""")]
    [InlineData("""{"sourceId":"POS_A","externalTransactionId":"T1","walletId":"W100","points":"25"}""")]
    [InlineData("""{"sourceId":"POS_A","externalTransactionId":"T1","walletId":"W100","points":25.5}""")]
    [InlineData("""{"sourceId":"POS_A","externalTransactionId":"T1","walletId":"W100","points":25.0}""")]
    [InlineData("""{"sourceId":"POS_A","externalTransactionId":"T1","walletId":"W100","points":1e2}""")]
    [InlineData("""{"sourceId":"POS_A","externalTransactionId":"T1","walletId":"W100","points":0}""")]
    [InlineData("""{"sourceId":"POS_A","externalTransactionId":"T1","walletId":"W100","points":-5}""")]
    [InlineData("""{"sourceId":"POS_A","externalTransactionId":"T1","walletId":"W100","points":1001}""")]
    [InlineData("""{"sourceId":"POS_A","externalTransactionId":"T1","walletId":"W100","points":99999999999}""")]
    [InlineData("""{"sourceId":"POS_A","externalTransactionId":"T1","walletId":"W100","points":true}""")]
    [InlineData("""{"sourceId":"POS_A","externalTransactionId":"T1","walletId":"W100","points":null}""")]
    public async Task Invalid_requests_return_400_json_and_change_nothing(string json)
    {
        await AssertJsonError(await PostRaw(json), HttpStatusCode.BadRequest);
        Assert.Equal(100, await Balance("W100"));
    }

    [Fact]
    public async Task Property_names_are_case_sensitive()
    {
        await AssertJsonError(
            await PostRaw("""{"SOURCEID":"POS_A","externalTransactionId":"T1","walletId":"W100","points":25}"""),
            HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unknown_route_and_wrong_method_return_json_errors()
    {
        await AssertJsonError(await _client.GetAsync("/nothing-here"), HttpStatusCode.NotFound);
        await AssertJsonError(await _client.GetAsync("/awards"), HttpStatusCode.MethodNotAllowed);
        await AssertJsonError(await _client.PostAsync("/wallets/W100", null), HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task Identifier_longer_than_64_chars_is_rejected()
    {
        await AssertJsonError(await PostAward(Body(txn: new string('a', 65))), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Boundary_values_are_accepted()
    {
        Assert.Equal(HttpStatusCode.Created, (await PostAward(Body(txn: new string('a', 64), points: 1000))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostAward(Body(txn: "a-b_C-9", points: 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostAward(Body(txn: "X"))).StatusCode);
        Assert.Equal(1126, await Balance("W100"));
    }

    [Fact]
    public async Task Validation_runs_before_award_lookup()
    {
        await PostAward(Body(points: 25));
        // Same identity, but invalid points: must be 400, not 200 or 409.
        await AssertJsonError(await PostAward(Body(points: 0)), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Validation_error_lists_every_invalid_field()
    {
        // Types are correct, values are not: every field is reported at once.
        var response = await PostRaw("""{"sourceId":"","walletId":"bad id","points":0}""");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var fields = (await Json(response)).GetProperty("details").EnumerateArray()
            .Select(d => d.GetProperty("field").GetString()).ToHashSet();
        Assert.Equal(new HashSet<string?> { "sourceId", "externalTransactionId", "walletId", "points" }, fields);
    }
}
