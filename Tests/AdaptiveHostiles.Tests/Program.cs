using static AdaptiveHostiles.Core.StallTracker;
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

await Check("anthropic client sends headers and only accepts listed choices", async () =>
{
    var h = new StubHandler("""{"content":[{"type":"text","text":"```json\n{\"scale\":\"assault\",\"approach\":\"teleport\"}\n```"}]}""");
    using var http = new HttpClient(h);
    using var r = await new AnthropicClient(http, "k").DecideAsync(new { w = 1 }, Questions(), CancellationToken.None);
    Require(h.Uri == AnthropicClient.OfficialEndpoint && h.Headers["x-api-key"] == "k", "wrong endpoint/key header");
    Require(JevAnswers.Choice(r.RootElement, "scale") == "assault", "valid choice lost");
    Require(JevAnswers.Choice(r.RootElement, "approach") is null, "unlisted choice accepted");
});

await Check("openai-compatible client uses custom base url and tolerates no key", async () =>
{
    var h = new StubHandler("""{"choices":[{"message":{"content":"{\"scale\":\"recon\"}"}}]}""");
    using var http = new HttpClient(h);
    using var r = await new OpenAiCompatibleClient(http, null, "http://localhost:11434/v1/", "llama").DecideAsync(
        new { w = 1 }, Questions(), CancellationToken.None);
    Require(h.Uri == "http://localhost:11434/v1/chat/completions" && h.Headers["auth"] == "", "wrong url/auth");
    Require(JevAnswers.Choice(r.RootElement, "scale") == "recon", "choice lost");
    var garbage = new StubHandler("""{"choices":[{"message":{"content":"I refuse"}}]}""");
    using var g = await new OpenAiCompatibleClient(new HttpClient(garbage), "k").DecideAsync(
        new { w = 1 }, Questions(), CancellationToken.None);
    Require(JevAnswers.Choice(g.RootElement, "scale") is null, "garbage produced a choice");
});

await Check("provider selection follows env and falls back to local", async () =>
{
    using var http = new HttpClient();
    IJevClient? Pick(params (string, string)[] env) =>
        DecisionProviders.Create(http, k => env.Where(e => e.Item1 == k).Select(e => e.Item2).FirstOrDefault());
    Require(Pick() is null, "no keys should mean local fallback");
    Require(Pick(("TYPESAFE_API_KEY", "a"), ("ANTHROPIC_API_KEY", "b")) is JevClient, "auto should prefer Jev");
    Require(Pick(("ANTHROPIC_API_KEY", "b")) is AnthropicClient, "auto should use Anthropic");
    Require(Pick(("TYPESAFE_API_KEY", "a"), ("ANTHROPIC_API_KEY", "b"), ("ADAPTIVE_HOSTILES_PROVIDER", "claude")) is AnthropicClient, "explicit provider ignored");
    Require(Pick(("ADAPTIVE_HOSTILES_PROVIDER", "openai"), ("ADAPTIVE_HOSTILES_ENDPOINT", "http://localhost:1/v1")) is OpenAiCompatibleClient, "keyless local endpoint");
    Require(Pick(("TYPESAFE_API_KEY", "a"), ("ADAPTIVE_HOSTILES_PROVIDER", "local")) is null, "local must disable remote");
    Require(Pick(("ADAPTIVE_HOSTILES_PROVIDER", "anthropic")) is null, "missing key must fall back");
    await Task.CompletedTask;
});

await Check("player settings win over env, blank settings defer to env", async () =>
{
    using var http = new HttpClient();
    Func<string, string?> env = k => k == "TYPESAFE_API_KEY" ? "envkey" : null;
    Require(DecisionProviders.Create(http, new ProviderSettings().ToLookup(env)) is JevClient, "blank settings should defer to env");
    var s = new ProviderSettings { AnthropicKey = " k " };
    Require(DecisionProviders.Create(http, s.ToLookup(env)) is JevClient, "auto keeps env priority");
    Require(DecisionProviders.Create(http, (s with { Provider = DecisionProvider.Anthropic }).ToLookup(env)) is AnthropicClient, "picked provider ignored");
    Require(DecisionProviders.Create(http, (s with { Provider = DecisionProvider.Local }).ToLookup(env)) is null, "local setting must disable remote");
    var local = new ProviderSettings { Provider = DecisionProvider.OpenAiCompatible, OpenAiBaseUrl = "http://localhost:11434/v1" };
    Require(DecisionProviders.Create(http, local.ToLookup(_ => null)) is OpenAiCompatibleClient, "keyless local server");
    await Task.CompletedTask;
});

await Check("roster is independent of raid points, deterministic and bounded", async () =>
{
    var plan = new RaidPlan(RaidScale.Invasion, RaidApproach.Breach, 12, true, true);
    var a = RosterGenerator.Generate(plan, Kinds(), seed: 42);
    var b = RosterGenerator.Generate(plan, Kinds(), seed: 42);
    Require(a.Entries.SequenceEqual(b.Entries), "same seed produced different roster");
    Require(a.Entries.Count == 12 && !a.Bypassed, "wrong roster size");
    Require(a.Entries.Count(e => e.IsOperative) == 1, "expected exactly one operative");
    var capped = RosterGenerator.Generate(plan with { IncludeOperative = false }, Kinds(), 1,
        new AdaptiveHostilesSettings { MaxRaiders = 5, CountMultiplier = 3 });
    Require(capped.Entries.Count == 5 && capped.Entries.All(e => !e.IsOperative), "cap/operative flag ignored");
    var none = RosterGenerator.Generate(plan, Array.Empty<RosterKind>(), 1);
    Require(none.Bypassed && none.Warning is not null, "empty faction must defer to vanilla");
    await Task.CompletedTask;
});

await Check("operative profile and elite roll are seeded and in range", async () =>
{
    var p = RosterGenerator.RollOperativeProfile(7);
    Require(p == RosterGenerator.RollOperativeProfile(7), "profile not deterministic");
    Require(p.Shooting is >= 16 and <= 20 && p.Melee is >= 14 and <= 20, "skills out of range");
    var r = RosterGenerator.RollElite(99);
    Require(r is >= 0 and < 1 && r == RosterGenerator.RollElite(99), "elite roll invalid");
    await Task.CompletedTask;
});

await Check("operative gear is seeded, tech-bounded and falls back to melee", async () =>
{
    var items = new List<GearCandidate>
    {
        new("Revolver", GearSlot.RangedWeapon, 4, 8f), new("Rifle", GearSlot.RangedWeapon, 4, 12f),
        new("ChargeRifle", GearSlot.RangedWeapon, 5, 20f), new("Bow", GearSlot.RangedWeapon, 3, 5f),
        new("Club", GearSlot.MeleeWeapon, 2, 4f),
        new("FlakVest", GearSlot.BodyArmor, 4, 1.2f), new("Marine", GearSlot.BodyArmor, 5, 2f),
        new("SimpleHelmet", GearSlot.Helmet, 4, 0.8f),
    };
    var a = OperativeGear.Roll(items, 4, 11)!;
    Require(a == OperativeGear.Roll(items, 4, 11), "gear not deterministic");
    Require(a.Weapon.DefName is "Revolver" or "Rifle", "weapon exceeds faction tech or isn't top tier");
    Require(a.BodyArmor!.DefName == "FlakVest" && a.Helmet!.DefName == "SimpleHelmet", "armor wrong for tech");
    for (ulong s = 0; s < 200; s++)
        Require(OperativeGear.Roll(items, 5, s)!.Weapon.DefName == "ChargeRifle", "spacer weapon not used");
    var tribal = OperativeGear.Roll(items, 2, 1)!;
    Require(tribal.Weapon.DefName == "Club" && tribal.BodyArmor is null && tribal.Helmet is null, "neolithic fallback wrong");
    Require(OperativeGear.Roll(new List<GearCandidate>(), 4, 1) is null, "empty candidates should yield null");
    await Task.CompletedTask;
});

await Check("candidate enumeration is bounded, reachable and deterministic", async () =>
{
    var map = new GridMap();
    var list = CandidateEnumerator.Enumerate(map, 50, 50, new[] { (60, 50) });
    Require(list.Count > 0 && list.Count <= OperativePlanner.MaxDestinations - 1, "bad candidate count");
    Require(list.All(c => c.Reachable && map.IsReachable(c.X, c.Z) && c.Id == $"cell_{c.X}_{c.Z}"), "bad candidate");
    Require(list.Select(c => c.Id).Distinct().Count() == list.Count, "duplicate ids");
    Require(list.Select(c => c.Purpose).Distinct().Count() > 1, "no purpose mix");
    Require(list.Select(c => c.Id).SequenceEqual(CandidateEnumerator.Enumerate(map, 50, 50, new[] { (60, 50) }).Select(c => c.Id)), "nondeterministic");
    await Task.CompletedTask;
});

await Check("order resolver revalidates and never trusts stale cells", async () =>
{
    var snap = Battlefield();
    var go = new OperativeDecision("cover_142_87", "colonist_1", TacticalStance.Aggressive, true);
    var open = new GridMap { Open = true };
    var record = new OperativeRecord("operative_1", "Pirate");
    var ok = OperativeOrderResolver.Resolve(go, snap, open, PawnBlock.None, record, tick: 10);
    Require(ok.Kind == OrderKind.MoveTo && ok.X == 142 && ok.Z == 87 && ok.TargetId == "colonist_1", "move not issued");
    Require(record.Log.Count == 1 && record.Log[0].X == 142 && record.Log[0].DestinationId == "cover_142_87", "log mismatch");
    var closed = new GridMap { Open = false };
    Require(OperativeOrderResolver.Resolve(go, snap, closed, PawnBlock.None).Kind == OrderKind.VanillaFallback, "stale cell accepted");
    var fake = go with { DestinationId = "cell_1_1" };
    Require(OperativeOrderResolver.Resolve(fake, snap, open, PawnBlock.None).Kind == OrderKind.VanillaFallback, "unknown id accepted");
    Require(OperativeOrderResolver.Resolve(go, snap, open, PawnBlock.Downed).Kind == OrderKind.None, "interrupted downed pawn");
    Require(OperativeOrderResolver.Resolve(go with { DestinationId = "hold" }, snap, open, PawnBlock.None).Kind == OrderKind.Hold, "hold broken");
    await Task.CompletedTask;
});

await Check("registry cooldown, commitment and block rules", async () =>
{
    var reg = new OperativeRegistry();
    var r = reg.Register("p1", "Pirate");
    Require(ReferenceEquals(r, reg.Register("p1", "Pirate")), "duplicate record");
    Require(OperativeRegistry.ShouldRequest(r, 0, TacticalTrigger.None, PawnBlock.None), "initial request due");
    OperativeRegistry.ScheduleNext(r, 1000, seed: 5);
    Require(r.NextDecisionTick is >= 1120 and <= 1300, "cooldown out of range");
    Require(!OperativeRegistry.ShouldRequest(r, 1010, TacticalTrigger.Damaged, PawnBlock.None), "ignored commitment");
    Require(OperativeRegistry.ShouldRequest(r, 1100, TacticalTrigger.Damaged, PawnBlock.None), "no reaction to damage");
    Require(!OperativeRegistry.ShouldRequest(r, 5000, TacticalTrigger.None, PawnBlock.MentalBreak), "interrupted break");
    for (var i = 0; i < 200; i++) r.Append(new DecisionLogEntry(i, "hold", 0, 0, null, TacticalStance.Defensive, false));
    Require(r.Log.Count == OperativeRecord.MaxLog, "log unbounded");
    Require(reg.Remove("p1") && !reg.TryGet("p1", out _), "remove failed");
    await Task.CompletedTask;
});

