using System.Collections.ObjectModel;
using System.Globalization;
using Cloris.Aion2Flow.Capture;
using Cloris.Aion2Flow.Protocol.Combat;
using Cloris.Aion2Flow.SceneRuntime.Combat;
using Cloris.Aion2Flow.SceneRuntime.Journal;
using Cloris.Aion2Flow.SceneRuntime.Model;
using Cloris.Aion2Flow.SceneRuntime.Observation;
using Cloris.Aion2Flow.SceneRuntime.Stores;
using Cloris.Aion2Flow.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cloris.Aion2Flow.ViewModels;

public sealed record ComboSkillChip(int SkillId, string? IconAssetName, string Name);

public sealed record ComboSuggestionItem(int SkillId, string? IconAssetName, string Name, string Reason, bool IsPrimary);

public sealed record ComboChainItem(IReadOnlyList<ComboSkillChip> Skills, string DamagePerSecondText, string CountText, string ToolTipText);

/// <summary>
/// Live combo helper: watches the local player's skill uses and suggests what to press next,
/// based on the chains that produced the most damage in the player's earlier fights.
/// </summary>
public sealed class ComboAssistantViewModel : ObservableObject, IDisposable
{
    private const int JournalReadBatchSize = 2_048;
    private const int RecentCastCapacity = 6;
    private const long DuplicateCastWindowMilliseconds = 600;
    private const long RefreshIntervalMilliseconds = 200;
    private const long RecentCastExpiryMilliseconds = 8_000;

    private readonly WinDivertCaptureService _captureService;
    private readonly GameResourceService _resources;
    private readonly LocalizationService _localization;
    private readonly ComboProfileStore _profile;
    private readonly PacketCooldownTracker _cooldowns = new();
    private readonly List<AuraInstanceState> _auraScratch = [];
    private readonly List<(int SkillId, long AtMilliseconds)> _recentCasts = [];
    private IReadOnlyList<ComboChainStat> _chains = [];
    private string? _chainsClassKey;
    private bool _chainsDirty = true;
    private Guid _sessionId;
    private JournalCursor _journalCursor;
    private long _lastRefreshAt;
    private int _suggestionSignature;
    private int _recentSignature;
    private int _chainSignature;
    private bool _disposed;

    public ComboAssistantViewModel(
        WinDivertCaptureService captureService,
        GameResourceService resources,
        LocalizationService localization,
        ComboProfileStore profile)
    {
        _captureService = captureService;
        _resources = resources;
        _localization = localization;
        _profile = profile;
        _profile.Changed += OnProfileChanged;
        _localization.LanguageChanged += OnLanguageChanged;
        StatusText = _localization["Combo_NoData"];
    }

    public ObservableCollection<ComboSkillChip> RecentCasts { get; } = [];

    public ObservableCollection<ComboSuggestionItem> Suggestions { get; } = [];

    public ObservableCollection<ComboChainItem> BestChains { get; } = [];

    public string StatusText
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    public bool HasStatus
    {
        get;
        private set => SetProperty(ref field, value);
    } = true;

    public bool HasSuggestions
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public bool HasRecentCasts
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public bool HasBestChains
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public string SuggestionsTitle => _localization["Combo_NextTitle"];

    public string BestChainsTitle => _localization["Combo_BestChainsTitle"];

    public string RecentTitle => _localization["Combo_RecentTitle"];

    public void ProcessUiFrame()
    {
        if (_disposed)
            return;

        var scene = _captureService.Scene;
        if (!_captureService.IsDriverActive)
        {
            ClearLive();
            return;
        }

        _auraScratch.Clear();
        var frame = scene.CopyLocalPlayerAurasTo(_auraScratch);
        EnsureSession(in frame);
        var castsChanged = ReadJournal(scene.Journal, in frame);
        var now = frame.ObservedAtMilliseconds;
        if (!castsChanged && now - _lastRefreshAt < RefreshIntervalMilliseconds && !_chainsDirty)
            return;

        _lastRefreshAt = now;
        ExpireRecentCasts(now);
        RefreshChains();
        RefreshRecentCasts();
        RefreshSuggestions(now);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _profile.Changed -= OnProfileChanged;
        _localization.LanguageChanged -= OnLanguageChanged;
    }

