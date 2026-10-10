using Cloris.Aion2Flow.SceneRuntime.Archive;
using Cloris.Aion2Flow.SceneRuntime.Combat;
using Cloris.Aion2Flow.SceneRuntime.Model;
using Cloris.Aion2Flow.Services;

namespace Cloris.Aion2Flow.ViewModels;

internal sealed record FightSummaryData(
    int LocalPlayerId,
    string ClassKey,
    SceneCombatantMetrics Metrics,
    int Rank,
    int PlayerCount,
    long DurationMilliseconds,
    ComboAnalysisResult Combo);

internal static class FightSummaryAnalysis
{
    public static string ResolveClassKey(CharacterClass? characterClass)
        => characterClass?.ToString() ?? "Unknown";

    public static bool TryAnalyze(ArchivedEncounterRecord record, SceneDisplayContext displayContext, GameResourceService resources, out FightSummaryData summary)
    {
        summary = null!;
        var payload = record.ScenePayload;
        var snapshot = payload.Snapshot;

        var localId = 0;
        SceneCombatantMetrics localMetrics = default;
        var players = new List<(int Id, long Damage)>();
        foreach (var (id, metrics) in snapshot.Combatants)
        {
            if (metrics.IsVisiblePlayerCombatant || displayContext.HasPcMetadata(id))
                players.Add((id, metrics.DamageAmount));
            if (localId == 0 && displayContext.IsLocalPlayer(id))
            {
                localId = id;
                localMetrics = metrics;
            }
        }

        if (localId == 0)
            return false;

        if (players.All(player => player.Id != localId))
            players.Add((localId, localMetrics.DamageAmount));
        var rank = 1 + players.Count(player => player.Id != localId && player.Damage > localMetrics.DamageAmount);

        var detail = payload.CreateDetailDelta(localId);
        var hits = new List<ComboHit>(detail.MetricEvents.Count);
        foreach (var metricEvent in detail.MetricEvents)
        {
            if (metricEvent.SourceId != localId || metricEvent.Metric != CombatMetricKind.Damage)
                continue;
            if (metricEvent.Delivery is not (CombatDeliveryKind.Direct or CombatDeliveryKind.Periodic))
                continue;

            var skillId = ResolveSkillId(metricEvent.EventKey, resources);
            if (skillId <= 0 || skillId == SyntheticCombatSkillCodes.UnresolvedInvincible)
                continue;

            hits.Add(new ComboHit(
                metricEvent.ObservedAtMilliseconds,
                skillId,
                metricEvent.Amount,
                metricEvent.Delivery == CombatDeliveryKind.Periodic));
        }

        var duration = Math.Max(0, snapshot.EncounterEndTime - snapshot.EncounterStartTime);
        if (duration == 0)
            duration = Math.Max(0, snapshot.EncounterTime);

        var characterClass = localMetrics.CharacterClass ?? displayContext.ResolvePcClass(localId);
        summary = new FightSummaryData(
            localId,
            ResolveClassKey(characterClass),
            localMetrics,
            rank,
            players.Count,
            duration,
            ComboAnalyzer.Analyze(hits));
        return true;
    }

    private static int ResolveSkillId(CombatEventKey eventKey, GameResourceService resources)
    {
        var baseKey = SkillBaseKey.FromEventKey(eventKey);
        var skillCode = baseKey.SkillCode > 0 ? baseKey.SkillCode : eventKey.SkillCode;
        if (skillCode <= 0)
            return 0;

        var normalized = resources.ResolveBaseSkillIdForCode(skillCode);
        return normalized > 0 ? normalized : skillCode;
    }
}