await Check("scheduler throttles, times out, backs off and drops stale results", async () =>
{
    var now = DateTime.UtcNow;
    var clock = () => now;
    using var s = new JevScheduler<int>(new AdaptiveHostilesSettings { MaxConcurrentRequests = 1, NetworkDeadline = TimeSpan.FromMilliseconds(100) },
        isModelResult: v => v > 0, now: clock, baseBackoff: TimeSpan.FromSeconds(10));
    var gate = new TaskCompletionSource<int>();
    Require(s.TryBegin("a", _ => gate.Task), "first begin refused");
    Require(!s.TryBegin("a", _ => Task.FromResult(1)), "duplicate key allowed");
    Require(!s.TryBegin("b", _ => Task.FromResult(1)), "concurrency cap ignored");
    gate.SetResult(7);
    await WaitFor(() => s.InFlightCount == 0);
    Require(s.TryTake("a", TimeSpan.FromSeconds(1), out var got) && got == 7, "result lost");
    Require(!s.TryTake("a", TimeSpan.FromSeconds(1), out _), "result delivered twice");

    Require(s.TryBegin("t", async token => { await Task.Delay(Timeout.Infinite, token); return 1; }), "begin refused");
    await WaitFor(() => s.InFlightCount == 0);
    Require(s.ConsecutiveFailures == 1, "timeout not counted");
    Require(!s.TryBegin("c", _ => Task.FromResult(1)), "no backoff after failure");
    now = now.AddSeconds(11);
    Require(s.TryBegin("c", _ => Task.FromResult(1)), "still blocked after backoff");
    await WaitFor(() => s.InFlightCount == 0);
    Require(s.ConsecutiveFailures == 0, "success did not clear failures");

    var late = new TaskCompletionSource<int>();
    Require(s.TryBegin("d", _ => late.Task), "begin refused");
    s.Reset();
    late.SetResult(5);
    await Task.Delay(50);
    Require(!s.TryTake("d", TimeSpan.FromSeconds(1), out _), "stale result survived reset");
    Require(s.TryBegin("d", _ => Task.FromResult(2)), "key stuck after reset");
});

await Check("decision cache expires", async () =>
{
    var now = DateTime.UtcNow;
    var cache = new DecisionCache<int>(TimeSpan.FromSeconds(30), () => now);
    cache.Put("k", 3);
    Require(cache.TryGet("k", out var v) && v == 3, "cache miss");
    now = now.AddSeconds(31);
    Require(!cache.TryGet("k", out _), "expired entry served");
    await Task.CompletedTask;
});

await Check("raid history classifies outcomes, windows per faction and caps size", () =>
{
    Require(RaidHistory.Classify(6, 3, 0) == RaidResult.RaidersFailed, "half down should fail the raid");
    Require(RaidHistory.Classify(6, 2, 1) == RaidResult.RaidersSucceeded, "colony losses should be a raider success");
    Require(RaidHistory.Classify(6, 2, 0) == RaidResult.Inconclusive, "no losses either way is inconclusive");
    Require(RaidHistory.Classify(0, 0, 0) == RaidResult.Inconclusive, "empty raid is inconclusive");

    var history = new RaidHistory();
    for (var i = 0; i < 7; i++) history.Record("A", i, 4, 4, 0, 0);
    history.Record("B", 100, 4, 0, 0, 2);
    Require(history.RecentFailed("A") == RaidHistory.RecentWindow, "window should cap at the last five raids");
    Require(history.RecentSucceeded("A") == 0 && history.RecentSucceeded("B") == 1, "factions must be tracked separately");
    for (var i = 0; i < 100; i++) history.Record("C", 200 + i, 4, 0, 0, 0);
    Require(history.Entries.Count == RaidHistory.MaxEntries, "history must stay bounded");
    return Task.CompletedTask;
});

await Check("raid memory adapts fallback scale by at most one step", async () =>
{
    Require(RaidPlanner.Adapt(RaidScale.Skirmish, 3, 0) == RaidScale.Assault, "repeated failures should escalate");
    Require(RaidPlanner.Adapt(RaidScale.Invasion, 5, 0) == RaidScale.Invasion, "escalation is capped");
    Require(RaidPlanner.Adapt(RaidScale.Assault, 0, 3) == RaidScale.Skirmish, "repeated wins should back off");
    Require(RaidPlanner.Adapt(RaidScale.Recon, 0, 5) == RaidScale.Recon, "back-off is capped");
    Require(RaidPlanner.Adapt(RaidScale.Assault, 2, 0) == RaidScale.Assault, "below threshold changes nothing");
    Require(RaidPlanner.Adapt(RaidScale.Assault, 3, 3) == RaidScale.Assault, "mixed history changes nothing");

    var plan = await new RaidPlanner().PlanAsync(Colony() with { RecentFailedRaids = 3 }, 1.0);
    Require(plan.Scale == RaidScale.Invasion, "fallback plan should use raid memory");
});

await Check("tactical loop: offline fallback applies a revalidated order and honours cooldown", async () =>
{
    var loop = new OperativeTacticalLoop(new OperativePlanner(), null, new AdaptiveHostilesSettings());
    var rec = new OperativeRecord("op", "Pirate");
    var host = new FakeHost();
    loop.Tick(rec, host, 100, 1);
    Require(host.Applied.Count == 1 && host.Applied[0].Kind == OrderKind.MoveTo, "no order applied");
    Require(host.Samples == 1 && rec.NextDecisionTick >= 220, "cooldown not scheduled");
    loop.Tick(rec, host, 110, 1);
    Require(host.Samples == 1, "resampled during cooldown");
    host.Trigger = TacticalTrigger.Damaged;
    loop.Tick(rec, host, 120, 1);
    Require(host.Samples == 1, "ignored minimum commitment");
    loop.Tick(rec, host, 190, 1);
    Require(host.Samples == 2, "no reaction to damage after commitment");
    await Task.CompletedTask;
});

await Check("tactical loop: blocked pawn is never sampled or ordered", async () =>
{
    var loop = new OperativeTacticalLoop(new OperativePlanner(), null, new AdaptiveHostilesSettings());
    var host = new FakeHost { Block = PawnBlock.Downed };
    loop.Tick(new OperativeRecord("op", "Pirate"), host, 100, 1);
    Require(host.Samples == 0 && host.Applied.Count == 0, "downed pawn was driven");
    await Task.CompletedTask;
});

await Check("tactical loop: remote answer arrives off-thread and is revalidated on take", async () =>
{
    var settings = new AdaptiveHostilesSettings();
    var gate = new TaskCompletionSource();
    var client = new GatedClient(gate.Task, """
        {"answers":{"movement":{"type":"choice","choice":"hold"},"target":{"type":"choice","choice":"none"},"stance":{"type":"choice","choice":"aggressive"}}}
        """);
    using var scheduler = new JevScheduler<OperativeDecision>(settings, d => d.IsModelDecision);
    var loop = new OperativeTacticalLoop(new OperativePlanner(client), scheduler, settings);
    var rec = new OperativeRecord("op", "Pirate");
    var host = new FakeHost();
    loop.Tick(rec, host, 100, 1);
    Require(loop.HasPending("op") && host.Applied.Count == 0, "request not pending without blocking");
    loop.Tick(rec, host, 110, 1);
    await WaitFor(() => client.Calls == 1);
    Require(client.Calls == 1 && host.Samples == 1, "duplicate in-flight request");
    gate.SetResult();
    await WaitFor(() => { loop.Tick(rec, host, 120, 1); return host.Applied.Count > 0; });
    Require(host.Applied[0].Kind == OrderKind.Hold && host.Applied[0].Stance == TacticalStance.Aggressive, "model answer not applied");
    Require(!loop.HasPending("op"), "pending not cleared");
    await Task.CompletedTask;
});

await Check("tactical loop: map changed during latency falls back to vanilla", async () =>
{
    var settings = new AdaptiveHostilesSettings();
    var gate = new TaskCompletionSource();
    var client = new GatedClient(gate.Task, """
        {"answers":{"movement":{"type":"choice","choice":"cell_142_87"},"target":{"type":"choice","choice":"none"},"stance":{"type":"choice","choice":"defensive"}}}
        """);
    using var scheduler = new JevScheduler<OperativeDecision>(settings, d => d.IsModelDecision);
    var loop = new OperativeTacticalLoop(new OperativePlanner(client), scheduler, settings);
    var host = new FakeHost();
    var rec = new OperativeRecord("op", "Pirate");
    loop.Tick(rec, host, 100, 1);
    host.Map = new GridMap { Open = false };
    gate.SetResult();
    await WaitFor(() => { loop.Tick(rec, host, 120, 1); return host.Applied.Count > 0; });
    Require(host.Applied[0].Kind == OrderKind.VanillaFallback, "stale destination issued");
    await Task.CompletedTask;
});

await Check("tactical loop: lost request times out and replans", async () =>
{
    var settings = new AdaptiveHostilesSettings { NetworkDeadline = TimeSpan.FromMilliseconds(20) };
    using var scheduler = new JevScheduler<OperativeDecision>(settings, d => d.IsModelDecision);
    var loop = new OperativeTacticalLoop(new OperativePlanner(new GatedClient(new TaskCompletionSource().Task, "{}")), scheduler, settings);
    var host = new FakeHost();
    var rec = new OperativeRecord("op", "Pirate");
    loop.Tick(rec, host, 100, 1);
    await Task.Delay(100);
    loop.Tick(rec, host, 100 + 130, 1);
    Require(!loop.HasPending("op") && rec.NextDecisionTick > 230, "timeout not released");
    await Task.CompletedTask;
});


await Check("operative fallback is aggressive, takes cover, and withdraws only when badly hurt", async () =>
{
    var healthy = await new OperativePlanner().DecideAsync(Battlefield());
    Require(healthy.Stance == TacticalStance.Aggressive && healthy.DestinationId == "cover_142_87", "healthy operative not aggressive");
    var hurt = await new OperativePlanner().DecideAsync(Battlefield() with { HealthFraction = 0.2f });
    Require(hurt.Stance == TacticalStance.Withdraw, "hurt operative kept charging");
    Require(TacticalHeuristics.StanceFor(0.1f, canFlee: false, lossFraction: 1) == TacticalStance.Withdraw, "operative may flee");
    Require(TacticalHeuristics.StanceFor(0.1f, canFlee: true, lossFraction: 0.1) == TacticalStance.Withdraw, "fled with squad winning");
    Require(TacticalHeuristics.StanceFor(0.1f, canFlee: true, lossFraction: 0.6) == TacticalStance.Flee, "no flee when losing");
    await Task.CompletedTask;
});

await Check("operative can never be ordered to flee, raiders can", async () =>
{
    var snap = Battlefield();
    var open = new GridMap { Open = true };
    var flee = new OperativeDecision("hold", null, TacticalStance.Flee, true);
    var op = OperativeOrderResolver.Resolve(flee, snap, open, PawnBlock.None, canFlee: false);
    Require(op.Kind != OrderKind.Flee && op.Stance == TacticalStance.Withdraw, "operative fled");
    var raider = OperativeOrderResolver.Resolve(flee, snap, open, PawnBlock.None, canFlee: true);
    Require(raider.Kind == OrderKind.Flee, "raider flee dropped");
    Require(OperativeOrderResolver.Resolve(flee, snap, open, PawnBlock.Downed).Kind == OrderKind.None, "downed pawn fled");
    await Task.CompletedTask;
});

await Check("squad: one model call returns coordinated per-member orders", async () =>
{
    var client = new FakeClient("""
        {"answers":{"tactic":{"type":"choice","choice":"flank"},"focus":{"type":"choice","choice":"pawn_1"},
        "move_0":{"type":"choice","choice":"cell_a"},"stance_0":{"type":"choice","choice":"aggressive"},
        "move_1":{"type":"choice","choice":"cell_b"},"stance_1":{"type":"choice","choice":"flee"}}}
        """);
    var d = await new SquadPlanner(client).DecideAsync(Squad(raiderHealth: 0.1f, sent: 4, down: 3));
    Require(d.IsModelDecision && d.Tactic == SquadTactic.Flank && d.FocusTargetId == "pawn_1", "squad plan lost");
    Require(d.Members["op"].DestinationId == "cell_a" && d.Members["op"].TargetId == "pawn_1", "operative order wrong");
    Require(d.Members["r1"].DestinationId == "cell_b" && d.Members["r1"].Stance == TacticalStance.Flee, "raider flee ignored");
    Require(client.Calls == 1, "more than one call for the squad");
    Require(!JsonSerializer.Serialize(client.LastQuestions!["stance_0"]).Contains("\"flee\""), "operative offered flee");
    Require(JsonSerializer.Serialize(client.LastQuestions!["stance_1"]).Contains("\"flee\""), "raider not offered flee");
    await Task.CompletedTask;
});