    private void OnProfileChanged(object? sender, EventArgs e) => _chainsDirty = true;

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        _chainsDirty = true;
        _suggestionSignature = 0;
        _recentSignature = 0;
        _chainSignature = 0;
        OnPropertyChanged(nameof(SuggestionsTitle));
        OnPropertyChanged(nameof(BestChainsTitle));
        OnPropertyChanged(nameof(RecentTitle));
    }

    private void ClearLive()
    {
        _recentCasts.Clear();
        _cooldowns.Clear();
        if (RecentCasts.Count != 0)
            RecentCasts.Clear();
        if (Suggestions.Count != 0)
            Suggestions.Clear();
        _recentSignature = 0;
        _suggestionSignature = 0;
        HasRecentCasts = false;
        HasSuggestions = false;
    }

    private void EnsureSession(in SceneLocalPlayerFrame frame)
    {
        if (_sessionId == frame.SessionId)
            return;

        _sessionId = frame.SessionId;
        _cooldowns.Clear();
        _recentCasts.Clear();
        _journalCursor = new JournalCursor(frame.StartObservationOrdinal);
    }

    private bool ReadJournal(ObservedEventJournal journal, in SceneLocalPlayerFrame frame)
    {
        var localPlayerEntityId = frame.EntityId;
        var changed = false;
        while (_journalCursor.NextObservationOrdinal < frame.EndObservationOrdinalExclusive)
        {
            var result = journal.ReadEntries(_journalCursor, frame.EndObservationOrdinalExclusive, JournalReadBatchSize, entries =>
            {
                for (var index = 0; index < entries.Count; index++)
                {
                    var entry = entries[index];
                    if (entry.Domain == ObservedEventDomain.Combat)
                    {
                        var raw = entry.Raw;
                        var combat = entry.Combat;
                        changed |= ObserveSkillPacket(entry.ObservedAtMilliseconds, entry.SourceEntityId, localPlayerEntityId, raw.Opcode, in combat);
                    }
                    else if (entry.Domain == ObservedEventDomain.State && entry.State.StateCode == StateCodes.CooldownStart0238)
                    {
                        ObserveCooldownStart(entry.ObservedAtMilliseconds, entry.SourceEntityId, localPlayerEntityId, entry.State.Value0, entry.State.Value1, entry.State.DetailRaw);
                    }
                    else if (entry.Domain == ObservedEventDomain.State && entry.State.StateCode == StateCodes.Cooldown4738)
                    {
                        ObserveCooldownUpdate(entry.ObservedAtMilliseconds, entry.State.Value0, entry.State.Value1);
                    }
                    else if (entry.Domain == ObservedEventDomain.State && entry.State.StateCode == StateCodes.CooldownCharge2238)
                    {
                        ObserveCooldownCharge(entry.ObservedAtMilliseconds, entry.State.Value0, entry.State.Value1, entry.State.DetailRaw);
                    }
                }
            });

            _journalCursor = result.Cursor;
            if (result.Count == 0)
                break;
        }

        return changed;
    }

    private bool ObserveSkillPacket(long observedAtMilliseconds, int sourceEntityId, int localPlayerEntityId, ushort opcode, in CombatWireObservation combat)
    {
        if (!PacketCooldownSourceFilter.MatchesKnownLocalPlayer(localPlayerEntityId, sourceEntityId) || opcode != 0x0238)
            return false;

        var packetSkillCode = combat.SkillCode > 0
            ? combat.SkillCode
            : combat.BodySkillVariantRaw > 0
                ? combat.BodySkillVariantRaw
                : combat.BodyCodeRaw is > 0 and <= int.MaxValue
                    ? (int)combat.BodyCodeRaw
                    : 0;
        if (!TryNormalizePacketSkillCode(packetSkillCode, out var skillId))
            return false;

        _cooldowns.ObserveControl0238(skillId, packetSkillCode, sourceEntityId, observedAtMilliseconds, _resources.ResolveMaxAvailableCount(packetSkillCode));

        if (_recentCasts.Count > 0)
        {
            var last = _recentCasts[^1];
            if (last.SkillId == skillId && observedAtMilliseconds - last.AtMilliseconds <= DuplicateCastWindowMilliseconds)
            {
                _recentCasts[^1] = (skillId, observedAtMilliseconds);
                return false;
            }
        }

        _recentCasts.Add((skillId, observedAtMilliseconds));
        if (_recentCasts.Count > RecentCastCapacity)
            _recentCasts.RemoveAt(0);
        return true;
    }

    private void ObserveCooldownStart(long observedAtMilliseconds, int sourceEntityId, int localPlayerEntityId, long skillValue, long remainingValue, long detailRaw)
    {
        if (skillValue is <= 0 or > int.MaxValue || remainingValue is <= 0 or > int.MaxValue)
            return;
        if (!CooldownStartObservationDetail.TryDecode(detailRaw, out _, out var availableCountAfterControl))
            return;

        var packetSkillCode = (int)skillValue;
        if (!TryNormalizePacketSkillCode(packetSkillCode, out var skillId))
            return;
        _cooldowns.ObserveStart0238(localPlayerEntityId, sourceEntityId, skillId, packetSkillCode, (int)remainingValue, observedAtMilliseconds, _resources.ResolveMaxAvailableCount(packetSkillCode), availableCountAfterControl);
    }

    private void ObserveCooldownUpdate(long observedAtMilliseconds, long skillValue, long remainingValue)
    {
        if (skillValue is <= 0 or > int.MaxValue || remainingValue is < 0 or > int.MaxValue)
            return;

        var packetSkillCode = (int)skillValue;
        if (!TryNormalizePacketSkillCode(packetSkillCode, out var skillId))
            return;
        _cooldowns.ObserveUpdate4738(skillId, packetSkillCode, (int)remainingValue, observedAtMilliseconds, _resources.ResolveMaxAvailableCount(packetSkillCode));
    }

    private void ObserveCooldownCharge(long observedAtMilliseconds, long skillValue, long remainingValue, long detailRaw)
    {
        if (skillValue is <= 0 or > int.MaxValue || remainingValue is < 0 or > int.MaxValue ||
            !CooldownChargeObservationDetail.TryDecode(detailRaw, out _, out var availableCount))
        {
            return;
        }

        var packetSkillCode = (int)skillValue;
        if (!TryNormalizePacketSkillCode(packetSkillCode, out var skillId))
            return;
        _cooldowns.ObserveCharge2238(skillId, packetSkillCode, availableCount, (int)remainingValue, observedAtMilliseconds);
    }

    private bool TryNormalizePacketSkillCode(int packetSkillCode, out int normalizedSkillId)
    {
        normalizedSkillId = 0;
        if (packetSkillCode <= 0)
            return false;

        var skillId = packetSkillCode;
        if (!_resources.ContainsSkill(packetSkillCode) &&
            CombatResourceRegistry.TryResolveSkillIdByEffectRef(ResourceEffectRef.FromRaw(unchecked((uint)packetSkillCode)), out var effectSkillId))
        {
            skillId = effectSkillId;
        }

        normalizedSkillId = _resources.ResolveBaseSkillIdForCode(skillId);
        return normalizedSkillId > 0;
    }

    private void ExpireRecentCasts(long now)
    {
        // After a long pause the old chain no longer predicts anything; start fresh.
        if (_recentCasts.Count > 0 && now - _recentCasts[^1].AtMilliseconds > RecentCastExpiryMilliseconds)
            _recentCasts.Clear();
    }

    private bool IsReady(int skillId, long now)
    {
        foreach (var state in _cooldowns.States)
        {
            if (state.RowBaseSkillId != skillId)
                continue;
            if (state.AvailableCount is > 0)
                return true;
            return state.ObservedAtMilliseconds + state.RemainingMilliseconds <= now;
        }

        return true;
    }

    private void RefreshChains()
    {
        var classKey = ResolveClassKey();
        if (!_chainsDirty && string.Equals(classKey, _chainsClassKey, StringComparison.Ordinal))
            return;

        _chainsDirty = false;
        _chainsClassKey = classKey;
        _chains = _profile.GetChains(classKey);

        var top = ComboSuggester.TopChains(_chains, 3);
        var signature = 17;
        foreach (var chain in top)
            signature = HashCode.Combine(signature, chain.Key, chain.Count);
        if (signature == _chainSignature && BestChains.Count == top.Count)
            return;

        _chainSignature = signature;
        BestChains.Clear();
        foreach (var chain in top)
            BestChains.Add(CreateChainItem(chain));
        HasBestChains = BestChains.Count > 0;
    }

    // The profile is keyed by class; the class that finished the latest fight is the one being played.
    private string? ResolveClassKey() => _profile.LastClassKey;

    private void RefreshRecentCasts()
    {
        var signature = 31;
        foreach (var cast in _recentCasts)
            signature = HashCode.Combine(signature, cast.SkillId, cast.AtMilliseconds);
        if (signature == _recentSignature)
            return;

        _recentSignature = signature;
        RecentCasts.Clear();
        foreach (var cast in _recentCasts)
            RecentCasts.Add(CreateChip(cast.SkillId));
        HasRecentCasts = RecentCasts.Count > 0;
    }

    private void RefreshSuggestions(long now)
    {
        var recent = _recentCasts.Select(static cast => cast.SkillId).ToArray();
        var suggestions = ComboSuggester.Suggest(recent, _chains, skillId => IsReady(skillId, now));

        var signature = 47;
        foreach (var suggestion in suggestions)
            signature = HashCode.Combine(signature, suggestion.SkillId, suggestion.Match);
        if (signature != _suggestionSignature)
        {
            _suggestionSignature = signature;
            Suggestions.Clear();
            for (var i = 0; i < suggestions.Count; i++)
            {
                var suggestion = suggestions[i];
                Suggestions.Add(new ComboSuggestionItem(
                    suggestion.SkillId,
                    _resources.ResolveSkillIconAssetName(suggestion.SkillId),
                    ResolveSkillName(suggestion.SkillId),
                    DescribeSuggestion(suggestion),
                    i == 0));
            }
        }

        HasSuggestions = Suggestions.Count > 0;
        if (_chains.Count == 0)
            StatusText = _localization["Combo_NoData"];
        else if (Suggestions.Count == 0)
            StatusText = _localization["Combo_NothingReady"];
        else
            StatusText = string.Empty;
        HasStatus = StatusText.Length > 0;
    }

    private string DescribeSuggestion(in ComboSuggestion suggestion)
    {
        var chainText = string.Join(" → ", suggestion.Basis.Skills.Select(ResolveSkillName));
        var dps = FormatCompact(suggestion.Basis.DamagePerSecond);
        return suggestion.Match switch
        {
            ComboSuggestionMatch.CompletesChain => string.Format(CultureInfo.CurrentCulture, _localization["Combo_Reason_Completes"], chainText, dps),
            ComboSuggestionMatch.FollowsLast => string.Format(CultureInfo.CurrentCulture, _localization["Combo_Reason_Follows"], chainText, dps),
            _ => string.Format(CultureInfo.CurrentCulture, _localization["Combo_Reason_Opener"], chainText, dps)
        };
    }

    private ComboChainItem CreateChainItem(ComboChainStat chain)
    {
        var chips = chain.Skills.Select(CreateChip).ToArray();
        var names = string.Join(" → ", chips.Select(static chip => chip.Name));
        var dps = FormatCompact(chain.DamagePerSecond) + "/s";
        var count = "×" + chain.Count.ToString(CultureInfo.InvariantCulture);
        return new ComboChainItem(chips, dps, count, $"{names}\n{dps} · {count}");
    }

    private ComboSkillChip CreateChip(int skillId)
        => new(skillId, _resources.ResolveSkillIconAssetName(skillId), ResolveSkillName(skillId));

    private string ResolveSkillName(int skillId)
    {
        var name = _resources.ResolveSkillName(skillId);
        return string.IsNullOrWhiteSpace(name) || string.Equals(name, skillId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            ? _localization["SkillMonitor_UnknownSkill"]
            : name;
    }

    internal static string FormatCompact(double value)
    {
        var abs = Math.Abs(value);
        return abs switch
        {
            >= 1_000_000_000d => (value / 1_000_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "B",
            >= 1_000_000d => (value / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M",
            >= 1_000d => (value / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K",
            _ => value.ToString("0", CultureInfo.InvariantCulture)
        };
    }
}
