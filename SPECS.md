# Adaptive Hostiles — Technical Specification

Version: **0.1.0-draft**  
Target: **RimWorld 1.7 unstable beta / .NET 10 / Harmony 3**  
Model: **TypeSafe Jev (`jev-latest`, System One API)**

## 1. Non-negotiable scope

- **No custom storyteller**. All installed storytellers remain available and preserve their incident timing.
- Replace the **composition logic for eligible hostile raid incidents**. Do not globally zero, overwrite, or patch raid-point calculations relied on by quests or other incident types.
- The new raid composer uses colony state and Jev decision categories, **not** vanilla threat or raid-point values, to choose number, kinds, and gear.
- A configurable small fraction of those raids include one **Jev Operative**, equipped and skilled above the normal faction pawn.
- Jev must choose the Operative's **specific destination** from valid map cells, as well as a target and tactical stance. RimWorld executes the resulting move/combat jobs.
- Exclude friendly raids, manhunters, mech clusters, quest-spawned encounters, and custom raid workers unless explicitly supported by a compatibility adapter.

## 2. Boundaries and flow

1. The installed storyteller schedules a normal hostile raid.
2. The raid adapter checks incident/faction eligibility, retrieves a cached decision for the relevant colony, and selects a local bounded fallback if cache or API is unavailable.
3. A **roster generator** maps Jev's `scale` and `approach` to faction-compatible PawnKindDefs, loadouts, and pawn counts. It does not ask vanilla to spend `IncidentParms.points` or use `PawnGroupMakerUtility`'s point budget.
4. The normal arrival/lord/letter systems are used where their APIs permit; otherwise the adapter must supply equivalent arrival/assault/exit behavior, without consuming raid points for composition.
5. At the configured chance (default proposed: **5% of eligible raids**, max one per raid), choose a suitable pawn as the Operative and upgrade stats/equipment within faction tech bounds.
6. Periodically sample the Operative's perceived battlefield state on the main thread, enumerate accessible tactical cells, and request a decision asynchronously. Apply the latest valid answer on the main thread. Do not block ticks.

## 3. Colony state supplied to Jev

An intentionally bounded structured snapshot:

| Field | Meaning |
| --- | --- |
| `total_wealth` | Colony wealth, inventory, and installed structures; numeric signal only |
| `colonist_count` | Alive free colonists |
| `combat_ready_count` | Colonists able to fight |
| `ranged_weapon_count` | Equipped ranged weapons |
| `turret_count` | Powered/unpowered turret count (future: split) |
| `defensive_structure_count` | Walls, embrasures, defensive structures (candidate definition TBD) |
| `injured_colonist_count` | Colonists with injuries |
| `raid_history` | Aggregated outcomes by hostile faction, persisted between loads |
| `faction` | Incoming raid faction's tech and available PawnKindDefs |

The initial raid decision asks independent typed Choice questions, `scale` (`recon`, `skirmish`, `assault`, `invasion`) and `approach` (`direct`, `flank`, `breach`, `siege`). Their result is not executable until the roster/strategy adapter validates faction and map feasibility. Jev does not directly output a numeric raid-point budget.

### Raid sizing policy

- `recon`: small probing squad
- `skirmish`: modest attack
- `assault`: substantial force
- `invasion`: major attack
- Default size mapping is a configurable **bounded pawn count** determined by the choice and available faction roster—not by vanilla points.
- Start with conservative limits; provide configurable maximum raiders and count multiplier, especially for endgame colonies.
- No promise that Jev's choice will be balanced without empirical playtests. Calibrate using raid outcomes, not just theoretical wealth.

## 4. Jev Operative

### Spawn and stats

- Spawn chance: proposed 5% per eligible raid, seeded/serialized for reproducible saves.
- One per incident by default; do not make every raid a boss battle.
- Humanlike, hostile, faction-compatible pawn. Respect restrictions from Ideology, Biotech, Anomaly, and modded faction pawn kinds.
- Proposed range: shooting 16–20, melee 14–20, high-quality faction-appropriate armor/weapons, possible enhancements. Stats and gear are configurable and are **not yet applied by the scaffold**.
- Surviving Operatives may recur only if persistence and faction compatibility make it safe.

### Decision loop

1. Sample self position, health, equipment/range, current destination, nearest hostile pawns, visible targets, allies, cover and attack lines.
2. Derive at most **32 valid reachable destination candidates**: hold, cover, firing position, advance, flank, retreat. Each candidate has an opaque ID, cell coordinates, reachability, exposure, cover quality, distance and reason.
3. Send those exact candidates to Jev as a `movement` Choice. Separately ask a `target` Choice and `stance` Choice using the same state.
4. Translate the **returned candidate ID** to its original, verified destination. Revalidate before starting the job, since positions may have changed during network latency.
5. Enqueue native movement/combat jobs on the game thread. Native pathfinding executes Jev's destination; it does **not** pick a different tactical endpoint.
6. Repeat after a cooldown (initially 120–300 ticks) or a meaningful change such as damage, a target loss, or blocked route. Add minimum commitment time to avoid twitching.