await Check("squad: operative flee answer, bad cells and shared cells fall back per member", async () =>
{
    var client = new FakeClient("""
        {"answers":{"tactic":{"type":"choice","choice":"focus_fire"},"focus":{"type":"choice","choice":"pawn_1"},
        "move_0":{"type":"choice","choice":"cell_a"},"stance_0":{"type":"choice","choice":"flee"},
        "move_1":{"type":"choice","choice":"cell_a"},"stance_1":{"type":"choice","choice":"aggressive"}}}
        """);
    var d = await new SquadPlanner(client).DecideAsync(Squad());
    Require(d.IsModelDecision, "valid tactic rejected");
    Require(!d.Members["op"].IsModelDecision && d.Members["op"].Stance != TacticalStance.Flee, "operative flee accepted");
    Require(d.Members["r1"].DestinationId != d.Members["op"].DestinationId, "two members sent to the same cell");
    var bad = await new SquadPlanner(new FakeClient("""
        {"answers":{"tactic":{"type":"choice","choice":"nuke"},"focus":{"type":"choice","choice":"pawn_1"}}}
        """)).DecideAsync(Squad());
    Require(!bad.IsModelDecision && bad.Members.Count == 2, "bad tactic accepted");
    await Task.CompletedTask;
});

await Check("squad: offline fallback is aggressive, spread, and only wounded raiders flee when losing", async () =>
{
    var d = await new SquadPlanner().DecideAsync(Squad());
    Require(!d.IsModelDecision && d.Members.Values.All(m => m.Stance == TacticalStance.Aggressive && m.TargetId == "pawn_1"), "fallback not aggressive focus fire");
    Require(d.Members["op"].DestinationId != d.Members["r1"].DestinationId, "members stacked on one cell");
    var losing = Squad(raiderHealth: 0.1f, sent: 6, down: 4);
    var l = await new SquadPlanner().DecideAsync(losing);
    Require(l.Members["r1"].Stance == TacticalStance.Flee, "losing wounded raider kept fighting");
    Require(l.Members["op"].Stance != TacticalStance.Flee, "operative fled");
    var cap = new SquadSnapshot("s", Enumerable.Range(0, 30).Select(i => Member($"r{i}", false, 1f)).ToList(), 30, 0, 0);
    Require((await new SquadPlanner().DecideAsync(cap)).Members.Count == SquadPlanner.MaxMembers, "squad call unbounded");
    await Task.CompletedTask;
});

await Check("squad: model withdraw/flee for healthy raiders is kept aggressive", async () =>
{
    var client = new FakeClient("""
        {"answers":{"tactic":{"type":"choice","choice":"regroup"},"focus":{"type":"choice","choice":"pawn_1"},
        "move_0":{"type":"choice","choice":"cell_a"},"stance_0":{"type":"choice","choice":"withdraw"},
        "move_1":{"type":"choice","choice":"cell_b"},"stance_1":{"type":"choice","choice":"flee"}}}
        """);
    var d = await new SquadPlanner(client).DecideAsync(Squad());
    Require(d.Members["op"].IsModelDecision && d.Members["op"].Stance == TacticalStance.Aggressive, "healthy withdraw obeyed");
    Require(d.Members["r1"].Stance == TacticalStance.Aggressive, "healthy flee obeyed");
    var hurt = await new SquadPlanner(client).DecideAsync(Squad(raiderHealth: 0.3f));
    Require(hurt.Members["r1"].Stance == TacticalStance.Withdraw, "hurt raider flee not downgraded to withdraw");
    await Task.CompletedTask;
});

await Check("turret range is flagged, penalised, and shown to the model", async () =>
{
    var list = CandidateEnumerator.Enumerate(new TurretMap(), 50, 50, new[] { (60, 50) });
    Require(list.Any(c => c.InTurretRange) && list.Any(c => !c.InTurretRange), "turret flag missing");
    var covered = new MovementCandidate("a", 1, 1, 0.9f, 0f, 5, "firing_position", true, InTurretRange: true);
    var safer = new MovementCandidate("b", 2, 2, 0.6f, 0f, 5, "firing_position", true);
    Require(TacticalHeuristics.PickDestination(new[] { covered, safer }, TacticalStance.Aggressive, 0)!.Id == "b", "walked into turret fire");
    var client = new FakeClient("""
        {"answers":{"movement":{"type":"choice","choice":"hold"},"target":{"type":"choice","choice":"turret_1"},"stance":{"type":"choice","choice":"aggressive"}}}
        """);
    var snap = Battlefield() with
    {
        Destinations = new[] { covered },
        Targets = new[] { new TargetCandidate("turret_1", 140, 90, 0.9f, true, "turret", 30) }
    };
    var d = await new OperativePlanner(client).DecideAsync(snap);
    Require(d.TargetId == "turret_1", "turret not targetable");
    Require(JsonSerializer.Serialize(client.LastQuestions).Contains("HOSTILE TURRET"), "turret range not shown to model");
});

await Check("breach route: the first wall on the path is the breach, the stand cell is just outside it", () =>
{
    var path = Enumerable.Range(0, 20).Select(i => (i, 5)).ToList();
    var walls = new HashSet<(int, int)> { (8, 5), (12, 5) };
    var site = BreachRoute.Resolve(path, (x, z) => walls.Contains((x, z)), (x, z) => true);
    Require(site is { } s && s.HasWall && s.WallX == 8 && s.StandX == 7 && s.StandZ == 5, "breach is not the first wall on the path");
    return Task.CompletedTask;
});

await Check("breach route: unusable stand cells move back along the path; a wall at the start has none", () =>
{
    var path = Enumerable.Range(0, 20).Select(i => (i, 5)).ToList();
    var site = BreachRoute.Resolve(path, (x, z) => x == 8, (x, z) => x <= 5);
    Require(site is { } s && s.WallX == 8 && s.StandX == 5, "stand cell did not step back to a usable cell");
    Require(BreachRoute.Resolve(path, (x, z) => x == 0, (x, z) => true) is null, "a wall on the first cell has no stand cell");
    Require(BreachRoute.Resolve(new List<(int, int)>(), (x, z) => true, (x, z) => true) is null, "empty path gave a site");
    return Task.CompletedTask;
});

await Check("breach route: an open base has no wall and the stand cell is short of the defenders", () =>
{
    var path = Enumerable.Range(0, 20).Select(i => (i, 5)).ToList();
    var site = BreachRoute.Resolve(path, (x, z) => false, (x, z) => true);
    Require(site is { } s && !s.HasWall && s.StandX == 19 - BreachRoute.OpenSetback, "open approach cell wrong");
    var shortPath = new List<(int, int)> { (0, 0), (1, 0) };
    Require(BreachRoute.Resolve(shortPath, (x, z) => false, (x, z) => true) is { StandX: 0 }, "short open path did not clamp to the start");
    return Task.CompletedTask;
});

await Check("breach route: pick keeps the weakest, spaced apart, one per wall, ids in order", () =>
{
    var picked = BreachRoute.Pick(new[]
    {
        new BreachPoint("", 13, 10, 0.95f, 0, 0, 14, 10),
        new BreachPoint("", 10, 30, 0.9f, 0, 0, 11, 31),
        new BreachPoint("", 40, 40, 0.7f, 0, 0, 11, 31),   // same wall as the 0.9 site
        new BreachPoint("", 10, 10, 0.5f, 1, 0, 11, 10),   // too close to the 0.95 site
        new BreachPoint("", 40, 10, 0.4f, 2, 1),
    }, 3, 8);
    Require(picked.Select(p => p.Id).SequenceEqual(new[] { "B1", "B2", "B3" }), "ids not sequential");
    Require(picked[0].Weakness == 0.95f && picked[1].Weakness == 0.9f && picked[2].Weakness == 0.4f, "wrong picks or order");
    return Task.CompletedTask;
});

await Check("B1 danger memory: marks fade over a day, penalise nearby cells and breaches, and are listed", async () =>
{
    var mem = new DangerMemory();
    mem.Record(50, 50, 1000);
    Require(mem.Penalty(50, 50, 1000) > 0.9f, "fresh mark at the cell has no penalty");
    Require(mem.Penalty(53, 50, 1000) > 0f && mem.Penalty(53, 50, 1000) < mem.Penalty(50, 50, 1000), "penalty does not fall with distance");
    Require(mem.Penalty(80, 50, 1000) == 0f, "far cell penalised");
    Require(mem.Penalty(50, 50, 1000 + DangerMemory.DecayTicks / 2) is > 0.4f and < 0.6f, "mark did not half-fade after half a day");
    Require(mem.Penalty(50, 50, 1000 + DangerMemory.DecayTicks) == 0f, "mark outlived a day");
    mem.Record(52, 51, 1500);
    Require(mem.Hotspots(2000, 5).Count == 1, "nearby marks not merged into one zone");
    Require(mem.Hotspots(1000 + DangerMemory.DecayTicks * 2, 5).Count == 0, "expired marks listed");

    var safe = new MovementCandidate("safe", 0, 0, 0.3f, 0.3f, 10, "cover", true);
    var deadly = new MovementCandidate("deadly", 1, 0, 0.5f, 0.3f, 10, "cover", true, Danger: 1f);
    Require(TacticalHeuristics.PickDestination(new[] { safe, deadly }, TacticalStance.Aggressive, 0)?.Id == "safe", "walked into a danger zone");
    Require(BreachRoute.Weakness(0, 0, 1f) < BreachRoute.Weakness(0, 0, 0f), "danger did not lower breach weakness");
    Require(BreachRoute.Weakness(0, 0, 0f) == 1f, "danger-free open breach is not fully weak");

    var squad = BreachSquad() with
    {
        DangerZones = new[] { new DangerZone(150, 90, 0.8f) },
        BreachPoints = new[] { new BreachPoint("B1", 150, 90, 0.3f, 0, 0, Danger: 0.9f), new BreachPoint("B2", 160, 90, 0.9f, 0, 0) }
    };
    var client = new FakeClient("""
        {"answers":{"tactic":{"type":"choice","choice":"flank"},"focus":{"type":"choice","choice":"none"},"breach":{"type":"choice","choice":"B2"},
        "move_0":{"type":"choice","choice":"hold"},"stance_0":{"type":"choice","choice":"aggressive"},"move_1":{"type":"choice","choice":"hold"},"stance_1":{"type":"choice","choice":"aggressive"}}}
        """);
    await new SquadPlanner(client).DecideAsync(squad);
    Require(JsonSerializer.Serialize(client.LastState).Contains("dangerZones"), "danger zones not listed to the model");
    Require(JsonSerializer.Serialize(client.LastQuestions!["breach"]).Contains("recently fell"), "danger missing from the breach options");
});

await Check("B2 stall: idle cycles set the breach aside; all stalled means ranged open a second wall", () =>
{
    var t = new StallTracker();
    var keys = new[] { "1,1", "2,2" };
    Require(!t.Observe("1,1", 0, 30, keys), "first look stalled");
    bool Idle(string k, int n, double dist) { var any = false; for (var i = 0; i < n; i++) any |= t.Observe(k, 0, dist, keys); return any; }
    Require(!Idle("1,1", StallCycles - 1, 30), "stalled too early");
    Require(t.Observe("1,1", 0, 30, keys), "idle cycles did not stall");
    Require(t.IsExcluded("1,1") && !t.RangedOpensWall, "stalled breach not set aside");
    Require(!t.Observe("2,2", 0, 30, keys), "new breach inherited the idle count");
    Require(!t.Observe("2,2", 0.2, 30, keys) && !t.Observe("2,2", 0, 20, keys), "progress counted as idle");
    Require(!Idle("2,2", StallCycles - 1, 20), "idle count did not restart after progress");
    Require(t.Observe("2,2", 0, 20, keys) && t.RangedOpensWall, "second stall did not open the second wall");
    Require(!t.IsExcluded("1,1"), "exclusions kept after every point stalled");
    var pts = new[] { new BreachPoint("B1", 1, 1, 1f, 0, 0), new BreachPoint("B2", 2, 2, 0.5f, 0, 0) };
    var t2 = new StallTracker();
    for (var i = 0; i <= StallCycles; i++) t2.Observe("1,1", 0, 30, new[] { "1,1", "2,2" });
    Require(t2.Available(pts, b => $"{b.X},{b.Z}").Select(b => b.Id).SequenceEqual(new[] { "B2" }), "stalled point still offered");
    return Task.CompletedTask;
});

