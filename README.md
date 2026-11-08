# 🎁 Loyalty Awards API

![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)
![ASP.NET Core](https://img.shields.io/badge/ASP.NET%20Core-Web%20API-0078D4)
![Tests](https://img.shields.io/badge/tests-50%20passing-2EA44F)
![Storage](https://img.shields.io/badge/storage-in--memory-F0AD4E)

A small single-process API that awards loyalty points safely under partner retries and concurrent requests.

> [!NOTE]
> Starting state: wallet **W100 = 100** points, wallet **W200 = 50** points, no awards.

---

## ⚙️ Runtime

- .NET SDK 9.0 (built and tested with SDK 9.0.320; `global.json` allows newer 9.0.x feature bands)
- ASP.NET Core Web API (controllers), xUnit + `Microsoft.AspNetCore.Mvc.Testing` for tests

## 🚀 Commands

```bash
# Get the code
git clone https://github.com/MohammedAwadEleash/-loyalty-awards.git
cd ./-loyalty-awards

# Start the service on http://localhost:5080
dotnet run --project src/LoyaltyAwards.Api

# Run all tests (every test goes through the real HTTP pipeline)
dotnet test
```

> [!TIP]
> **Reset to the initial state:** state is in memory only, so stop the process (`Ctrl+C`) and start it again.
> Each test class creates its own fresh application, so tests never need a reset.

### Example requests

```bash
curl -i -X POST http://localhost:5080/awards -H "Content-Type: application/json" \
  -d '{"sourceId":"POS_A","externalTransactionId":"T1","walletId":"W100","points":25}'

curl -i http://localhost:5080/wallets/W100
```

---

## 📡 API

| Request | Status | Result |
|---|:---:|---|
| `POST /awards`, new identity, existing wallet | 🟢 `201` | Award record, balance + points once |
| `POST /awards`, same identity, same wallet + points | 🟢 `200` | Original award record, no balance change |
| `POST /awards`, same identity, different wallet or points | 🟠 `409` | Nothing changes |
| `POST /awards`, new identity, unknown wallet | 🟠 `404` | Nothing changes, identity not consumed |
| `POST /awards`, invalid field / malformed JSON / non-object | 🔴 `400` | Field errors listed |
| `GET /wallets/{walletId}` | 🟢 `200` / 🟠 `404` | `{walletId, balance}` |

**Award record** (`201` and `200`):

```json
{
  "awardId": "awd_6f573f85de5f4a458cfbbc4e316e145b",
  "sourceId": "POS_A",
  "externalTransactionId": "T1",
  "walletId": "W100",
  "points": 25
}
```

**Error body**: every error is a JSON object, including 404 for unknown routes and 405 for a wrong method. `details` appears only for field validation errors and lists every invalid field at once.

```json
{
  "error": "invalid_request",
  "message": "The request body is missing, malformed or has invalid fields.",
  "details": [{ "field": "points", "message": "Must be an integer from 1 to 1000." }]
}
```

### 🔀 Error precedence

> [!IMPORTANT]
> 1. **400**: body and field validation, before any state is read.
> 2. **200 / 409**: the identity `(sourceId, externalTransactionId)` is looked up next. If it already exists, the request is a retry. It returns 200 if wallet and points match, otherwise 409, **even if the new walletId does not exist**. A request that collides with a recorded award is a conflict whatever else is wrong with it.
> 3. **404**: unknown wallet, for identities not seen before.
> 4. **201**: accepted.

### ✅ Validation details

- Validation uses standard ASP.NET model binding: `[FromBody] AwardRequestDto` with `[Required]`, `[RegularExpression]` and `[Range]`.
- Identifiers: JSON strings matching `^[A-Za-z0-9_-]{1,64}$` (ASCII only, values case-sensitive; a trailing newline is rejected).
- `points`: a JSON integer from 1 to 1000. `JsonNumberHandling.Strict` is set so `"25"` is rejected; `25.5`, `25.0`, `1e2`, `true` and `null` are rejected too.
- A wrong JSON type (for example a number for an id) is reported for that field only, because the JSON reader stops at the first type error. Value errors (empty, bad format, out of range) are all reported together.
- Property names are case-sensitive (`sourceId`, not `SOURCEID`). Unknown properties are ignored. If a property appears twice, the last value wins (System.Text.Json behaviour).
- `Content-Type` must be `application/json`; anything else returns `415` (ASP.NET's standard ProblemDetails body).
- `GET /wallets/{id}` returns 404 for any id that is not an existing wallet, including badly formatted ids.

---

## 📝 Handoff

### 🧩 Design

- Validation is standard `[FromBody]` model binding with data annotations. The one non-default setting is `JsonNumberHandling.Strict`, because the web default would accept `"25"` as a number. A small `InvalidModelStateResponseFactory` keeps every 400 in the same JSON error shape.
- `LoyaltyStore` holds wallets and accepted awards in dictionaries behind **one lock**. Inside that lock, "look up identity → check wallet → record award + credit balance" runs as a single atomic step. That makes all the concurrency requirements hold by construction. Two identical requests can never both see "no award yet", and two different awards can never lose an update to the same balance.
- For one process with a microsecond-long critical section, a global lock is the proportionate choice. Per-wallet locks or `ConcurrentDictionary.GetOrAdd` would add risk without measurable benefit here.
- The award is stored only after the wallet check succeeds, so a 404 never consumes the identity.
- The HTTP layer is two thin controllers (`AwardsController`, `WalletsController`) that only map store outcomes to status codes.

### 🧪 Checks executed

- `dotnet test`: **50 tests pass**, covering every behaviour rule, about 30 validation cases, boundary values, precedence, and 4 concurrency scenarios. The concurrency scenarios are: 25 identical requests × 20 rounds, 200 distinct awards, same identity with different points × 20 rounds, and mixed traffic across both wallets. Requests are released together through a gate.
- **Mutation check**: with the lock removed (plus a 1 ms delay between check and write), all 4 concurrency tests failed. They detect the races they are meant to.
- **Manual test** with `curl` against `dotnet run`: 201, 200, 409, 404, 400 (wrong type, bad values, malformed, empty, array), 415, both wallet lookups, and 30 identical requests in parallel (one 201, 29 × 200, balance credited once).

### ⚠️ Limitations

- State is in memory: lost on restart, single instance only. A multi-instance version needs a database with a unique constraint on `(sourceId, externalTransactionId)` and the insert and balance update in one transaction.
- Awards are kept forever, so memory grows with traffic. There is no retention window.
- One global lock serialises all writes. That is fine at this scale but would be the first thing to change under heavy load.
- No authentication, and no request-size limit beyond Kestrel's default.

### ⏱️ Time spent

About 1 hour, including reading the brief, implementation, testing and documentation.
