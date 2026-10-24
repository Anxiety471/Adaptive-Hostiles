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

### Squad command

A raid that contains an Operative is commanded as one squad by Jev, not only the Operative.

- One squad decision per cycle (same 120–300 tick cooldown and commitment as the Operative), never one call per pawn. The snapshot holds up to **12** controllable spawned raiders (the Operative first, then those nearest the enemy) with their own candidate cells and visible targets.
- Questions: a squad `tactic` (`focus_fire`, `flank`, `suppress_push`, `regroup`), one squad `focus` target, and for each member a `move_i` Choice over that member's candidate IDs and a `stance_i` Choice. Answers are mapped back to the exact offered cells and revalidated per member before any job starts.
- A member whose answer is invalid, or whose cell another member already took, uses the local heuristic for that member only. A wholly invalid squad answer uses the local squad fallback.
- Members beyond the cap, blocked, downed or in a Jev-ordered exit are left to their current job / vanilla AI.

### Turrets and engagement

- Hostile powered turrets are part of the battlefield: they are offered as visible targets (kind `turret`, with range) and candidate cells inside their range are flagged and penalised, so Jev can focus a turret or stay out of its fire.
- Squad members with no enemy pawn or turret within the scan radius (60 cells) are left to vanilla assault AI so they keep marching on the colony; the squad only commands engaged raiders.
- Squad cadence is slower than the lone Operative: 360–600 tick cooldown and 360 tick commitment, so orders are not replaced mid-walk.
- Every member must get a distinct destination.

### Breach and roles

- The Mod scores **breach points** around the defenders: reachable cells near the colony whose weakness falls with every defender who sees them and every turret covering them. The kill zone in front of the defenders scores low. The top 3 (`B1`..`B3`) go to Jev in a `breach` Choice; the squad answers one.
- Each member also gets role cells for every breach point: **melee** a `breach_<id>` cell at the point, **ranged** a `support_<id>` cell behind it on the raiders' side. All members must use the chosen breach point's cells, and ranged members may not take the melee `breach_` cells, so melee push in front with ranged backup from behind, together. Offline, the weakest breach is chosen and roles follow it.
- **Synchronized assault.** Each raider also gets a `stage_<id>` cell about 16 cells out on the raiders' side. The squad answer carries a `phase`: `gather` (everyone goes to its staging cell and holds, nobody presses), `breach` (the go signal: melee through the breach, ranged support behind) or `press`. Local code owns the go signal: breach/press are only allowed once at least **75%** of members stand within 4 cells of their staging cell; before that every healthy member is sent to its staging cell with a defensive stance, whatever the model said. Wounded members still withdraw. Once the squad has gone it does not return to gathering, and after 4 gather cycles it goes anyway so a stuck raider cannot stall the raid.
- Jev's orders own the squad's movement: each order clears the pawn's queued jobs and ends with a `Wait_Combat` hold, so vanilla assault AI cannot walk a raider elsewhere between orders.

### Aggression and survival

- Default posture is aggressive: press the attack from the best-covered firing position, advance or flank cell; spread members over different approaches and focus fire.
- Self-preservation: a member below **35%** health withdraws to cover or retreat cells.
- Local fallback (no model): the same rules. Everyone is aggressive; wounded members withdraw.

### Fleeing

- The base game's flee check is disabled for squad-controlled raiders: their lord may not enter an exit, flee, steal-and-leave or kidnap toil, and panic/flee mental states are refused. A vanilla exit duty never blocks the tactical loop. Vanilla exit logic is allowed again after 30000 ticks of squad control so a raid cannot hang.
- **The Operative never flees.** A model `flee` answer for it is downgraded to `withdraw`.
- For ordinary raiders **Jev decides** whether to flee (`flee` stance, an exit-map job). Without a model answer the local fallback flees only a raider below **20%** health once at least **half** the raid is down or gone.

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
| `RaidIncidentAdapter` | Eligible incident filtering, cached plans, roster and arrival | Core roster/cache, `RaidHistory` and RimWorld composition hook done (unverified in game) |
| `SquadPlanner` / `SquadLoop` | One coordinated Jev decision per raid squad; per-member revalidation; no-flee Operative | Core done and tested; `Mod/Tactics/SquadDriver` and `Raids/FleePatches` written (unverified in game) |
| `OperativeRegistry` | Mark, upgrade, track and serialize Operatives | Core records, cooldowns, decision log done; RimWorld save hooks TODO |
| `TacticalSampler` | Perceived state, feasible target/cell enumeration | Core `CandidateEnumerator` done; RimWorld map adapter and sampler written (`Mod/Tactics`, unverified in game) |
| `OperativeJobExecutor` | Revalidate candidate then assign legal native jobs | Core `OperativeOrderResolver` and `OperativeTacticalLoop` done; Goto/attack job assignment and per-tick driver written (unverified in game) |
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