await Check("B2 squad loop: a stalled assault is called out in the log and ranged told to open a second wall", () =>
{
    var loop = new SquadLoop(new SquadPlanner(), null, new AdaptiveHostilesSettings());
    var host = new FakeSquadHost { Snapshot = StagedSquad(arrived: true) };
    for (var i = 0; i < StallCycles * 2 + 4; i++) loop.Tick("s", host, 1000 + i * 1000, 1);
    Require(host.Callouts.Any(c => c.Contains("stalled")), "no stall callout");
    Require(host.Callouts.Any(c => c.Contains("second wall")), "ranged not told to open a second wall");
    return Task.CompletedTask;
});

await Check("B3 split: 6+ raiders against a thin defence send one melee and one ranged raider to another breach, never the Operative", () =>
{
    SquadMember M(string id, bool op, bool melee) => new(new OperativeSnapshot(id, 100, 90, 1f, "x", 1, new List<MovementCandidate>(), new List<TargetCandidate>()), op, melee);
    var six = new[] { M("op", true, false), M("m1", false, true), M("m2", false, true), M("r1", false, false), M("r2", false, false), M("r3", false, false) };
    var pts = new[] { new BreachPoint("B1", 1, 1, 0.9f, 0, 0), new BreachPoint("B2", 20, 20, 0.6f, 1, 0), new BreachPoint("B3", 40, 40, 0.2f, 3, 1) };
    var pick = SquadSplit.Choose(six, pts, "B1", defenders: 2);
    Require(pick is { } p && p.BreachId == "B2" && p.PawnIds.Count == 2 && !p.PawnIds.Contains("op"), "wrong split");
    Require(pick!.Value.PawnIds.Contains("m2") && pick.Value.PawnIds.Contains("r3"), "group is not one melee and one ranged");
    Require(SquadSplit.Choose(six.Take(5).ToList(), pts, "B1", 2) is null, "split with fewer than 6 raiders");
    Require(SquadSplit.Choose(six, pts, "B1", 7) is null, "split against a defence larger than the squad");
    Require(SquadSplit.GroupSizeFor(6) == 2 && SquadSplit.GroupSizeFor(12) == 3 && SquadSplit.GroupSizeFor(40) == 4, "flank group size");
    Require(SquadSplit.Choose(six, pts, "B1", 6) is not null, "no flank against a defence as large as the squad");
    Require(SquadSplit.Choose(six, pts.Take(1).ToList(), "B1", 2) is null, "split with one breach point");

    var loop = new SquadLoop(new SquadPlanner(), null, new AdaptiveHostilesSettings());
    MovementCandidate C(string id, string purpose, int x) => new(id, x, 90, 0.5f, 0.2f, 10, purpose, true);
    SquadMember Member(string id, bool op, bool melee, int x) => new(new OperativeSnapshot(id, x, 90, 1f, "x", 1,
        new[] { C($"{id}_s1", "stage_B1", 100), C($"{id}_s2", "stage_B2", 100),
                C($"{id}_p1", melee ? "breach_B1" : "support_B1", 150), C($"{id}_p2", melee ? "breach_B2" : "support_B2", 160) },
        new[] { new TargetCandidate("pawn_1", 145, 86, 0.8f, true) }), op, melee);
    var members = new[] { Member("op", true, false, 100), Member("m1", false, true, 100), Member("m2", false, true, 100),
                          Member("r1", false, false, 100), Member("r2", false, false, 100), Member("r3", false, false, 100) };
    var snap = new SquadSnapshot("s", members, 6, 0, 0, pts.Take(2).ToList(), Defenders: 2);
    var host = new FakeSquadHost { Snapshot = snap };
    for (var i = 0; i < 3; i++) loop.Tick("s", host, 1000 + i * 1000, 1);
    Require(host.Callouts.Count(c => c.Contains("split off")) == 1, "split not announced exactly once");
    Require(host.Applied.Any(a => a.Id == "m2" && a.Order.Reason.EndsWith("B2")), "split melee not sent to the second breach");
    Require(host.Applied.Any(a => a.Id == "m1" && a.Order.Reason.EndsWith("B1")), "main group left its breach");
    return Task.CompletedTask;
});

await Check("B4 target cap: at most 3 raiders on one target, the rest take their next best", async () =>
{
    MovementCandidate C(string id, string purpose) => new(id, 10, 10, 0.5f, 0.2f, 10, purpose, true);
    SquadMember M(string id, bool op) => new(new OperativeSnapshot(id, 100, 90, 1f, "x", 1, new[] { C("c_" + id, "cover") },
        new[] { new TargetCandidate("pawn_1", 145, 86, 0.9f, true), new TargetCandidate("pawn_2", 146, 86, 0.5f, true) }), op, false);
    var squad = new SquadSnapshot("s", new[] { M("op", true), M("a", false), M("b", false), M("c", false), M("d", false) }, 5, 0, 0);
    var d = await new SquadPlanner().DecideAsync(squad);
    var counts = d.Members.Values.Where(x => x.TargetId is not null).GroupBy(x => x.TargetId!).ToDictionary(g => g.Key, g => g.Count());
    Require(counts["pawn_1"] == SquadPlanner.MaxPerTarget && counts["pawn_2"] == 2, "targets not capped and redistributed");
    var single = new SquadSnapshot("s", squad.Members.Select(m => m with { Snapshot = m.Snapshot with { Targets = new[] { new TargetCandidate("pawn_1", 1, 1, 0.9f, true) } } }).ToList(), 5, 0, 0);
    var s = await new SquadPlanner().DecideAsync(single);
    Require(s.Members.Values.Count(x => x.TargetId == "pawn_1") == 3 && s.Members.Values.Count(x => x.TargetId is null) == 2, "extra raiders kept the full target");
});

await Check("C5 intent log: one mod-log callout per phase change, never repeated", () =>
{
    var loop = new SquadLoop(new SquadPlanner(), null, new AdaptiveHostilesSettings());
    var host = new FakeSquadHost { Snapshot = StagedSquad(arrived: false) };
    loop.Tick("s", host, 100, 1);
    loop.Tick("s", host, 1000, 1);
    var gather = host.Callouts.Where(c => !c.StartsWith("sync:") && !c.StartsWith("flank")).ToList();
    Require(gather.Count == 1 && gather[0].Contains("gathering") && gather[0].Contains("breach B1"), "gather callout missing or repeated");
    host.Snapshot = StagedSquad(arrived: true);
    loop.Tick("s", host, 2000, 1);
    loop.Tick("s", host, 3000, 1);
    var intents = host.Callouts.Where(c => !c.StartsWith("sync:") && !c.StartsWith("flank")).ToList();
    Require(intents.Count == 2 && intents[1].Contains("melee pushing") && host.Callouts.Any(c => c.StartsWith("sync:")), "breach callout missing or repeated");
    Require(IntentLog.Describe(SquadPhase.Press, new SquadDecision(SquadTactic.FocusFire, null, new Dictionary<string, OperativeDecision>(), false),
        host.Snapshot).Contains("no breach point"), "no-breach intent unclear");
    return Task.CompletedTask;
});

await Check("breach cells: role cells stay on the raiders' side of the wall and ranged support stands close enough to shoot it", () =>
{
    // wall at (130,136), stand cell (130,137): outside is z > 136
    Require(BreachRoute.OutsideWall(130, 136, 130, 137, 128, 140), "outside cell rejected");
    Require(BreachRoute.OutsideWall(130, 136, 130, 137, 133, 137), "stand row rejected");
    Require(!BreachRoute.OutsideWall(130, 136, 130, 137, 130, 135), "cell inside the base accepted");
    Require(!BreachRoute.OutsideWall(130, 136, 130, 137, 126, 136), "cell level with the wall accepted");
    Require(!BreachRoute.OutsideWall(130, 136, 130, 137, 118, 127), "far interior cell accepted");
    Require(BreachRoute.SupportDistance(30f) <= 6f && BreachRoute.SupportDistance(30f) >= 3f, "support distance not within a few cells");
    Require(BreachRoute.SupportDistance(12f) < 12f, "support distance not within weapon range of the wall");
    return Task.CompletedTask;
});

await Check("turrets: ranged raiders focus a turret over colonists, melee keep their usual target, and the cap of 3 holds", async () =>
{
    var targets = new[]
    {
        new TargetCandidate("pawn_c", 145, 86, 0.9f, true),
        new TargetCandidate("pawn_t", 146, 86, 0.7f, true, "turret", 20f)
    };
    Require(TacticalHeuristics.PickTarget(targets, null) == "pawn_c", "default target changed");
    Require(TacticalHeuristics.PickTarget(targets, null, preferTurrets: true) == "pawn_t", "ranged did not prefer the turret");
    Require(TacticalHeuristics.PickTarget(targets.Take(1).ToList(), null, preferTurrets: true) == "pawn_c", "no turret should fall back to colonists");

    MovementCandidate C(string id, string purpose, int x) => new(id, x, 90, 0.5f, 0.2f, 10, purpose, true);
    SquadMember M(string id, bool melee) => new(new OperativeSnapshot(id, 100, 90, 1f, "x", 1,
        new[] { C($"{id}_s", "stage_B1", 100), C($"{id}_p", melee ? "breach_B1" : "support_B1", 150) }, targets), false, melee);
    var members = new[] { M("m1", true), M("r1", false), M("r2", false), M("r3", false), M("r4", false) };
    var snap = new SquadSnapshot("s", members, 5, 0, 0, new[] { new BreachPoint("B1", 150, 90, 0.5f, 1, 2) }, SquadPhase.Breach);
    var d = await new SquadPlanner().DecideAsync(snap);
    Require(d.FocusTargetId == "pawn_t", "squad did not focus the turret that covers the breach");
    Require(d.Members["m1"].TargetId == "pawn_t", "capped target missing for the first raider");
    var onTurret = new[] { "m1", "r1", "r2", "r3", "r4" }.Count(id => d.Members[id].TargetId == "pawn_t");
    Require(onTurret == SquadPlanner.MaxPerTarget, $"{onTurret} raiders on one turret");
    var noTurretCover = snap with { BreachPoints = new[] { new BreachPoint("B1", 150, 90, 0.5f, 1, 0) } };
    var d2 = await new SquadPlanner().DecideAsync(noTurretCover);
    Require(d2.FocusTargetId == "pawn_c", "focused a turret that does not cover the breach");
    Require(d2.Members["r1"].TargetId == "pawn_t", "ranged still prefer turrets");
});

await Check("enemies: a non-colony hostile is offered as an enemy target and engaged ahead of colonists, turrets still first for ranged", async () =>
{
    var targets = new[]
    {
        new TargetCandidate("pawn_c", 145, 86, 0.9f, true),
        new TargetCandidate("pawn_mech", 140, 86, 1.1f, true, "enemy"),
        new TargetCandidate("pawn_t", 146, 86, 0.7f, true, "turret", 20f)
    };
    Require(TacticalHeuristics.PickTarget(targets.Take(2).ToList(), null) == "pawn_mech", "enemy not engaged first");
    Require(TacticalHeuristics.PickTarget(targets, null, preferTurrets: true) == "pawn_t", "turret not preferred by ranged");
    var client = new FakeClient("""{"tactic":"focus_fire","focus":"pawn_mech","phase":"breach","breach":"B1","stance_0":"aggressive","move_0":"hold"}""");
    var d = await new SquadPlanner(client).DecideAsync(new SquadSnapshot("s", new[]
    {
        new SquadMember(new OperativeSnapshot("m1", 100, 90, 1f, "x", 1, new List<MovementCandidate>(), targets), false, true)
    }, 1, 0, 0));
    var text = System.Text.Json.JsonSerializer.Serialize(client.LastQuestions);
    Require(text.Contains("non-colony enemy"), "enemy not described to the model");
    Require(d.FocusTargetId == "pawn_mech", "model focus on an enemy rejected");
});

