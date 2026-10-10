using Avalonia.Threading;
using Cloris.Aion2Flow.SceneRuntime.Archive;
using Cloris.Aion2Flow.SceneRuntime.Identity;
using Cloris.Aion2Flow.Services.Logging;
using Cloris.Aion2Flow.ViewModels;

namespace Cloris.Aion2Flow.Services;

/// <summary>
/// Watches finished fights: keeps the latest one ready for the summary window and
/// feeds the local player's skill chains into the combo profile.
/// </summary>
public sealed class FightSummaryService : IDisposable
{
    /// <summary>Fights with fewer casts than this teach nothing useful about combos.</summary>
    private const int MinimumCastsToLearn = 6;

    private readonly EncounterArchiveService _archive;
    private readonly ComboProfileStore _profile;
    private readonly GameResourceService _resources;
    private readonly HashSet<Guid> _seen = [];
    private bool _disposed;

    public FightSummaryService(EncounterArchiveService archive, ComboProfileStore profile, GameResourceService resources)
    {
        _archive = archive;
        _profile = profile;
        _resources = resources;
        foreach (var record in _archive.History)
            _seen.Add(record.Id);
        _archive.HistoryChanged += OnHistoryChanged;
    }

    public event EventHandler? LatestChanged;

    public ArchivedEncounterRecord? Latest { get; private set; }

    /// <summary>True until the person opens the summary of the latest fight.</summary>
    public bool IsLatestUnseen { get; private set; }

    public void MarkLatestSeen()
    {
        if (!IsLatestUnseen)
            return;

        IsLatestUnseen = false;
        LatestChanged?.Invoke(this, EventArgs.Empty);
    }

    public SceneDisplayContext CreateDisplayContext(ArchivedEncounterRecord record)
        => new(record.ScenePayload.IdentityScope, null, record.ScenePayload.Snapshot, _resources, string.Empty);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _archive.HistoryChanged -= OnHistoryChanged;
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(ProcessHistory);

    private void ProcessHistory()
    {
        if (_disposed)
            return;

        ArchivedEncounterRecord? newest = null;
        var fresh = new List<ArchivedEncounterRecord>();
        foreach (var record in _archive.History)
        {
            if (!_seen.Add(record.Id))
                continue;

            newest ??= record;
            fresh.Add(record);
        }

        // Oldest first, so the profile's "last class" ends up as the newest fight's class.
        for (var i = fresh.Count - 1; i >= 0; i--)
            Learn(fresh[i]);

        if (newest is null)
            return;

        Latest = newest;
        IsLatestUnseen = true;
        LatestChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Learn(ArchivedEncounterRecord record)
    {
        try
        {
            if (!FightSummaryAnalysis.TryAnalyze(record, CreateDisplayContext(record), _resources, out var summary))
                return;
            if (summary.Combo.Casts.Count < MinimumCastsToLearn)
                return;

            _profile.Record(summary.ClassKey, summary.Combo);
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warning, $"Failed to learn combos from encounter {record.Id}: {ex}");
        }
    }
}
