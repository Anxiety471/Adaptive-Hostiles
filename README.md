# Adaptive Hostiles

**Raids that think.** With a decision-model key, every hostile raid is commanded as one squad by an AI decision model (Jev by default), so raiders gather, find the weakest way in, push breaches with melee in front and ranged covering, kite with long-range weapons, and go after colonists caught in the open. They do not run away on their own.

> **Status: playable, in testing.** Built for RimWorld 1.7. The squad behaviour below has been tried in live raids against fortress layouts, open ground and turret-heavy bases; expect rough edges and please report them. The elite Operative pawn has been dropped: no special pawn is spawned.

## What raiders do

- **One squad, one plan.** A decision every two seconds picks the target, the breach point and each raider's cell and stance. Code lists the legal options from the game state and the model only chooses among them, so it can never invent a coordinate.
- **Gather, then go.** Raiders stage together and push on a go signal. The wait ends at once when colonists stand in the open.
- **Melee in front, ranged behind.** Melee wait in cover until the ranged raiders are in position, then take the breach. Ranged cover them and move up only once the melee are through.
- **Real breaches.** Raiders find the weakest wall, door or opening on the path to the colony and break it, and use a second group to flank through another opening when there is one.
- **Kiting.** Ranged raiders step out of colonists' weapon range and shoot from inside their own, unless the model chooses to go all in. Melee raiders always swarm. Shield-belt wearers press in.
- **Colonists in the open** are attacked directly instead of being ignored in favour of the base. Ranged raiders who lose sight spread around the target from several angles.
- **Turrets:** melee keep hitting a turret until it is destroyed and step out of the way of blasts.
- **One-opening buildings:** a small group goes through the door while the rest hold their own firing spots.
- **No retreat on their own.** Vanilla "satisfied with the damage, leaving" is blocked while the squad commands the raid. When the last colonist is dead the squad lets go and vanilla AI loots and leaves.
- Wounded raiders pull back and tend themselves before rejoining.

Raids still arrive when your storyteller says so; Adaptive Hostiles changes how raiders fight, not when they come.

## Decision models

Pick a model in **Options > Mod settings > Adaptive Hostiles**: TypeSafe Jev (recommended), Anthropic, or any OpenAI-compatible server. **A key is required.** With no key (or the provider set to Off) the mod does nothing and raids stay vanilla. Network calls run in the background and never block the game; a slow or failed answer falls back to the built-in rules for that cycle.

## Design

See [SPECS.md](SPECS.md) for behaviour, interfaces and guardrails, and [CHANGELOG.md](CHANGELOG.md) for what changed.

| Layer | Role |
| --- | --- |
| Storyteller | Raid timing, unchanged |
| Squad planner (Core, tested offline) | Turns game state into legal options, asks the model, validates answers, falls back locally |
| Mod layer (Harmony) | Samples the map, issues native jobs, blocks vanilla retreat while commanded |

## Build (RimWorld 1.7 unstable beta)

The 1.7 Waylith build uses **.NET 10**. You need the .NET 10 SDK and a local copy of RimWorld 1.7 plus the Harmony mod for 1.7. Proprietary game DLLs and Harmony DLLs are **not** checked into this repository.

```bash
# Run the game-independent tests first:
dotnet run --project Tests/AdaptiveHostiles.Tests/AdaptiveHostiles.Tests.csproj

# Build the RimWorld assembly. Paths must point to your own installs:
dotnet build Source/AdaptiveHostiles.Mod/AdaptiveHostiles.Mod.csproj -c Release \
  -p:RimWorldDir="/path/to/RimWorld" \
  -p:HarmonyDll="/path/to/Harmony/1.7/Assemblies/0Harmony.dll"
```

The mod project's build output is `1.7/Assemblies/`. Copy the **entire repository mod folder** (excluding Source, Tests, and .git if preferred) into RimWorld's local `Mods` directory and enable Harmony above Adaptive Hostiles. The mod replaces eligible hostile raid rosters (Harmony patches in `Source/AdaptiveHostiles.Mod/Raids`).

For development, set `TYPESAFE_API_KEY` in your environment. The core client sends requests to TypeSafe's official `https://api.typesafe.ai/v1/systemone` endpoint with model `jev-latest`. Other decision models are selectable with `ADAPTIVE_HOSTILES_PROVIDER` (`jev`, `anthropic`, `openai`, `local`; default `auto` picks the first of `TYPESAFE_API_KEY`, `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`), with `ADAPTIVE_HOSTILES_MODEL` and `ADAPTIVE_HOSTILES_ENDPOINT` (any OpenAI-compatible server, e.g. a local Ollama) as overrides. **Never commit an API key.** In game, use the mod settings page instead.

## Principles

1. **No storyteller replacement** and no change to raid scheduling.
2. **The model chooses, code decides what is possible.** It picks among reachable cells, targets and stances; it cannot teleport, path through walls or fabricate a coordinate.
3. **Nonblocking and recoverable.** Network calls never run on the main thread. A failed, missing or stale decision falls back to bounded local logic.
4. **Faction and mod compatibility first.** No blanket changes to friendly raids, quests, traders or unrelated incidents.

## Roadmap

- [x] Squad command: breach points, staging, melee/ranged sync, cover hops, flanking
- [x] Kiting, colonists-in-the-open, turret melee, single-opening funnel
- [x] Pluggable decision models and settings page
- [x] Image reading of the colony (optional, text fallback)
- [ ] Rare elite Operative pawn (deferred)
- [ ] More in-game testing across layouts and mod lists
- [ ] Performance and save/load soak tests

## References

- [TypeSafe System One API](https://docs.typesafe.ai/api/system-one)
- [TypeSafe decision primitives](https://docs.typesafe.ai/primitives)
- [RimWorld 1.7 beta announcement](https://ludeon.com/blog/2026/10/announcing-the-waylith-engine-and-update-1-7/)
- [Harmony for RimWorld](https://github.com/pardeike/HarmonyRimWorld)

This is an independent fan-made mod and is not affiliated with Ludeon Studios or TypeSafe AI.
