using AdaptiveHostiles.Core;
using Verse;

namespace AdaptiveHostiles.Mod.Raids;

/// <summary>Save-game holder for Operative records. Persists no tasks, credentials or paths.</summary>
public sealed class AdaptiveHostilesComponent : GameComponent
{
    public OperativeRegistry Registry { get; private set; } = new();

    public AdaptiveHostilesComponent(Game game) { }

    public static AdaptiveHostilesComponent? Instance => Current.Game?.GetComponent<AdaptiveHostilesComponent>();

    public override void LoadedGame() => JevPlanService.Reset();

    public override void StartedNewGame() => JevPlanService.Reset();

    public override void ExposeData()
    {
        var saved = Scribe.mode == LoadSaveMode.Saving
            ? Registry.All.Select(r => new OperativeSave(r)).ToList()
            : new List<OperativeSave>();
        Scribe_Collections.Look(ref saved, "operatives", LookMode.Deep);

        if (Scribe.mode == LoadSaveMode.LoadingVars)
        {
            Registry = new OperativeRegistry();
            foreach (var s in saved ?? new List<OperativeSave>())
                s.Restore(Registry);
        }
    }

    private sealed class OperativeSave : IExposable
    {
        private string pawnId = "";
        private string factionDefName = "";
        private long nextDecisionTick;
        private long commitUntilTick;
        private List<LogSave> log = new();

        public OperativeSave() { }

        public OperativeSave(OperativeRecord r)
        {
            pawnId = r.PawnId;
            factionDefName = r.FactionDefName;
            nextDecisionTick = r.NextDecisionTick;
            commitUntilTick = r.CommitUntilTick;
            log = r.Log.Select(e => new LogSave(e)).ToList();
        }

        public void Restore(OperativeRegistry registry)
        {
            if (pawnId.NullOrEmpty()) return;
            var record = registry.Register(pawnId, factionDefName);
            record.NextDecisionTick = nextDecisionTick;
            record.CommitUntilTick = commitUntilTick;
            record.Log = log.Select(l => l.ToEntry()).ToList();
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref pawnId, "pawnId", "");
            Scribe_Values.Look(ref factionDefName, "faction", "");
            Scribe_Values.Look(ref nextDecisionTick, "next");
            Scribe_Values.Look(ref commitUntilTick, "commit");
            Scribe_Collections.Look(ref log, "log", LookMode.Deep);
            log ??= new List<LogSave>();
        }
    }

    private sealed class LogSave : IExposable
    {
        private long tick;
        private string destinationId = "";
        private int x, z;
        private string? targetId;
        private TacticalStance stance;
        private bool isModel;

        public LogSave() { }

        public LogSave(DecisionLogEntry e)
        {
            tick = e.Tick; destinationId = e.DestinationId; x = e.X; z = e.Z;
            targetId = e.TargetId; stance = e.Stance; isModel = e.IsModelDecision;
        }

        public DecisionLogEntry ToEntry() => new(tick, destinationId, x, z, targetId, stance, isModel);

        public void ExposeData()
        {
            Scribe_Values.Look(ref tick, "tick");
            Scribe_Values.Look(ref destinationId, "dest", "");
            Scribe_Values.Look(ref x, "x");
            Scribe_Values.Look(ref z, "z");
            Scribe_Values.Look(ref targetId, "target");
            Scribe_Values.Look(ref stance, "stance");
            Scribe_Values.Look(ref isModel, "model");
        }
    }
}