await Check("corner ambush: ranged support stands beside the opening hugging the wall, not straight behind it, and prefers unseen wall-hugging cells", () =>
{
    // wall at (130,136), raiders' side is z > 136, stand cell (130,137)
    var a0 = BreachRoute.SupportAnchor(130, 137, 130, 136, 30f, 0, true);
    var a1 = BreachRoute.SupportAnchor(130, 137, 130, 136, 30f, 1, true);
    Require(a0.X != 130 && a1.X != 130 && a0.X != a1.X, "support is in line with the opening or on one side only");
    Require(a0.Z > 136 && a1.Z > 136, "support anchor is not outside the wall");
    Require(Math.Abs(a0.X - 130) <= 3 && Math.Abs(a0.Z - 137) <= 3, "support is not within a few cells of the breach");
    var open = BreachRoute.SupportAnchor(150, 90, 120, 90, 30f, 0, false);
    Require(open.X < 150 + BreachRoute.SupportDistance(30f) + 1 && open.X > 150 && open.Z != 90, "open breach support not back and to the side");
    Require(BreachRoute.CellScore(0f, false, 2) < BreachRoute.CellScore(0.6f, true, 0), "an exposed cell beat an unseen one");
    Require(BreachRoute.CellScore(0.2f, true, 1) < BreachRoute.CellScore(0.2f, false, 1), "hugging a wall gave no bonus");
    Require(BreachRoute.CellScore(0.2f, false, 0) < BreachRoute.CellScore(0.2f, false, 3), "a farther cell beat a nearer equal one");
    return Task.CompletedTask;
});

await Check("press through: melee at an open breach go on to distinct press cells instead of holding at the gap, and keep going", async () =>
{
    MovementCandidate C(string id, string purpose, int x, int z) => new(id, x, z, 0.5f, 0.2f, 10, purpose, true);
    var tgt = new[] { new TargetCandidate("pawn_1", 150, 100, 0.8f, true) };
    SquadMember M(string id, int x, int z, int pressX, float hp = 1f) => new(new OperativeSnapshot(id, x, z, hp, "x", 1,
        new[] { C($"{id}_s", "stage_B1", 100, 90), C($"{id}_b", "breach_B1", 130, 90), C($"{id}_p", "press_B1", pressX, 90) }, tgt), false, true);
    SquadSnapshot Snap(params SquadMember[] ms) => new("s", ms, ms.Length, 0, 0, new[] { new BreachPoint("B1", 130, 90, 0.9f, 0, 0) },
        SquadPhase.Breach);
    // two melee standing at the gap, one still far away
    var d = await new SquadPlanner().DecideAsync(Snap(M("m1", 130, 90, 134), M("m2", 129, 91, 135), M("m3", 100, 90, 136)));
    Require(d.Members["m1"].DestinationId == "m1_p" && d.Members["m2"].DestinationId == "m2_p", "melee at the gap did not press on");
    Require(d.Members["m3"].DestinationId == "m3_b", "a far melee should still come to the breach");
    Require(d.Members["m1"].DestinationId != d.Members["m2"].DestinationId, "followers share one cell");
    // already at the press cell, 4 cells from the breach: stays pressing, does not walk back to the gap
    var d2 = await new SquadPlanner().DecideAsync(Snap(M("m1", 134, 90, 134)));
    Require(d2.Members["m1"].DestinationId == "m1_p", "melee walked back from its press cell to the gap");
    // a walled breach offers no press cell: melee keep their breach cell
    var wall = Snap(new SquadMember(new OperativeSnapshot("m4", 130, 90, 1f, "x", 1,
        new[] { C("m4_s", "stage_B1", 100, 90), C("m4_b", "breach_B1", 130, 90) }, tgt), false, true));
    Require((await new SquadPlanner().DecideAsync(wall)).Members["m4"].DestinationId == "m4_b", "walled breach changed");
    // a wounded melee still withdraws instead of pressing
    Require((await new SquadPlanner().DecideAsync(Snap(M("m5", 130, 90, 134, 0.2f)))).Members["m5"].Stance == TacticalStance.Withdraw, "hurt melee pressed on");
});

await Check("commitment: a squad that is not losing presses on, a losing one may pull back, a cornered wounded raider fights", () =>
{
    MovementCandidate C(string id, string purpose, int x) => new(id, x, 90, 0.5f, 0.2f, 10, purpose, true);
    SquadMember M(string id, float hp, params TargetCandidate[] t) => new(new OperativeSnapshot(id, 120, 90, hp, "x", 1,
        new[] { C("away", "retreat", 60), C("push", "press_B1", 135), C("stage", "stage_B1", 100) }, t), false, true);
    var far = new TargetCandidate("c1", 150, 90, 0.8f, true);
    var near = new TargetCandidate("c2", 123, 90, 0.8f, true);
    SquadDecision Order(params SquadMember[] ms) => new(SquadTactic.Regroup, null, ms.ToDictionary(m => m.Snapshot.PawnId,
        m => new OperativeDecision("away", null, TacticalStance.Aggressive, true)), true, "B1", SquadPhase.Breach);
    SquadSnapshot Snap(int sent, int down, params SquadMember[] ms) => new("s", ms, sent, down, 0, new[] { new BreachPoint("B1", 130, 90, 0.9f, 0, 0) }, SquadPhase.Breach);

    var strong = Snap(4, 1, M("a", 1f, far), M("b", 1f, far), M("c", 1f, far));
    var pressed = SquadCommitment.Apply(Order(strong.Members.ToArray()), strong);
    Require(pressed.Members.Values.All(d => d.DestinationId == "push" && d.Stance == TacticalStance.Aggressive), "healthy raiders walked away from the fight");
    Require(pressed.Tactic == SquadTactic.SuppressAndPush, "a regroup plan survived while winning");

    var losing = Snap(8, 5, M("a", 1f, far, near, near, near), M("b", 0.3f, far));
    Require(SquadCommitment.Losing(losing), "a wrecked squad was not called losing");
    Require(SquadCommitment.Apply(Order(losing.Members.ToArray()), losing).Members.Values.All(d => d.DestinationId == "away"), "a losing squad was forced forward");

    var wounded = Snap(4, 1, M("w", 0.2f, near), M("x", 0.2f, far));
    var order = Order(wounded.Members.ToArray());
    order = order with { Members = order.Members.ToDictionary(k => k.Key, k => k.Value with { Stance = TacticalStance.Withdraw }) };
    var d2 = SquadCommitment.Apply(order, wounded).Members;
    Require(d2["w"].Stance == TacticalStance.Aggressive && d2["w"].DestinationId == "push", "a cornered wounded raider ran instead of fighting");
    Require(d2["x"].Stance == TacticalStance.Withdraw, "a wounded raider away from the fight was kept in it");
    return Task.CompletedTask;
});

await Check("withdraw: a hurt raider only goes to safe cells, never the breach or support cell, and holds when there is none", () =>
{
    MovementCandidate C(string id, string purpose) => new(id, 1, 1, 0.5f, 0.2f, 10, purpose, true);
    var attackOnly = new[] { C("push", "breach_B1"), C("sup", "support_B1"), C("adv", "advance") };
    Require(TacticalHeuristics.PickDestination(attackOnly, TacticalStance.Withdraw, 0, "B1", true) is null, "withdraw picked an attack cell");
    var withStage = attackOnly.Append(C("stage", "stage_B1")).ToArray();
    Require(TacticalHeuristics.PickDestination(withStage, TacticalStance.Withdraw, 0, "B1", true)?.Id == "stage", "withdraw did not fall back to the staging cell");
    var withRetreat = withStage.Append(C("away", "retreat")).ToArray();
    Require(TacticalHeuristics.PickDestination(withRetreat, TacticalStance.Withdraw, 0, "B1", true)?.Id == "away", "retreat cell not preferred");
    var d = TacticalHeuristics.Decide(attackOnly, new List<TargetCandidate>(), 0.25f, 0, breachId: "B1", isMelee: true);
    Require(d.Stance == TacticalStance.Withdraw && d.DestinationId == "hold", "hurt melee with no safe cell should hold");
    return Task.CompletedTask;
});

await Check("withdraw latch: a raider that withdrew keeps withdrawing until it is back above half health", () =>
{
    MovementCandidate C(string id, string purpose, int x) => new(id, x, 90, 0.5f, 0.2f, 10, purpose, true);
    SquadSnapshot Snap(float hp) => new("s", new[]
    {
        new SquadMember(new OperativeSnapshot("m1", 130, 90, hp, "x", 2,
            new[] { C("push", "breach_B1", 150), C("away", "retreat", 110), C("stage", "stage_B1", 100) },
            new[] { new TargetCandidate("pawn_1", 145, 86, 0.8f, true) }), false, true)
    }, 1, 0, 0, new[] { new BreachPoint("B1", 150, 90, 0.9f, 0, 0) });
    var loop = new SquadLoop(new SquadPlanner(), null, new AdaptiveHostilesSettings());
    var host = new FakeSquadHost { Snapshot = Snap(0.2f) };
    loop.Tick("s", host, 1000, 1);
    host.Snapshot = Snap(0.42f); // above the 0.35 wounded line, below the 0.5 recovery line
    loop.Tick("s", host, 2000, 1);
    loop.Tick("s", host, 3000, 1);
    Require(host.Applied.Count >= 2, "no orders");
    Require(host.Applied.All(a => a.Order.Stance == TacticalStance.Withdraw && !a.Order.Reason.StartsWith("breach_")), "wounded raider went back to the breach");
    host.Snapshot = Snap(0.6f);
    loop.Tick("s", host, 4000, 1);
    loop.Tick("s", host, 5000, 1);
    Require(host.Applied.Last().Order.Stance != TacticalStance.Withdraw, "raider did not return after recovering");
    return Task.CompletedTask;
});

await Check("squad: melee push the weakest breach, ranged support behind it, in sync", async () =>
{
    var squad = BreachSquad();
    var offline = await new SquadPlanner().DecideAsync(squad);
    Require(offline.BreachId == "B2", "offline fallback did not pick the weakest breach");
    Require(offline.Members["m1"].DestinationId == "cell_b2_push" && offline.Members["r1"].DestinationId == "cell_b2_support", "roles not synced");

    var client = new FakeClient("""
        {"answers":{"tactic":{"type":"choice","choice":"suppress_push"},"focus":{"type":"choice","choice":"pawn_1"},"breach":{"type":"choice","choice":"B1"},
        "move_0":{"type":"choice","choice":"cell_b1_push"},"stance_0":{"type":"choice","choice":"aggressive"},
        "move_1":{"type":"choice","choice":"cell_b1_push"},"stance_1":{"type":"choice","choice":"aggressive"}}}
        """);
    var d = await new SquadPlanner(client).DecideAsync(squad);
    Require(d.IsModelDecision && d.BreachId == "B1", "model breach lost");
    Require(d.Members["m1"].DestinationId == "cell_b1_push" && d.Members["m1"].IsModelDecision, "melee push rejected");
    Require(d.Members["r1"].DestinationId != "cell_b1_push" && !d.Members["r1"].IsModelDecision, "ranged took the melee front");
    Require(JsonSerializer.Serialize(client.LastQuestions!["breach"]).Contains("weakness"), "breach options missing");

    var wrong = new FakeClient("""
        {"answers":{"tactic":{"type":"choice","choice":"flank"},"focus":{"type":"choice","choice":"pawn_1"},"breach":{"type":"choice","choice":"B1"},
        "move_0":{"type":"choice","choice":"cell_b2_push"},"stance_0":{"type":"choice","choice":"aggressive"},
        "move_1":{"type":"choice","choice":"cell_b1_support"},"stance_1":{"type":"choice","choice":"aggressive"}}}
        """);
    var w = await new SquadPlanner(wrong).DecideAsync(squad);
    Require(!w.Members["m1"].IsModelDecision, "melee sent to a different breach than the squad's");
    Require(w.Members["r1"].DestinationId == "cell_b1_support", "valid ranged support rejected");
    await Task.CompletedTask;
});

