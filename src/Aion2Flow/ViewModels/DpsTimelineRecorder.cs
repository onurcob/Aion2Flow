namespace Cloris.Aion2Flow.ViewModels;

/// <summary>
/// Records cumulative damage per combatant in one-second buckets for each encounter and
/// turns it into a rolling-window DPS series for the overlay chart.
/// Encounters are kept (bounded) so archived encounters from this session still have a chart.
/// </summary>
public sealed class DpsTimelineRecorder
{
    public const int DefaultMaxEncounters = 12;
    public const int DefaultWindowSeconds = 5;
    public const int MaxSeconds = 60 * 60;

    private readonly int _maxEncounters;
    private readonly Dictionary<Guid, EncounterTimeline> _encounters = [];
    private readonly LinkedList<Guid> _encounterOrder = [];

    public DpsTimelineRecorder(int maxEncounters = DefaultMaxEncounters)
    {
        _maxEncounters = Math.Max(1, maxEncounters);
    }

    /// <summary>Records the cumulative damage of a combatant at the given encounter time.</summary>
    public void Record(Guid encounterId, int combatantId, long encounterTimeMilliseconds, double cumulativeDamage)
    {
        if (encounterId == Guid.Empty || combatantId <= 0 || encounterTimeMilliseconds < 0)
            return;

        var second = (int)Math.Min(MaxSeconds, encounterTimeMilliseconds / 1000);
        var timeline = GetOrAddEncounter(encounterId);
        if (!timeline.Combatants.TryGetValue(combatantId, out var samples))
        {
            samples = [];
            timeline.Combatants.Add(combatantId, samples);
        }

        // Fill gaps with the last known cumulative value so the series stays continuous.
        var last = samples.Count > 0 ? samples[^1] : 0d;
        while (samples.Count <= second)
            samples.Add(last);

        // Cumulative damage never decreases; ignore out-of-order samples that would.
        var value = Math.Max(cumulativeDamage, samples[second]);
        for (var i = second; i < samples.Count; i++)
            samples[i] = Math.Max(samples[i], value);

        timeline.LastSecond = Math.Max(timeline.LastSecond, second);
    }

    public bool HasEncounter(Guid encounterId) => _encounters.ContainsKey(encounterId);

    /// <summary>
    /// Returns rolling DPS values (one per second, 0..last second) for a combatant, or an empty array.
    /// </summary>
    public double[] GetDpsSeries(Guid encounterId, int combatantId, int windowSeconds = DefaultWindowSeconds)
    {
        if (!_encounters.TryGetValue(encounterId, out var timeline) ||
            !timeline.Combatants.TryGetValue(combatantId, out var samples) ||
            samples.Count == 0)
        {
            return [];
        }

        var window = Math.Max(1, windowSeconds);
        var length = timeline.LastSecond + 1;
        var result = new double[length];
        for (var t = 0; t < length; t++)
        {
            var current = SampleAt(samples, t);
            var start = Math.Max(0, t - window);
            var previous = t - window >= 0 ? SampleAt(samples, start) : 0d;
            var span = Math.Max(1, t - start + (t - window >= 0 ? 0 : 1));
            result[t] = Math.Max(0d, (current - previous) / span);
        }

        return result;
    }

    public void Clear()
    {
        _encounters.Clear();
        _encounterOrder.Clear();
    }

    private static double SampleAt(List<double> samples, int second) =>
        second < samples.Count ? samples[second] : samples[^1];

    private EncounterTimeline GetOrAddEncounter(Guid encounterId)
    {
        if (_encounters.TryGetValue(encounterId, out var existing))
            return existing;

        var timeline = new EncounterTimeline();
        _encounters.Add(encounterId, timeline);
        _encounterOrder.AddLast(encounterId);
        while (_encounterOrder.Count > _maxEncounters && _encounterOrder.First is { } oldest)
        {
            _encounters.Remove(oldest.Value);
            _encounterOrder.RemoveFirst();
        }

        return timeline;
    }

    private sealed class EncounterTimeline
    {
        public Dictionary<int, List<double>> Combatants { get; } = [];
        public int LastSecond { get; set; }
    }
}
