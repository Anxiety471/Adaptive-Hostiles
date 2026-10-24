# Adaptive Hostiles

**Jev-assisted hostile raids and elite pawn tactics for RimWorld.**

> **Status: initial codebase / pre-alpha.** The TypeSafe API client, validated decision planners, deterministic fallbacks, unit-test harness, and RimWorld mod bootstrap are present. The game-independent core (roster generator, candidate enumeration, order revalidation, operative registry, async scheduler) is also implemented and tested. The RimWorld-facing hooks (raid interception, map sampling, job assignment, save/load) and in-game testing are **not yet implemented**. Hostile-raid composition hooks now exist in the Mod project but are **unverified in game** (no RimWorld references were available when written); map sampling and Operative job control are still missing.

## Design

Adaptive Hostiles is **not a storyteller**. Cassandra, Phoebe, Randy, and modded storytellers continue deciding *when* raid incidents occur. The planned integration replaces *how hostile humanlike raid forces are composed*, without relying on raid points, and occasionally adds a Jev-controlled elite **Operative**.

The Operative's movement decisions belong to Jev. RimWorld's native job and pathfinding systems only execute a **destination cell selected by Jev** from reachable cells supplied by the game. Jev also selects targets and a tactical stance; it does not emit arbitrary code or pathfinding instructions.

| System | Role |
| --- | --- |
| Vanilla/modded storyteller | Incident timing and scheduling, unchanged |
| Raid adapter *(planned)* | Intercept hostile raids and create a faction-compatible roster without raid-point budgets |
| Jev raid planner *(implemented)* | Choose raid scale and approach from structured colony state |
| Elite Operative adapter *(planned)* | Spawn, equip, and track an uncommon elite pawn |
| Jev tactical planner *(implemented)* | Choose an exact movement candidate, target, and stance from legal options |
| RimWorld jobs *(planned)* | Execute the chosen destination and actions, preserving native combat/pathfinding |

See [SPECS.md](SPECS.md) for behavior, interfaces, guardrails, and acceptance tests.

## Current repository structure

```text
About/About.xml                 RimWorld metadata and Harmony dependency
LoadFolders.xml                 RimWorld 1.7 assembly folder selection
Source/AdaptiveHostiles.Core/  API client, decision models, planners and fallbacks
Source/AdaptiveHostiles.Mod/   RimWorld 1.7 bootstrap (no gameplay hooks yet)
Tests/AdaptiveHostiles.Tests/  Dependency-free planner and client tests
1.7/Assemblies/                Build output (ignored by git)
```

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

For development, set `TYPESAFE_API_KEY` in your environment. The core client sends requests to TypeSafe's official `https://api.typesafe.ai/v1/systemone` endpoint with model `jev-latest`. Other decision models are selectable with `ADAPTIVE_HOSTILES_PROVIDER` (`jev`, `anthropic`, `openai`, `local`; default `auto` picks the first of `TYPESAFE_API_KEY`, `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`), with `ADAPTIVE_HOSTILES_MODEL` and `ADAPTIVE_HOSTILES_ENDPOINT` (any OpenAI-compatible server, e.g. a local Ollama) as overrides. **Never commit an API key.** The game-facing settings and background request scheduler have not yet been connected.

## Principles

1. **No storyteller replacement** and no change to raid scheduling.
2. **No raid points in our hostile raid composition algorithm**. Some other game systems still use vanilla points, and we will not corrupt those globally.
3. **Jev chooses the Operative's actual destinations** from valid, reachable candidates; it cannot teleport, navigate through walls, or fabricate a map coordinate.
4. **Nonblocking and recoverable.** Network calls must not run on the RimWorld main thread. A failed/missing/stale decision falls back to bounded local logic.
5. **Faction and mod compatibility first.** Elite equipment should be appropriate to its faction; no blanket changes to friendly raids, quests, traders, or unrelated incidents.

## Roadmap

- [x] Document technical scope and acceptance criteria
- [x] Set up .NET 10 projects, RimWorld mod manifest, and test harness
- [x] Implement Jev request/response validation and decisions from candidate lists
- [ ] Verify assembly references against an installed RimWorld 1.7 beta
- [ ] Add on-game-thread colony snapshot and background/cached Jev raid evaluation
- [x] Raid history: outcomes recorded per faction, saved with the game, fed into planning *(unverified in game)*
- [x] Replace **hostile raid composition** without consuming raid points *(hooks written, not yet verified in game)*
- [ ] Generate faction-appropriate rare Operatives and persist their identities
- [ ] Observe tactical map, enumerate reachable cells, and asynchronously ask Jev
- [ ] Convert Jev-selected cells/targets into RimWorld jobs with safe reevaluation
- [ ] Add settings, compatibility checks, dev-mode tools, performance and save/load tests

## References

- [TypeSafe System One API](https://docs.typesafe.ai/api/system-one)
- [TypeSafe decision primitives](https://docs.typesafe.ai/primitives)
- [RimWorld 1.7 beta announcement](https://ludeon.com/blog/2026/10/announcing-the-waylith-engine-and-update-1-7/)
- [Harmony for RimWorld](https://github.com/pardeike/HarmonyRimWorld)

This is an independent fan-made mod and is not affiliated with Ludeon Studios or TypeSafe AI.
