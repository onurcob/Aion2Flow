using System.Globalization;
using Cloris.Aion2Flow.SceneRuntime.Archive;
using Cloris.Aion2Flow.Services;

namespace Cloris.Aion2Flow.ViewModels;

public sealed record FightSummaryTile(string Label, string Value, bool IsHighlight = false);

public sealed record FightSummaryChainItem(IReadOnlyList<ComboSkillChip> Skills, string DamagePerSecondText, string CountText, string VersusAverageText, bool IsAboveAverage, string ToolTipText);

/// <summary>What the fight summary window shows: headline numbers, rotation flow, best chains and the skill table.</summary>
public sealed class FightSummaryViewModel : IDisposable
{
    private readonly GameResourceService _resources;
    private readonly LocalizationService _localization;

    public FightSummaryViewModel(
        ArchivedEncounterRecord record,
        SceneDisplayContext displayContext,
        GameResourceService resources,
        LocalizationService localization,
        UiFrameBatchService frameBatchService)
    {
        _resources = resources;
        _localization = localization;
        Title = localization["Summary_Title"];
        SceneName = displayContext.ResolveSceneName(record.ScenePayload.Kind, record.ScenePayload.Snapshot.MapId, record.ScenePayload.BossNpcCodes);
        if (string.IsNullOrWhiteSpace(SceneName))
            SceneName = localization["Scene_Unknown"];
        TimeText = record.ArchivedAt.ToString("HH:mm", CultureInfo.InvariantCulture);
        Details = new CombatantDetailsFlyoutViewModel(localization, frameBatchService)
        {
            DisplayContext = displayContext
        };

        if (!FightSummaryAnalysis.TryAnalyze(record, displayContext, resources, out var summary))
        {
            HasLocalPlayer = false;
            EmptyText = localization["Summary_NoLocalPlayer"];
            return;
        }

        HasLocalPlayer = true;
        LocalPlayerId = summary.LocalPlayerId;
        var snapshot = record.ScenePayload.Snapshot;
        Details.SelectSceneEncounterCombatant(snapshot.EncounterId, summary.LocalPlayerId, snapshot, record.ScenePayload.CreateDetailDelta(summary.LocalPlayerId), forceRefresh: true);

        var metrics = summary.Metrics;
        var fightDps = metrics.DamagePerSecond > 0
            ? metrics.DamagePerSecond
            : summary.DurationMilliseconds > 0 ? metrics.DamageAmount * 1000d / summary.DurationMilliseconds : 0d;
        Tiles =
        [
            new FightSummaryTile(localization["Summary_Dps"], ComboAssistantViewModel.FormatCompact(fightDps) + "/s", IsHighlight: true),
            new FightSummaryTile(localization["Summary_TotalDamage"], ComboAssistantViewModel.FormatCompact(metrics.DamageAmount)),
            new FightSummaryTile(localization["Summary_Duration"], FormatDuration(summary.DurationMilliseconds)),
            new FightSummaryTile(localization["Summary_Rank"], summary.PlayerCount > 1
                ? $"{summary.Rank}/{summary.PlayerCount}"
                : summary.Rank.ToString(CultureInfo.InvariantCulture)),
            new FightSummaryTile(localization["Summary_Share"], metrics.DamageContribution.ToString("P0", CultureInfo.CurrentCulture)),
            new FightSummaryTile(localization["Stat_Critical"], metrics.CriticalRate.ToString("P0", CultureInfo.CurrentCulture)),
            new FightSummaryTile(localization["Stat_Back"], metrics.BackRate.ToString("P0", CultureInfo.CurrentCulture)),
            new FightSummaryTile(localization["Summary_CastsPerMinute"], summary.Combo.CastsPerMinute.ToString("0.#", CultureInfo.CurrentCulture)),
        ];

        var combo = summary.Combo;
        FlowText = combo.IdleGapCount == 0
            ? localization["Summary_NoIdle"]
            : string.Format(
                CultureInfo.CurrentCulture,
                localization["Summary_IdleFormat"],
                combo.IdleGapCount,
                FormatSeconds(combo.IdleMilliseconds),
                FormatSeconds(combo.LongestIdleGapMilliseconds));
        HasIdleWarning = combo.IdleGapCount > 0;

        var chains = ComboSuggester.TopChains(combo.Chains, 3);
        if (chains.Count == 0)
            chains = ComboSuggester.TopChains(combo.Chains, 3, minimumCount: 1);
        Chains = chains.Select(chain => CreateChainItem(chain, fightDps)).ToArray();
        HasChains = Chains.Count > 0;
    }

    public string Title { get; }

    public string SceneName { get; }

    public string TimeText { get; }

    public bool HasLocalPlayer { get; }

    public int LocalPlayerId { get; }

    public string EmptyText { get; } = string.Empty;

    public IReadOnlyList<FightSummaryTile> Tiles { get; } = [];

    public string FlowText { get; } = string.Empty;

    public bool HasIdleWarning { get; }

    public IReadOnlyList<FightSummaryChainItem> Chains { get; } = [];

    public bool HasChains { get; }

    public bool HasNoChains => HasLocalPlayer && !HasChains;

    public CombatantDetailsFlyoutViewModel Details { get; }

    public string CloseText => _localization["Summary_Close"];

    public string FlowTitle => _localization["Summary_FlowTitle"];

    public string ComboTitle => _localization["Summary_ComboTitle"];

    public string SkillsTitle => _localization["Summary_SkillsTitle"];

    public string NoChainsText => _localization["Summary_NoChains"];

    public void Dispose() => Details.Dispose();

    private FightSummaryChainItem CreateChainItem(ComboChainStat chain, double fightDps)
    {
        var chips = chain.Skills.Select(CreateChip).ToArray();
        var dps = ComboAssistantViewModel.FormatCompact(chain.DamagePerSecond) + "/s";
        var count = string.Format(CultureInfo.CurrentCulture, _localization["Summary_TimesFormat"], chain.Count);
        var delta = fightDps > 0 ? chain.DamagePerSecond / fightDps - 1d : 0d;
        var deltaText = (delta >= 0 ? "+" : string.Empty) + delta.ToString("P0", CultureInfo.CurrentCulture);
        var versus = fightDps > 0
            ? string.Format(CultureInfo.CurrentCulture, _localization["Summary_ChainVsAverageFormat"], deltaText)
            : string.Empty;
        var names = string.Join(" → ", chips.Select(static chip => chip.Name));
        return new FightSummaryChainItem(chips, dps, count, versus, delta >= 0, $"{names}\n{dps} · {count}");
    }

    private ComboSkillChip CreateChip(int skillId)
    {
        var name = _resources.ResolveSkillName(skillId);
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, skillId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            name = _localization["SkillMonitor_UnknownSkill"];
        return new ComboSkillChip(skillId, _resources.ResolveSkillIconAssetName(skillId), name);
    }

    private static string FormatDuration(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatSeconds(long milliseconds)
        => (milliseconds / 1000d).ToString("0.0", CultureInfo.CurrentCulture) + " s";
}