Example Jev movement answer (`movement.choice`): `"cell_142_87"`. The mod maps this ID to a `Map`-valid `IntVec3(142, 0, 87)`. Model-supplied arbitrary x/z numbers are **never** trusted.

### Hard constraints

- No invalid/off-map/unreachable cells, hostile fire friendly targets, or illegal Jobs.
- Never call the network on the main thread or per tick.
- Apply a strict network deadline and rate limit (e.g. one in-flight decision per Operative, global concurrency cap) with stale-response invalidation.
- Low-confidence, invalid, stale, 429, timeout or disconnected result: fallback to vanilla pawn AI or a safe local maneuver.
- Do not interrupt stun, downed, mental break, incapacitation, scripted duty, death, or higher-priority forced states.
- Persist assigned Operative status and cooldowns; do **not** persist raw HTTP tasks, API credentials, or transient path searches.

## 5. Implementation modules

| Module | Responsibility | Scaffold status |
| --- | --- | --- |
| `AdaptiveHostiles.Core` | Typed snapshots, client, decisions, result validation, fallbacks | Present |
| `AdaptiveHostiles.Mod` | RimWorld mod bootstrap, future patch hooks and adapters | Bootstrap only |
| `RaidIncidentAdapter` | Eligible incident filtering, cached plans, roster and arrival | Core roster/cache done (`RosterGenerator`, `DecisionCache`); RimWorld hooks TODO |
| `OperativeRegistry` | Mark, upgrade, track and serialize Operatives | Core records, cooldowns, decision log done; RimWorld save hooks TODO |
| `TacticalSampler` | Perceived state, feasible target/cell enumeration | Core `CandidateEnumerator` done over `ITacticalMap`; map adapter TODO |
| `OperativeJobExecutor` | Revalidate candidate then assign legal native jobs | Core `OperativeOrderResolver` done; job assignment TODO |
| `JevScheduler` | Async throttling, timeouts, cancellation, cache and main-thread handoff | Done (`JevScheduler<T>`) |

## 6. Failure behavior

| Failure | Required behavior |
| --- | --- |
| No TypeSafe key | Use deterministic fallback; do not crash or hang |
| 401/429/529/network outage | Backoff, cap retries, continue ordinary gameplay |
| Malformed/unknown Jev Choice | Reject unknown ID/label; use fallback |
| Selected cell no longer reachable | Replan later; use vanilla safe movement immediately |
| Raid adapter cannot accommodate a faction or DLC mechanic | Defer to vanilla raid for that incident, with a warning; clearly mark it as bypassed, not "Jev raid" |
| Save/load while decision in flight | Cancel/discard stale decision and reconstruct state |
| Multiplayer/determinism concerns | Default unsupported until host-authoritative synchronization is implemented |

## 7. Security and privacy

- API key remains local and must never be committed or included in diagnostics. In production, prefer a local proxy or encrypted secret storage over writing the key in a plaintext game settings file.
- The official API is `POST https://api.typesafe.ai/v1/systemone`; do not use look-alike Jev domains.
- Send only game-state summaries, not OS paths, user identities, save file bytes, or credentials.
- Limit payload size and concurrent requests. Validate all model labels against options generated locally.

## 8. Acceptance tests before a playable release

1. All base storytellers still appear and schedule raids normally.
2. Raid composition is **independent of `IncidentParms.points`**: two raids with the same Jev plan but different points produce the same candidate roster under an identical seed.
3. At least one tested raid uses a local fallback with no internet and does not hang gameplay.
4. Distinct factions generate their own permitted gear and pawn kinds.
5. An Operative demonstrably moves to multiple different **Jev-selected exact cells** during a combat scenario; saved decision log matches destination job targets.
6. Illegal/unreachable/outdated Jev destinations never cause teleportation, freezes, or exceptions.
7. API work remains off-thread; normal-speed and high-speed TPS regression stays within an agreed performance budget.
8. Save/load before, during, and after a raid preserves legitimate pawn ownership, hostile group behavior, and Operative identity.
9. 100 seeded raid simulation runs measure force mix, extreme outliers, faction compatibility, and elite appearance rate.

## 9. Explicit non-goals for the first playable version

- Training or fine-tuning Jev.
- Full LLM-based storytelling, dialogue, or event scheduling.
- Direct control of animation frames, arbitrary raw path routes, or game-engine calls from Jev.
- Removing vanilla points from every RimWorld subsystem.
- Replacing all hostile incidents from DLCs and other mods on day one.
