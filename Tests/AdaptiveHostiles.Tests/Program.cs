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

await Check("B2 stall: three idle cycles set the breach aside; all stalled means ranged open a second wall", () =>
{
    var t = new StallTracker();
    var keys = new[] { "1,1", "2,2" };
    Require(!t.Observe("1,1", 0, 30, keys), "first look stalled");
    Require(!t.Observe("1,1", 0, 30, keys) && !t.Observe("1,1", 0, 30, keys), "stalled too early");
    Require(t.Observe("1,1", 0, 30, keys), "three idle cycles did not stall");
    Require(t.IsExcluded("1,1") && !t.RangedOpensWall, "stalled breach not set aside");
    Require(!t.Observe("2,2", 0, 30, keys), "new breach inherited the idle count");
    Require(!t.Observe("2,2", 0.2, 30, keys) && !t.Observe("2,2", 0, 20, keys), "progress counted as idle");
    Require(!t.Observe("2,2", 0, 20, keys) && !t.Observe("2,2", 0, 20, keys), "idle count did not restart after progress");
    Require(t.Observe("2,2", 0, 20, keys) && t.RangedOpensWall, "second stall did not open the second wall");
    Require(!t.IsExcluded("1,1"), "exclusions kept after every point stalled");
    var pts = new[] { new BreachPoint("B1", 1, 1, 1f, 0, 0), new BreachPoint("B2", 2, 2, 0.5f, 0, 0) };
    var t2 = new StallTracker();
    for (var i = 0; i < 4; i++) t2.Observe("1,1", 0, 30, new[] { "1,1", "2,2" });
    Require(t2.Available(pts, b => $"{b.X},{b.Z}").Select(b => b.Id).SequenceEqual(new[] { "B2" }), "stalled point still offered");
    return Task.CompletedTask;
});

await Check("B2 squad loop: a stalled assault is called out in the log and ranged told to open a second wall", () =>
{
    var loop = new SquadLoop(new SquadPlanner(), null, new AdaptiveHostilesSettings());
    var host = new FakeSquadHost { Snapshot = StagedSquad(arrived: true) };
    for (var i = 0; i < 6; i++) loop.Tick("s", host, 1000 + i * 1000, 1);
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
    Require(SquadSplit.Choose(six, pts, "B1", 4) is null, "split against a strong defence");
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
    Require(host.Callouts.Count == 1 && host.Callouts[0].Contains("gathering") && host.Callouts[0].Contains("breach B1"), "gather callout missing or repeated");
    host.Snapshot = StagedSquad(arrived: true);
    loop.Tick("s", host, 2000, 1);
    loop.Tick("s", host, 3000, 1);
    Require(host.Callouts.Count == 2 && host.Callouts[1].Contains("melee pushing"), "breach callout missing or repeated");
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

await Check("squad: raiders gather at staging cells and only go together on the signal", async () =>
{
    var far = StagedSquad(arrived: false);
    var d = await new SquadPlanner().DecideAsync(far);
    Require(d.Phase == SquadPhase.Gather, "attacked before gathering");
    Require(d.Members["m1"].DestinationId == "cell_stage_m1" && d.Members["r1"].DestinationId == "cell_stage_r1", "not sent to staging cells");
    Require(d.Members.Values.All(m => m.Stance == TacticalStance.Defensive), "attacked while gathering");

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
