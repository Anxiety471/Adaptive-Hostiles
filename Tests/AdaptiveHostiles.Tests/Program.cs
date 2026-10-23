using System.Net;
using System.Text;
using System.Text.Json;
using AdaptiveHostiles.Core;

var passed = 0;
var failed = 0;

await Check("offline raid fallback ignores raid points and stays bounded", async () =>
{
    var plan = await new RaidPlanner().PlanAsync(Colony(), eliteRoll: 0.049);
    Require(plan.Scale == RaidScale.Assault, "expected fallback assault for colony readiness");
    Require(plan.RaiderCount == RaidPlanner.PawnCountFor(RaidScale.Assault, 11), "unexpected count");
    Require(plan.IncludeOperative && !plan.IsModelDecision, "elite chance or source incorrect");
    Require(RaidPlanner.PawnCountFor(RaidScale.Invasion, 500) <= 24, "unbounded raiders");
    Require(!(await new RaidPlanner().PlanAsync(Colony(), 0.05)).IncludeOperative, "chance boundary");
});

await Check("model raid decisions are schema-validated", async () =>
{
    var planner = new RaidPlanner(new FakeClient("""
        {"answers":{"scale":{"type":"choice","choice":"invasion"},"approach":{"type":"choice","choice":"flank"}}}
        """));
    var plan = await planner.PlanAsync(Colony(), 0.90);
    Require(plan.IsModelDecision && plan.Scale == RaidScale.Invasion, "model raid scale ignored");
    Require(plan.Approach == RaidApproach.Flank && !plan.IncludeOperative, "model raid labels wrong");

    var invalid = new RaidPlanner(new FakeClient("""
        {"answers":{"scale":{"type":"choice","choice":"godmode"},"approach":{"type":"choice","choice":"flank"}}}
        """));
    Require(!(await invalid.PlanAsync(Colony(), 0.50)).IsModelDecision, "unknown label accepted");
});

await Check("Jev chooses the actual known destination and target", async () =>
{
    var planner = new OperativePlanner(new FakeClient("""
        {"answers":{"movement":{"type":"choice","choice":"cover_142_87"},"target":{"type":"choice","choice":"colonist_1"},"stance":{"type":"choice","choice":"aggressive"}}}
        """));
    var move = await planner.DecideAsync(Battlefield());
    Require(move.IsModelDecision && move.DestinationId == "cover_142_87", "wrong cell selected");
    Require(move.TargetId == "colonist_1" && move.Stance == TacticalStance.Aggressive, "wrong target/stance");
});

await Check("illegal or unreachable movements cannot be issued", async () =>
{
    var planner = new OperativePlanner(new FakeClient("""
        {"answers":{"movement":{"type":"choice","choice":"wall_1"},"target":{"type":"choice","choice":"colonist_1"},"stance":{"type":"choice","choice":"aggressive"}}}
        """));
    var move = await planner.DecideAsync(Battlefield());
    Require(!move.IsModelDecision && move.DestinationId == "cover_142_87", "illegal tile accepted");

    var capture = new FakeClient("""
        {"answers":{"movement":{"type":"choice","choice":"hold"},"target":{"type":"choice","choice":"none"},"stance":{"type":"choice","choice":"defensive"}}}
        """);
    await new OperativePlanner(capture).DecideAsync(Battlefield());
    var json = JsonSerializer.Serialize(capture.LastQuestions);
    Require(!json.Contains("wall_1", StringComparison.Ordinal), "unreachable cell exposed as valid option");
});

await Check("cancellation propagates and does not produce stale move", async () =>
{
    using var cancel = new CancellationTokenSource();
    cancel.Cancel();
    try
    {
        await new OperativePlanner(new CancelledClient()).DecideAsync(Battlefield(), cancel.Token);
        throw new Exception("cancelled request was swallowed");
    }
    catch (OperationCanceledException) { /* expected */ }
});

await Check("official API client sends typed schema to correct endpoint", async () =>
{
    var handler = new CaptureHandler();
    using var http = new HttpClient(handler);
    var client = new JevClient(http, "test-only-key");
    using var response = await client.DecideAsync(
        new { total_wealth = 2000 },
        new Dictionary<string, object> { ["safe"] = new { type = "noul", instructions = "Is this a test?" } },
        CancellationToken.None);
    Require(handler.Uri == JevClient.OfficialEndpoint, "wrong API endpoint");
    Require(handler.Authorization == "Bearer test-only-key", "wrong authorization header");
    using var body = JsonDocument.Parse(handler.Body ?? "");
    Require(body.RootElement.GetProperty("model").GetString() == "jev-latest", "wrong model alias");
    Require(body.RootElement.GetProperty("questions").TryGetProperty("safe", out _), "missing questions");
});

Console.WriteLine($"Tests: {passed} passed, {failed} failed");
Environment.ExitCode = failed == 0 ? 0 : 1;

async Task Check(string name, Func<Task> test)
{
    try
    {
        await test();
        Console.WriteLine($"PASS {name}");
        passed++;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"FAIL {name}: {error.Message}");
        failed++;
    }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static ColonySnapshot Colony() => new(
    TotalWealth: 175000, ColonistCount: 11, CombatReadyCount: 8,
    EquippedRangedWeapons: 7, TurretCount: 2, DefensiveStructureCount: 30,
    InjuredColonistCount: 1, FactionDefName: "OutlanderRough", FactionTechLevel: "Industrial",
    RecentFailedRaids: 2);

static OperativeSnapshot Battlefield() => new(
    PawnId: "operative_1", X: 130, Z: 90, HealthFraction: 0.85f,
    EquippedWeapon: "AssaultRifle", NearbyAllies: 2,
    Destinations: new[]
    {
        new MovementCandidate("cover_142_87", 142, 87, 0.9f, 0.1f, 12, "flank", true),
        new MovementCandidate("wall_1", 143, 87, 1f, 0f, 13, "blocked", false)
    },
    Targets: new[] { new TargetCandidate("colonist_1", 145, 86, 0.8f, true) });

sealed class FakeClient(string json) : IJevClient
{
    public IReadOnlyDictionary<string, object>? LastQuestions { get; private set; }
    public Task<JsonDocument> DecideAsync(object state, IReadOnlyDictionary<string, object> questions, CancellationToken token)
    {
        LastQuestions = questions;
        return Task.FromResult(JsonDocument.Parse(json));
    }
}

sealed class CancelledClient : IJevClient
{
    public Task<JsonDocument> DecideAsync(object state, IReadOnlyDictionary<string, object> questions, CancellationToken token)
        => Task.FromCanceled<JsonDocument>(token);
}

sealed class CaptureHandler : HttpMessageHandler
{
    public string? Uri { get; private set; }
    public string? Authorization { get; private set; }
    public string? Body { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Uri = request.RequestUri?.ToString();
        Authorization = request.Headers.Authorization?.ToString();
        Body = await request.Content!.ReadAsStringAsync(token);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"model":"jev-latest","answers":{"safe":{"type":"noul","noul":0.9}},"usage":{"input_tokens":20,"output_tokens":1}}
                """, Encoding.UTF8, "application/json")
        };
    }
}
