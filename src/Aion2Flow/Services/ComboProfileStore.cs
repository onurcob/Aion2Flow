using System.Text.Json;
using System.Text.Json.Serialization;
using Cloris.Aion2Flow.Services.Logging;
using Cloris.Aion2Flow.ViewModels;

namespace Cloris.Aion2Flow.Services;

/// <summary>
/// Remembers which skill chains the local player used and how much damage they produced,
/// per class and across fights, so the combo helper can suggest proven sequences.
/// </summary>
public sealed class ComboProfileStore
{
    private const string FileName = "combo-profile.json";
    private const int MaxChainsPerClass = 300;
    private readonly Lock _lock = new();
    private readonly string _filePath;
    private ComboProfileDocument _document;

    public ComboProfileStore() : this(Path.Combine(WorkingDirectoryResolver.GetWorkingDirectory(), FileName))
    {
    }

    public ComboProfileStore(string filePath)
    {
        _filePath = filePath;
        _document = Load(filePath);
    }

    public event EventHandler? Changed;

    public string? LastClassKey
    {
        get
        {
            lock (_lock)
                return _document.LastClass;
        }
    }

    internal void Record(string classKey, ComboAnalysisResult analysis)
    {
        if (string.IsNullOrWhiteSpace(classKey) || analysis.Chains.Count == 0)
            return;

        ComboProfileDocument snapshot;
        lock (_lock)
        {
            if (!_document.Classes.TryGetValue(classKey, out var profile))
            {
                profile = new ComboClassProfile();
                _document.Classes[classKey] = profile;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var chain in analysis.Chains)
            {
                if (!profile.Chains.TryGetValue(chain.Key, out var record))
                {
                    record = new ComboChainRecord();
                    profile.Chains[chain.Key] = record;
                }

                record.Count += chain.Count;
                record.Damage += chain.TotalDamage;
                record.DurationMilliseconds += chain.TotalDurationMilliseconds;
                record.LastSeenUnixMilliseconds = now;
            }

            profile.Encounters++;
            Prune(profile);
            _document.LastClass = classKey;
            snapshot = _document;
            Save(snapshot);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal IReadOnlyList<ComboChainStat> GetChains(string? classKey)
    {
        if (string.IsNullOrWhiteSpace(classKey))
            return [];

        lock (_lock)
        {
            if (!_document.Classes.TryGetValue(classKey, out var profile))
                return [];

            var chains = new List<ComboChainStat>(profile.Chains.Count);
            foreach (var (key, record) in profile.Chains)
            {
                if (ComboAnalyzer.TryParseKey(key, out var skills))
                    chains.Add(new ComboChainStat(skills, record.Count, record.Damage, record.DurationMilliseconds));
            }

            return chains;
        }
    }

    internal int GetEncounterCount(string? classKey)
    {
        if (string.IsNullOrWhiteSpace(classKey))
            return 0;

        lock (_lock)
            return _document.Classes.TryGetValue(classKey, out var profile) ? profile.Encounters : 0;
    }

    public void Clear(string? classKey)
    {
        lock (_lock)
        {
            if (classKey is null)
                _document.Classes.Clear();
            else
                _document.Classes.Remove(classKey);
            Save(_document);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void Prune(ComboClassProfile profile)
    {
        if (profile.Chains.Count <= MaxChainsPerClass)
            return;

        var keep = profile.Chains
            .OrderByDescending(static pair => pair.Value.Count)
            .ThenByDescending(static pair => pair.Value.LastSeenUnixMilliseconds)
            .Take(MaxChainsPerClass)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        profile.Chains = keep;
    }

    private static ComboProfileDocument Load(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return new ComboProfileDocument();

            using var stream = File.OpenRead(filePath);
            return JsonSerializer.Deserialize(stream, ComboProfileJsonContext.Default.ComboProfileDocument) ?? new ComboProfileDocument();
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warning, $"Failed to load combo profile from '{filePath}': {ex}");
            return new ComboProfileDocument();
        }
    }

    private void Save(ComboProfileDocument document)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var tempPath = _filePath + ".tmp";
            using (var stream = File.Create(tempPath))
                JsonSerializer.Serialize(stream, document, ComboProfileJsonContext.Default.ComboProfileDocument);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warning, $"Failed to save combo profile to '{_filePath}': {ex}");
        }
    }
}

public sealed class ComboProfileDocument
{
    public string? LastClass { get; set; }

    public Dictionary<string, ComboClassProfile> Classes { get; set => field = value ?? []; } = [];
}

public sealed class ComboClassProfile
{
    public int Encounters { get; set; }

    public Dictionary<string, ComboChainRecord> Chains { get; set => field = value ?? []; } = [];
}

public sealed class ComboChainRecord
{
    public int Count { get; set; }

    public long Damage { get; set; }

    public long DurationMilliseconds { get; set; }

    public long LastSeenUnixMilliseconds { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ComboProfileDocument))]
internal sealed partial class ComboProfileJsonContext : JsonSerializerContext
{
}