await Check("ranged: shoot point blank instead of kiting, and move up at an open breach when idle", async () =>
{
    MovementCandidate C(string id, string purpose, int x) => new(id, x, 90, 0.5f, 0.2f, 10, purpose, true);
    SquadSnapshot Snap(int rx, TargetCandidate[] tgt, bool walled)
    {
        var r = new OperativeSnapshot("r1", rx, 90, 1f, "x", 2, new[] { C("r_s", "support_B1", 130), C("r_b", "breach_B1", 140), C("r_p", "press_B1", 150), C("r_st", "stage_B1", 100) }, tgt);
        return new SquadSnapshot("s", new[] { new SquadMember(r, false, false) }, 1, 0, 0,
            new[] { new BreachPoint("B1", 140, 90, 0.9f, 0, 0, walled ? 139 : -1, walled ? 90 : -1) }, SquadPhase.Breach);
    }
    var far = new TargetCandidate("far", 160, 90, 0.9f, true);
    var near = new TargetCandidate("near", 132, 91, 0.3f, true);
    var d = await new SquadPlanner().DecideAsync(Snap(130, new[] { far, near }, false));
    Require(d.Members["r1"].DestinationId == "hold" && d.Members["r1"].TargetId == "near", "ranged kited or ignored the adjacent enemy");
    var idle = await new SquadPlanner().DecideAsync(Snap(130, new[] { new TargetCandidate("hidden", 160, 90, 0.9f, false) }, false));
    Require(idle.Members["r1"].DestinationId == "r_p", "ranged idled at its support cell at an open breach");
    var walled = await new SquadPlanner().DecideAsync(Snap(130, new[] { new TargetCandidate("hidden", 160, 90, 0.9f, false) }, true));
    Require(walled.Members["r1"].DestinationId == "r_s", "ranged left its support cell at a walled breach");
});

await Check("targets: a badly hurt enemy is finished before the focus target", async () =>
{
    var strong = new TargetCandidate("a", 10, 10, 0.9f, true);
    var hurt = new TargetCandidate("b", 12, 10, 0.4f, true, "pawn", 0f, 0.2f);
    Require(TacticalHeuristics.PickTarget(new[] { strong, hurt }, "a") == "b", "a nearly dead enemy was ignored");
    Require(TacticalHeuristics.PickTarget(new[] { strong, hurt with { Health = 0.9f } }, "a") == "a", "healthy enemy changed the focus");
    await Task.CompletedTask;
});

await Check("cadence: a one-second squad interval schedules the next call 60-75 ticks out with a short commitment", () =>
{
    var settings = new AdaptiveHostilesSettings { MinDecisionCooldownTicks = 60, MaxDecisionCooldownTicks = 75, MinCommitmentTicks = 40 };
    var record = new OperativeRecord("s", "squad");
    for (ulong seed = 1; seed <= 20; seed++)
    {
        OperativeRegistry.ScheduleNext(record, 1000, seed, settings);
        Require(record.NextDecisionTick is >= 1060 and <= 1075 && record.CommitUntilTick == 1040, "interval outside 60-75 ticks or commitment not 40");
    }
    Require(!OperativeRegistry.ShouldRequest(record, 1030, TacticalTrigger.Damaged, PawnBlock.None), "re-ordered inside the commitment");
    Require(OperativeRegistry.ShouldRequest(record, 1041, TacticalTrigger.Damaged, PawnBlock.None), "key event did not re-order after the commitment");
    return Task.CompletedTask;
});

await Check("press through: a melee raider past the opening keeps pressing instead of walking back to the breach cell", async () =>
{
    MovementCandidate C(string id, string purpose, int x) => new(id, x, 90, 0.5f, 0.2f, 10, purpose, true);
    var m = new SquadMember(new OperativeSnapshot("m1", 138, 90, 1f, "x", 1,
        new[] { C("m1_b", "breach_B1", 130), C("m1_p", "press_B1", 146), C("m1_s", "stage_B1", 100) },
        new[] { new TargetCandidate("hidden", 170, 90, 0.9f, false) }), false, true);
    var squad = new SquadSnapshot("s", new[] { m }, 1, 0, 0, new[] { new BreachPoint("B1", 130, 90, 0.9f, 0, 0) }, SquadPhase.Breach);
    Require((await new SquadPlanner().DecideAsync(squad)).Members["m1"].DestinationId == "m1_p", "melee walked back from beyond the opening");
});

await Check("cover hop: a raider far from its role cell advances through a cover cell, a near one goes straight", async () =>
{
    MovementCandidate C(string id, string purpose, int x) => new(id, x, 90, 0.5f, 0.2f, 10, purpose, true);
    SquadSnapshot Snap(int x) => new("s", new[] { new SquadMember(new OperativeSnapshot("r1", x, 90, 1f, "x", 2,
        new[] { C("r_s", "support_B1", 130), C("r_h", "hop_B1", x + 10), C("r_st", "stage_B1", 100) },
        new[] { new TargetCandidate("hidden", 160, 90, 0.9f, false) }), false, false) }, 1, 0, 0,
        new[] { new BreachPoint("B1", 140, 90, 0.9f, 0, 0, 139, 90) }, SquadPhase.Breach);
    Require((await new SquadPlanner().DecideAsync(Snap(100))).Members["r1"].DestinationId == "r_h", "far raider crossed open ground in one run");
    Require((await new SquadPlanner().DecideAsync(Snap(125))).Members["r1"].DestinationId == "r_s", "near raider took a pointless hop");
});

await Check("go signal: waits for every healthy member, a hurt one does not hold it", async () =>
{
    var squad = StagedSquad(arrived: true);
    var late = squad.Members[1] with { Snapshot = squad.Members[1].Snapshot with { X = 40 } };
    var three = squad with { Members = new[] { squad.Members[0], late } };
    Require((await new SquadPlanner().DecideAsync(three)).Phase == SquadPhase.Gather, "went before the last member staged");
    var hurt = late with { Snapshot = late.Snapshot with { HealthFraction = 0.2f } };
    Require((await new SquadPlanner().DecideAsync(squad with { Members = new[] { squad.Members[0], hurt } })).Phase == SquadPhase.Breach,
        "a withdrawing member held the squad");
});

await Check("squad: raiders gather at staging cells and only go together on the signal", async () =>
{
    var far = StagedSquad(arrived: false);
    var d = await new SquadPlanner().DecideAsync(far);
    Require(d.Phase == SquadPhase.Gather, "attacked before gathering");
    Require(d.Members["m1"].DestinationId == "cell_stage_m1" && d.Members["r1"].DestinationId == "cell_stage_r1", "not sent to staging cells");
    Require(d.Members.Values.All(m => m.Stance == TacticalStance.Defensive), "attacked while gathering");
    Require(d.Members["m1"].TargetId is null, "melee raider given a target to chase while gathering");

    var go = await new SquadPlanner().DecideAsync(StagedSquad(arrived: true));
    Require(go.Phase == SquadPhase.Breach && go.Members["m1"].DestinationId == "cell_push", "no go signal once staged");

    var impatient = await new SquadPlanner(new FakeClient("""
        {"answers":{"tactic":{"type":"choice","choice":"flank"},"focus":{"type":"choice","choice":"pawn_1"},"breach":{"type":"choice","choice":"B1"},"phase":{"type":"choice","choice":"press"},
        "move_0":{"type":"choice","choice":"cell_push"},"stance_0":{"type":"choice","choice":"aggressive"},
        "move_1":{"type":"choice","choice":"cell_support"},"stance_1":{"type":"choice","choice":"aggressive"}}}
        """)).DecideAsync(far);
    Require(impatient.Phase == SquadPhase.Gather && impatient.Members["m1"].DestinationId == "cell_stage_m1", "model overrode the gather gate");

    Require((await new SquadPlanner().DecideAsync(far with { GatherCycles = SquadPlanner.MaxGatherCycles })).Phase == SquadPhase.Breach, "gather never times out");
    Require((await new SquadPlanner().DecideAsync(far with { PreviousPhase = SquadPhase.Breach })).Phase == SquadPhase.Breach, "went back to gathering mid-assault");
});

await Check("squad loop: remembers the phase between cycles", async () =>
{
    var loop = new SquadLoop(new SquadPlanner(), null, new AdaptiveHostilesSettings());
    var host = new FakeSquadHost { Snapshot = StagedSquad(arrived: false) };
    loop.Tick("s", host, 100, 1);
    Require(host.Applied[0].Order.X == 100, "gather order not applied");
    host.Snapshot = StagedSquad(arrived: true);
    loop.Tick("s", host, 1000, 1);
    Require(host.Applied.Last().Order.X != 100 && host.Applied.Last().Id.Length > 0, "go signal not applied");
    await Task.CompletedTask;
});

await Check("squad: ranged support from the start of the breach phase; flee needs a lost raid and a nearly dead raider", async () =>
{
    var squad = StagedSquad(arrived: true) with { PreviousPhase = SquadPhase.Breach };
    var go = await new SquadPlanner().DecideAsync(squad);
    Require(go.Members["m1"].DestinationId == "cell_push", "melee not pushing");
    Require(go.Members["r1"].DestinationId == "cell_support", "ranged held back instead of supporting at once");
    Require(TacticalHeuristics.Guard(TacticalStance.Flee, 0.3f, 0.9) == TacticalStance.Withdraw, "hurt raider fled");
    Require(TacticalHeuristics.Guard(TacticalStance.Flee, 0.1f, 0.2) == TacticalStance.Withdraw, "fled in a winning raid");
    Require(TacticalHeuristics.Guard(TacticalStance.Flee, 0.1f, 0.6) == TacticalStance.Flee, "dying raider in a lost raid cannot flee");
    await Task.CompletedTask;
});

await Check("squad loop: one sample and one resolve per member, cooldown honoured", async () =>
{
    var loop = new SquadLoop(new SquadPlanner(), null, new AdaptiveHostilesSettings());
    var host = new FakeSquadHost { Snapshot = Squad() };
    loop.Tick("s", host, 100, 1);
    Require(host.Samples == 1 && host.Applied.Count == 2, "members not ordered");
    Require(host.Applied.Any(a => a.Id == "op") && host.OperativeRecord.Log.Count == 1, "operative not logged");
    loop.Tick("s", host, 110, 1);
    Require(host.Samples == 1, "resampled during cooldown");
    host.Blocked.Add("r1");
    loop.Tick("s", host, 400, 1);
    Require(host.Applied.Count == 4 && host.Applied[3].Order.Kind == OrderKind.None, "blocked raider interrupted");
    await Task.CompletedTask;
});

await Check("squad loop: remote answer arrives off-thread and stale cells are dropped", async () =>
{
    var settings = new AdaptiveHostilesSettings();
    var gate = new TaskCompletionSource();
    var client = new GatedClient(gate.Task, """
        {"answers":{"tactic":{"type":"choice","choice":"flank"},"focus":{"type":"choice","choice":"pawn_1"},
        "move_0":{"type":"choice","choice":"cell_a"},"stance_0":{"type":"choice","choice":"aggressive"},
        "move_1":{"type":"choice","choice":"cell_b"},"stance_1":{"type":"choice","choice":"aggressive"}}}
        """);
    using var scheduler = new JevScheduler<SquadDecision>(settings, d => d.IsModelDecision);
    var loop = new SquadLoop(new SquadPlanner(client), scheduler, settings);
    var host = new FakeSquadHost { Snapshot = Squad(), Map = new GridMap { Open = false } };
    loop.Tick("s", host, 100, 1);
    Require(loop.HasPending("s") && host.Applied.Count == 0, "blocked on network");
    gate.SetResult();
    await WaitFor(() => { loop.Tick("s", host, 120, 1); return host.Applied.Count > 0; });
    Require(host.Applied.All(a => a.Order.Kind == OrderKind.VanillaFallback), "stale destinations issued");
    Require(client.Calls == 1, "per-pawn calls");
    await Task.CompletedTask;
});

await Check("image requests use the image model and header; text requests do not", async () =>
{
    var text = new CaptureHandler();
    using (var http = new HttpClient(text))
        (await new JevClient(http, "k").DecideAsync(new { a = 1 }, Questions(), CancellationToken.None)).Dispose();
    Require(text.ImagesHeader is null, "text request sent the images header");
    Require(JsonDocument.Parse(text.Body!).RootElement.GetProperty("model").GetString() == "jev-latest", "text model changed");

    var image = new CaptureHandler();
    var png = PngWriter.EncodeIndexed(new byte[4], 2, 2, 1, new[] { ((byte)0, (byte)0, (byte)0) });
    using (var http = new HttpClient(image))
    {
        var (state, questions, assets) = BaseVision.Build(new BaseImage(png, 0, 0, 2, 2));
        (await new JevClient(http, "k").DecideAsync(state, questions, assets, CancellationToken.None)).Dispose();
    }
    Require(image.ImagesHeader == "true", "image request missing the images header");
    var root = JsonDocument.Parse(image.Body!).RootElement;
    Require(root.GetProperty("model").GetString() == "jev-1.14.0", "image model wrong");
    Require(root.GetProperty("assets").GetProperty(BaseVision.ImageId).GetProperty("type").GetString() == "image", "asset missing");
    Require(root.GetProperty("state").GetProperty("map").GetString() == BaseVision.ImageId, "state does not reference the image");
    Require(root.GetProperty("questions").GetProperty("weak_wall").GetProperty("type").GetString() == "box", "box question missing");
});

