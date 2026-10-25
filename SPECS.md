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

- Hostile turrets, powered or not, are part of the battlefield: once within the scan radius they are always offered as targets (kind `turret`, with range), whether or not the raider has line of sight, and they are listed before pawns so the target cap never drops them. Candidate cells inside their range are flagged and penalised, so Jev can focus a turret or stay out of its fire.
- Until one raider has an enemy pawn or turret within the scan radius (60 cells), the raid stays on vanilla assault AI and keeps marching on the colony. From then on every raider that can be ordered is in the squad, including one that has lost sight of the enemy, so none is left to roam alone on vanilla AI.
- Squad cadence is slower than the lone Operative: 360–600 tick cooldown and 360 tick commitment, so orders are not replaced mid-walk.
- Every member must get a distinct destination.

### Breach and roles

- The Mod finds **breach points** the way vanilla sappers choose where to dig: for up to 5 defenders and 5 turrets it computes the lead raider's shortest path (passing destroyable things) and takes the **first wall or door on that path** as the breach. The breach point's cell is the standable, reachable cell just outside that wall (`WallX/WallZ` carry the wall itself). In an open base there is no wall, so the point is the path cell 6 steps short of the target. It is never a ring cell around the defenders. Weakness falls with every defender who sees the cell and every turret covering it. Candidates are ranked by weakness, at least 8 cells apart, one per wall; the top 3 (`B1`..`B3`) go to Jev in a `breach` Choice (the option text names the wall), and the squad answers one. Points are pinned per raid for 6000 ticks.
- Each member also gets role cells for every breach point: **melee** a `breach_<id>` cell at the point, **ranged** a `support_<id>` cell behind it on the raiders' side, 3 to 6 cells back at an open breach (30% of weapon range) so they stay close to the melee and in range of the wall. For a sealed breach every role and staging cell is on the raiders' side of the breach wall, never inside the base, so no raider walks around the wall to reach its cell. All members must use the chosen breach point's cells, and ranged members may not take the melee `breach_` cells, so melee push in front with ranged backup from behind, together. Offline, the weakest breach is chosen and roles follow it.
- **Corner ambush.** At a walled breach, ranged support cells sit just outside the wall on the raiders' side, offset 2 cells sideways from the opening (alternating sides per raider). Among nearby cells the Mod prefers low exposure to the defenders and cells that hug a wall, so ranged shoot from the corner instead of standing in the line of the opening. Staging cells use the same choice.
- **Press through.** At an open breach (no wall to break) melee also get a `press_<id>` cell, a different one per raider, 3 or more cells past the opening toward the defenders. A melee raider within 3 cells of its `breach_` or `press_` cell is ordered onto its press cell (Aggressive) and stays on it, so the squad streams through the gap instead of clogging it. Wounded melee (Withdraw/Flee) never press.
- **Stale breach.** When the wall or door of a pinned breach has been destroyed, the breach points are recomputed on the next cycle so the squad moves on to the next wall or the open path.
- **Go signal.** The squad leaves staging only when every healthy member stands within 4 cells of its staging cell; a member that is withdrawing or fleeing does not hold the squad. After 4 gather cycles the squad goes anyway. Squad slots (and so role cells) are ordered by operative flag and pawn id, not by distance, so a raider keeps its cell from one cycle to the next.
- **Ranged engagement.** A ranged raider with a visible enemy within 3 cells shoots it where it stands (no kiting, no repositioning). At an open breach, once the squad has gone, a ranged raider that reached its support cell with no visible target moves up to the opening. A stall only turns ranged into wall breakers when the other breach point actually has a wall.
- **Synchronized assault.** Each raider also gets a `stage_<id>` cell about 16 cells out on the raiders' side. The squad answer carries a `phase`: `gather` (everyone goes to its staging cell and holds, nobody presses), `breach` (the go signal: melee through the breach, ranged support behind) or `press`. Local code owns the go signal: breach/press are only allowed once at least **75%** of members stand within 4 cells of their staging cell; before that every healthy member is sent to its staging cell with a defensive stance, whatever the model said. Wounded members still withdraw. Once the squad has gone it does not return to gathering, and after 4 gather cycles it goes anyway so a stuck raider cannot stall the raid.
- **Sealed bases.** A melee raider pushing a `breach_` cell attacks the breach point's own wall or door (the one found above, still standing); once it is down, the next wall or door on its current shortest path to the defenders. A walled colony is broken into at the path wall rather than circled. Blocking the vanilla retreat transition also suppresses its "are leaving" message.
- **Ranged support from the start.** On the go signal ranged raiders take their support cells at once, together with the melee; they do not wait for the melee to arrive. At a sealed breach a ranged raider shoots the breach wall unless it can see its target from the support cell.
- **Non-colony enemies.** Only the colony's own pawns and turrets are defenders: breach paths, breach points and wall breaking aim at them. Any other hostile in line of sight within the scan radius (mechanoids, manhunters, another faction's pawns) is offered as an `enemy` target with threat 1.0 or more, above any colonist, so ranged raiders shoot it before it closes in and melee meet it; the per-target cap of 3 applies. Cells near enemies count as exposed like cells near colonists. A mech cluster that is still dormant is ignored until it wakes.
- **Turret focus.** When a turret covers the chosen breach point the offline squad focus is a turret, and ranged raiders prefer a visible turret over colonists as their own target so they shoot it from outside its range while the melee push in; melee keep their usual target. The model is told to focus a turret when turrets cover the breach. The per-target cap of 3 still applies.
- **Withdraw.** A withdrawing raider may only go to a `retreat` or `cover` cell, falling back to its staging cell, and holds when there is none; it is never sent to a breach, support or other attack cell, and a model answer that does so is replaced by the offline order. Once a raider has withdrawn it stays withdrawn until its health is back above 50%, so a hurt melee raider does not retreat and then attack again.
- **Danger memory.** Every cell where a raider went down or died is remembered and fades linearly over one in-game day (60000 ticks). A mark penalises candidate cells and breach points within 6 cells (nearer and fresher counts more; summed and capped at 1): a candidate's score loses `0.8 * danger`, a breach point's weakness is multiplied by `1 - 0.8 * danger`, and a new loss recomputes the raid's breach points. The 5 strongest zones go to Jev as `dangerZones`, and cells and breach options with danger above 0.05 say so in their text.
- **Replan on stall.** Once the squad has left gathering, a cycle counts as idle when no damage was dealt (defender health lost, a defender downed, or breach wall hit points lost) and the closest melee raider gained under 1.5 cells on the chosen breach point. After 3 idle cycles on the same breach point it is set aside and the next one is offered. When every point has stalled, the exclusions clear and ranged raiders are given the melee `breach_` role at the weakest other point, so they open a second wall.
- **Second breach group.** With 6+ raiders, a second breach point and at most one defender per two raiders, one melee and one ranged raider (never the Operative) split off to the weakest other breach point. This is decided once per raid; the group keeps its own breach point for staging and roles.
- **Target cap.** At most 3 raiders are ordered onto one target. Later raiders take their next best visible target with room, or none.
- **Intent log.** On each phase change (gather, breach, press), and on a stall or split, one short line goes to the mod log (`intent (raid ...): ...`). It is never shown in game.
- **Suppression (dropped).** A ranged suppress role aimed at the doorway or defender firing positions is not built: RimWorld has no suppression mechanic that makes defenders duck or hold fire, and `AttackStatic` at a cell only wastes ammunition, so there is no clean defender-reaction hook.
- **Aim.** Firing positions are short to medium range (30–65% of weapon range). An order identical to the one a raider is already carrying out (same cell, walking, shooting or holding) does not restart its job.
- **Flee.** Only a raider under 20% health in a raid that has lost at least half its members may flee; any other flee answer becomes withdraw.
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