await Check("png writer makes a valid scaled palette image", () =>
{
    var png = PngWriter.EncodeIndexed(new byte[] { 0, 1, 1, 0 }, 2, 2, 3, new[] { ((byte)10, (byte)20, (byte)30), ((byte)200, (byte)100, (byte)50) });
    Require(png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "bad signature");
    Require(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)) == 6 &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)) == 6, "scaled size wrong");
    // Walk the chunks, check each CRC, and inflate the pixel data.
    var at = 8;
    using var idat = new MemoryStream();
    while (at < png.Length)
    {
        var len = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
        var type = Encoding.ASCII.GetString(png, at + 4, 4);
        var crc = Crc32(png.AsSpan(at + 4, len + 4));
        Require(crc == System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + len)), $"bad crc in {type}");
        if (type == "IDAT") idat.Write(png, at + 8, len);
        at += 12 + len;
    }
    idat.Position = 0;
    using var inflated = new MemoryStream();
    using (var z = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress)) z.CopyTo(inflated);
    var raw = inflated.ToArray();
    Require(raw.Length == 6 * 7, "pixel data size wrong");
    Require(raw[1] == 0 && raw[3] == 0 && raw[4] == 1 && raw[6] == 1, "pixels not scaled");
    Require(raw[8] == 0 && raw[14 + 4] == 1, "rows not repeated");
    Require(raw[21 + 1] == 1 && raw[21 + 4] == 0, "second cell row wrong");
    return Task.CompletedTask;
});

await Check("vision answers parse and only a sealed, trusted reading yields a wall cell", () =>
{
    var image = new BaseImage(Array.Empty<byte>(), 100, 50, 100, 80);
    BaseVisionResult Read(double sealedP, double hasWall, double conf, double l = 0.2, double t = 0.0, double r = 0.4, double b = 0.1) =>
        BaseVision.Parse(JsonDocument.Parse($$$"""
        {"answers":{"sealed":{"type":"noul","noul":{{{sealedP}}} },"has_wall":{"type":"noul","noul":{{{hasWall}}} },
        "weak_wall":{"type":"box","box":{"top":{{{t}}},"bottom":{{{b}}},"left":{{{l}}},"right":{{{r}}} },"confidence":{{{conf}}} } } }
        """).RootElement);
    var ok = new BaseVisionReading(image, Read(0.9, 0.9, 0.9));
    Require(ok.TryWallCell(out var x, out var z), "trusted reading rejected");
    // Centre x 0.3 of 100 cells from 100; centre y 0.05 from the top of an 80-cell image whose top row is z = 129.
    Require(x == 130 && z == 129 - 4, $"wrong cell ({x},{z})");
    Require(!new BaseVisionReading(image, Read(0.3, 0.9, 0.9)).TryWallCell(out _, out _), "open base still gave a wall");
    Require(!new BaseVisionReading(image, Read(0.9, 0.2, 0.9)).TryWallCell(out _, out _), "no wall still gave a wall");
    Require(!new BaseVisionReading(image, Read(0.9, 0.9, 0.2)).TryWallCell(out _, out _), "low confidence box accepted");
    Require(!new BaseVisionReading(image, Read(0.9, 0.9, 0.9, l: 0.6, r: 0.4)).TryWallCell(out _, out _), "inverted box accepted");
    try { BaseVision.Parse(JsonDocument.Parse("""{"answers":{"sealed":{"type":"noul","noul":0.9}}}""").RootElement); Require(false, "partial answer accepted"); }
    catch (InvalidDataException) { /* expected */ }
    return Task.CompletedTask;
});

await Check("a failed vision request backs off and later retries", async () =>
{
    var now = DateTime.UtcNow;
    using var scheduler = new JevScheduler<int>(null, null, () => now);
    Require(scheduler.CanBegin("m"), "fresh scheduler blocked");
    scheduler.TryBegin("m", _ => throw new HttpRequestException("down"));
    await WaitFor(() => scheduler.ConsecutiveFailures == 1);
    Require(!scheduler.CanBegin("m"), "no back-off after a failure");
    now = now.AddMinutes(10);
    Require(scheduler.CanBegin("m"), "back-off never ends");
});

await Check("ranged/melee sync: melee hold for cover, ranged cover the melee and only follow once melee are through", async () =>
{
    MovementCandidate C(string id, string purpose, int x, int z = 90) => new(id, x, z, 0.5f, 0.2f, 10, purpose, true);
    var near = new TargetCandidate("c_near", 150, 90, 0.8f, true);   // defender by the opening, close to the melee
    var far = new TargetCandidate("c_far", 150, 110, 0.9f, true);    // higher threat, but away from the melee
    SquadMember Melee(string id, int x) => new(new OperativeSnapshot(id, x, 90, 1f, "x", 1,
        new[] { C($"{id}_b", "breach_B1", 130), C($"{id}_p", "press_B1", 134) }, new[] { near, far }), false, true);
    SquadMember Ranged(string id, int x) => new(new OperativeSnapshot(id, x, 90, 1f, "x", 1,
        new[] { C($"{id}_s", "support_B1", 128, 94), C($"{id}_p", "press_B1", 134) }, new[] { near, far }), false, false);
    SquadSnapshot Snap(int breachCycles, params SquadMember[] ms) => new("s", ms, ms.Length, 0, 0,
        new[] { new BreachPoint("B1", 130, 90, 0.9f, 0, 0) }, SquadPhase.Breach, 0, breachCycles);
    var coverFire = new FakeClient("""{"answers":{"tactic":{"type":"choice","choice":"suppress_push"},"focus":{"type":"choice","choice":"none"},"breach":{"type":"choice","choice":"B1"},"phase":{"type":"choice","choice":"breach"},"melee_push":{"type":"choice","choice":"hold_for_cover"},"ranged_role":{"type":"choice","choice":"cover_fire"}}}""");

    // melee 10 cells out, ranged still 30 cells away: melee hold, then go once the wait runs out
    var wait = Snap(0, Melee("m1", 120), Ranged("r1", 98));
    var held = await new SquadPlanner().DecideAsync(wait);
    Require(held.Members["m1"].DestinationId == "hold" && held.Members["m1"].Stance == TacticalStance.Defensive, "melee dashed ahead of unready ranged");
    var timeout = await new SquadPlanner().DecideAsync(Snap(SquadCoordination.MaxCoverWaitCycles, Melee("m1", 120), Ranged("r1", 98)));
    Require(timeout.Members["m1"].DestinationId == "m1_b", "melee waited past the timeout");
    // ranged already in position: no hold
    var ready = await new SquadPlanner().DecideAsync(Snap(0, Melee("m1", 120), Ranged("r1", 128)));
    Require(ready.Members["m1"].DestinationId != "hold", "melee held although the ranged were in position");
    // a melee raider already at the opening is never held
    var atGap = await new SquadPlanner().DecideAsync(Snap(0, Melee("m1", 129), Ranged("r1", 98)));
    Require(atGap.Members["m1"].DestinationId != "hold", "committed melee was held");
    // the model can say go even though the ranged are not ready
    var go = new FakeClient("""{"answers":{"tactic":{"type":"choice","choice":"suppress_push"},"focus":{"type":"choice","choice":"none"},"breach":{"type":"choice","choice":"B1"},"phase":{"type":"choice","choice":"breach"},"melee_push":{"type":"choice","choice":"go_now"},"ranged_role":{"type":"choice","choice":"cover_fire"}}}""");
    Require((await new SquadPlanner(go).DecideAsync(wait)).Members["m1"].DestinationId != "hold", "go_now was ignored");
    Require(go.LastQuestions!.ContainsKey("melee_push") && go.LastQuestions.ContainsKey("ranged_role"), "coordination questions not asked");

    // covering fire: ranged shoot the defender nearest the melee, not the highest threat
    var cover = await new SquadPlanner(coverFire).DecideAsync(Snap(0, Melee("m1", 120), Ranged("r1", 128)));
    Require(cover.Members["r1"].TargetId == "c_near", "ranged did not cover the melee");

    // ranged do not run ahead: with the melee still out, even follow_through keeps ranged on the support cell
    var follow = new FakeClient("""{"answers":{"tactic":{"type":"choice","choice":"suppress_push"},"focus":{"type":"choice","choice":"none"},"breach":{"type":"choice","choice":"B1"},"phase":{"type":"choice","choice":"breach"},"melee_push":{"type":"choice","choice":"go_now"},"ranged_role":{"type":"choice","choice":"follow_through"}}}""");
    var early = await new SquadPlanner(follow).DecideAsync(Snap(0, Melee("m1", 118), Ranged("r1", 128)));
    Require(early.Members["r1"].DestinationId != "r1_p", "ranged moved up ahead of the melee");
    // melee through: follow_through moves ranged up even with a target in sight; cover_fire keeps them shooting
    var through = Snap(0, Melee("m1", 134), Ranged("r1", 128));
    Require((await new SquadPlanner(follow).DecideAsync(through)).Members["r1"].DestinationId == "r1_p", "ranged did not follow the melee through");
    Require((await new SquadPlanner(coverFire).DecideAsync(through)).Members["r1"].DestinationId != "r1_p", "cover_fire ranged left their post with a target in sight");
    // offline fallback keeps the same timing rule
    Require((await new SquadPlanner().DecideAsync(wait)).Members["m1"].DestinationId == "hold", "offline melee did not wait for ranged");
});

await Check("ranged go in behind engaged melee at a sealed breach instead of waiting outside", async () =>
{
    MovementCandidate C(string id, string purpose, int x, int z) => new(id, x, z, 0.5f, 0.2f, 10, purpose, true);
    var inside = new TargetCandidate("c1", 140, 90, 0.8f, true);
    var melee = new SquadMember(new OperativeSnapshot("m1", 133, 90, 1f, "x", 1,
        new[] { C("m1_b", "breach_B1", 130, 90) }, new[] { inside }), false, true);
    var ranged = new SquadMember(new OperativeSnapshot("r1", 124, 94, 1f, "x", 1,
        new[] { C("r1_s", "support_B1", 124, 94), C("r1_far", "advance", 110, 90), C("r1_in", "firing_position", 136, 91) }, System.Array.Empty<TargetCandidate>()), false, false);
    SquadSnapshot Snap(params SquadMember[] ms) => new("s", ms, ms.Length, 0, 0,
        new[] { new BreachPoint("B1", 130, 90, 0.5f, 1, 0, 128, 90) }, SquadPhase.Breach, 0, 1);
    var d = await new SquadPlanner().DecideAsync(Snap(melee, ranged));
    Require(d.Members["r1"].DestinationId == "r1_in", "ranged stayed outside while the melee fought");
    // melee not engaged and far away: ranged keep their support cell
    var farMelee = melee with { Snapshot = melee.Snapshot with { X = 100, Targets = System.Array.Empty<TargetCandidate>() } };
    var d2 = await new SquadPlanner().DecideAsync(Snap(farMelee, ranged));
    Require(d2.Members["r1"].DestinationId != "r1_in", "ranged went in before the melee were engaged");
});

await Check("a withdrawn raider stays out until it has treated its wounds, with a cap on the wait", () =>
{
    MovementCandidate C(string id, string purpose, int x) => new(id, x, 90, 0.5f, 0.2f, 10, purpose, true);
    SquadSnapshot Snap(float hp, bool needsTending) => new("s", new[] { new SquadMember(new OperativeSnapshot("r1", 120, 90, hp, "x", 1,
        new[] { C("safe", "retreat", 60), C("push", "press_B1", 135) }, System.Array.Empty<TargetCandidate>(), needsTending), false, true) },
        4, 0, 0, new[] { new BreachPoint("B1", 130, 90, 0.9f, 0, 0) }, SquadPhase.Breach);
    var loop = new SquadLoop(new SquadPlanner(), null, new AdaptiveHostilesSettings());
    var host = new FakeSquadHost();
    host.Snapshot = Snap(0.2f, true);           // hurt: withdraws
    loop.Tick("s", host, 600, 1);
    Require(host.Applied.Last().Order.Stance == TacticalStance.Withdraw, "hurt raider did not withdraw");
    host.Snapshot = Snap(0.9f, true);           // healed enough but still untreated: stays out
    loop.Tick("s", host, 2000, 1);
    Require(host.Applied.Last().Order.Stance == TacticalStance.Withdraw, "untreated raider went back in");
    host.Snapshot = Snap(0.9f, true);           // still untreated after the wait cap: released anyway
    loop.Tick("s", host, 600 + (int)SquadLoop.MaxTendWaitTicks + 100, 1);
    Require(host.Applied.Last().Order.Stance != TacticalStance.Withdraw, "untreated raider held out past the cap");
    host.Snapshot = Snap(0.2f, true);
    loop.Tick("s", host, 5000, 1);
    host.Snapshot = Snap(0.9f, false);          // treated: released
    loop.Tick("s", host, 9000, 1);
    Require(host.Applied.Last().Order.Stance != TacticalStance.Withdraw, "treated raider stayed out");
    return Task.CompletedTask;
});

await Check("flank: the game state offers it, the model picks how many raiders go", async () =>
{
    var squad = BreachSquad() with { Flank = new FlankOffer("B2", 2, 4), Defenders = 3 };
    string Answer(string flank) => "{\"answers\":{\"tactic\":{\"type\":\"choice\",\"choice\":\"focus_fire\"},\"focus\":{\"type\":\"choice\",\"choice\":\"none\"},\"breach\":{\"type\":\"choice\",\"choice\":\"B1\"},\"flank\":{\"type\":\"choice\",\"choice\":\"" + flank + "\"}}}";
    var three = new FakeClient(Answer("flank_3"));
    Require((await new SquadPlanner(three).DecideAsync(squad)).FlankSize == 3, "model flank size ignored");
    Require(three.LastQuestions!.ContainsKey("flank"), "flank question not asked");
    Require((await new SquadPlanner(new FakeClient(Answer("no_flank"))).DecideAsync(squad)).FlankSize == 0, "no_flank still split");
    Require((await new SquadPlanner(new FakeClient(Answer("flank_9"))).DecideAsync(squad)).FlankSize is >= 2 and <= 4, "invalid size not replaced by the default");
    var none = new FakeClient(Answer("flank_3"));
    Require((await new SquadPlanner(none).DecideAsync(BreachSquad())).FlankSize == 0 && !none.LastQuestions!.ContainsKey("flank"), "flank asked although the game state does not allow it");
});

await Check("flank offer reaches the loop: 12 raiders, 8 defenders, 3 breach points", () =>
{
    MovementCandidate C(string id, string purpose, int x) => new(id, x, 90, 0.5f, 0.2f, 10, purpose, true);
    SquadMember M(int i, bool melee, bool op = false) => new(new OperativeSnapshot($"p{i}", 100 + i, 90, 1f, "x", 1,
        new[] { C($"s1_{i}", "stage_B1", 100), C($"s2_{i}", "stage_B2", 101), C($"s3_{i}", "stage_B3", 102),
                C($"b1_{i}", melee ? "breach_B1" : "support_B1", 130), C($"b2_{i}", melee ? "breach_B2" : "support_B2", 140),
                C($"b3_{i}", melee ? "breach_B3" : "support_B3", 150) },
        new[] { new TargetCandidate("c1", 150, 90, 0.8f, true) }), op, melee);
    var members = Enumerable.Range(0, 12).Select(i => M(i, i < 6, i == 0)).ToList();
    var snap = new SquadSnapshot("s", members, 12, 0, 0,
        new[] { new BreachPoint("B1", 130, 90, 1f, 0, 0, 129, 90), new BreachPoint("B2", 140, 90, 1f, 0, 0, 139, 90), new BreachPoint("B3", 150, 90, 0.9f, 0, 0, 149, 90) },
        Defenders: 8);
    var loop = new SquadLoop(new SquadPlanner(), null, new AdaptiveHostilesSettings());
    var host = new FakeSquadHost { Snapshot = snap };
    for (var t = 0; t < 12; t++) loop.Tick("s", host, 600 + t * 300, 1);
    Require(host.Callouts.Any(c => c.Contains("flank offered")), "no flank offered; callouts: " + string.Join(" | ", host.Callouts));
    return Task.CompletedTask;
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

static async Task WaitFor(Func<bool> condition)
{
    for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
    if (!condition()) throw new Exception("timed out waiting for condition");
}

static RosterKind[] Kinds() => new[]
{
    new RosterKind("Thug", RosterRole.Melee), new RosterKind("Gunner", RosterRole.Ranged),
    new RosterKind("Grenadier", RosterRole.Heavy)
};

static uint Crc32(ReadOnlySpan<byte> bytes)
{
    var crc = 0xFFFFFFFFu;
    foreach (var b in bytes)
    {
        crc ^= b;
        for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
    }
    return crc ^ 0xFFFFFFFFu;
}

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static Dictionary<string, object> Questions() => new()
{
    ["scale"] = new { type = "choice", instructions = "size?", criteria = new Dictionary<string, string> { ["recon"] = "s", ["assault"] = "l" } },
    ["approach"] = new { type = "choice", instructions = "how?", criteria = new Dictionary<string, string> { ["direct"] = "d" } }
};

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

static SquadMember Member(string id, bool op, float health, params string[] cells) => new(
    new OperativeSnapshot(id, 130, 90, health, "AssaultRifle", 2,
        cells.Length > 0
            ? cells.Select((c, i) => new MovementCandidate(c, 140 + i, 87, 0.8f - i * 0.1f, 0.1f, 10 + i, i == 0 ? "firing_position" : "flank", true)).ToArray()
            : new[]
            {
                new MovementCandidate("cell_a", 140, 87, 0.8f, 0.1f, 10, "firing_position", true),
                new MovementCandidate("cell_b", 141, 87, 0.7f, 0.1f, 11, "flank", true),
                new MovementCandidate("cell_c", 142, 87, 0.5f, 0.2f, 12, "firing_position", true)
            },
        new[] { new TargetCandidate("pawn_1", 145, 86, 0.8f, true) }), op);

static SquadSnapshot Squad(float raiderHealth = 1f, int sent = 2, int down = 0) => new("s",
    new[] { Member("op", true, 1f), Member("r1", false, raiderHealth) }, sent, down, 0);

static SquadSnapshot BreachSquad()
{
    MovementCandidate C(string id, string purpose, int x) => new(id, x, 90, 0.5f, 0.2f, 10, purpose, true);
    var tgt = new[] { new TargetCandidate("pawn_1", 145, 86, 0.8f, true) };
    OperativeSnapshot Snap(string id, params MovementCandidate[] cells) =>
        new(id, 130, 90, 1f, "x", 2, cells, tgt);
    var melee = Snap("m1", C("cell_b1_push", "breach_B1", 150), C("cell_b2_push", "breach_B2", 160), C("cell_free", "cover", 131));
    var ranged = Snap("r1", C("cell_b1_support", "support_B1", 140), C("cell_b2_support", "support_B2", 141),
        C("cell_b1_push", "breach_B1", 150), C("cell_free2", "cover", 132));
    return new SquadSnapshot("s", new[] { new SquadMember(melee, false, true), new SquadMember(ranged, false, false) }, 2, 0, 0,
        new[] { new BreachPoint("B1", 150, 90, 0.2f, 4, 1), new BreachPoint("B2", 160, 90, 0.9f, 0, 0) });
}

static SquadSnapshot StagedSquad(bool arrived)
{
    MovementCandidate C(string id, string purpose, int x) => new(id, x, 90, 0.5f, 0.2f, 10, purpose, true);
    var tgt = new[] { new TargetCandidate("pawn_1", 145, 86, 0.8f, true) };
    OperativeSnapshot Snap(string id, string role, int stageX, int pushX) =>
        new(id, arrived ? stageX : 40, 90, 1f, "x", 2,
            new[] { C($"cell_stage_{id}", "stage_B1", stageX), C(role == "melee" ? "cell_push" : "cell_support", role == "melee" ? "breach_B1" : "support_B1", pushX) },
            tgt);
    var m = Snap("m1", "melee", 100, 150);
    var r = Snap("r1", "ranged", 101, 140);
    return new SquadSnapshot("s", new[] { new SquadMember(m, false, true), new SquadMember(r, false, false) }, 2, 0, 0,
        new[] { new BreachPoint("B1", 150, 90, 0.9f, 0, 0) });
}

sealed class FakeClient(string json) : IJevClient
{
    public IReadOnlyDictionary<string, object>? LastQuestions { get; private set; }
    public object? LastState { get; private set; }
    public int Calls { get; private set; }
    public Task<JsonDocument> DecideAsync(object state, IReadOnlyDictionary<string, object> questions, CancellationToken token)
    {
        Calls++;
        LastState = state;
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
    public string? ImagesHeader { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Uri = request.RequestUri?.ToString();
        ImagesHeader = request.Headers.TryGetValues("TypeSafe-Danger-Zone-Images", out var h) ? string.Join(",", h) : null;
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

sealed class TurretMap : ITacticalMap
{
    public bool IsStandable(int x, int z) => true;
    public bool IsReachable(int x, int z) => true;
    public float CoverAt(int x, int z) => 0.5f;
    public float ExposureAt(int x, int z) => 0.2f;
    public bool InTurretRange(int x, int z) => x > 50;
}

sealed class GridMap : ITacticalMap
{
    public bool Open { get; set; } = true;
    public bool IsStandable(int x, int z) => Open && x >= 0 && z >= 0 && x < 200 && z < 200 && (x + z) % 7 != 0;
    public bool IsReachable(int x, int z) => Open;
    public float CoverAt(int x, int z) => (x % 3) / 2f;
    public float ExposureAt(int x, int z) => (z % 4) / 4f;
}

sealed class StubHandler(string body) : HttpMessageHandler
{
    public string? Uri { get; private set; }
    public Dictionary<string, string> Headers { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Uri = request.RequestUri?.ToString();
        foreach (var h in request.Headers.NonValidated) Headers[h.Key] = h.Value.ToString();
        Headers["auth"] = request.Headers.Authorization?.ToString() ?? "";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
    }
}

sealed class FakeHost : IOperativeHost
{
    public PawnBlock Block { get; set; }
    public TacticalTrigger Trigger { get; set; }
    public ITacticalMap Map { get; set; } = new GridMap();
    public ITacticalMap CurrentMap => Map;
    public int Samples { get; private set; }
    public List<OperativeOrder> Applied { get; } = new();

    public OperativeSnapshot Sample()
    {
        Samples++;
        return new OperativeSnapshot("op", 130, 90, 0.85f, "AssaultRifle", 2,
            new[] { new MovementCandidate("cell_142_87", 142, 87, 0.9f, 0.1f, 12, "cover", true) },
            new[] { new TargetCandidate("pawn_1", 145, 86, 0.8f, true) });
    }

    public void Apply(OperativeOrder order) => Applied.Add(order);
}

sealed class GatedClient(Task gate, string json) : IJevClient
{
    private int _calls;
    public int Calls => _calls;
    public async Task<JsonDocument> DecideAsync(object state, IReadOnlyDictionary<string, object> questions, CancellationToken token)
    {
        Interlocked.Increment(ref _calls);
        await gate.WaitAsync(token);
        return JsonDocument.Parse(json);
    }
}

sealed class FakeSquadHost : ISquadHost
{
    public OperativeRecord Timing { get; } = new("squad", "Pirate");
    public OperativeRecord OperativeRecord { get; } = new("op", "Pirate");
    public TacticalTrigger Trigger => TacticalTrigger.None;
    public ITacticalMap Map { get; set; } = new GridMap { Open = true };
    public HashSet<string> Blocked { get; } = new();
    public int Samples { get; private set; }
    public List<(string Id, OperativeOrder Order)> Applied { get; } = new();
    public List<string> Callouts { get; } = new();
    public void Callout(string text) => Callouts.Add(text);

    public SquadSnapshot Snapshot { get; set; } = null!;
    public SquadSnapshot Sample() { Samples++; return Snapshot; }
    public PawnBlock BlockOf(string id) => Blocked.Contains(id) ? PawnBlock.Downed : PawnBlock.None;
    public ITacticalMap CurrentMap(string id) => Map;
    public OperativeRecord? RecordOf(string id) => id == "op" ? OperativeRecord : null;
    public void Apply(string id, OperativeOrder order) => Applied.Add((id, order));
}
