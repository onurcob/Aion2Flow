namespace Cloris.Aion2Flow.ViewModels;

/// <summary>One damage event of the analysed player: a direct hit or a periodic tick.</summary>
internal readonly record struct ComboHit(long AtMilliseconds, int SkillId, long Damage, bool IsPeriodic);

/// <summary>One use of a skill, built by merging the hits it produced.</summary>
internal readonly record struct ComboCast(long AtMilliseconds, int SkillId, long Damage);

/// <summary>A skill sequence and how much damage per second it produced while it was being used.</summary>
internal sealed record ComboChainStat(int[] Skills, int Count, long TotalDamage, long TotalDurationMilliseconds)
{
    public string Key => ComboAnalyzer.CreateKey(Skills);

    public double DamagePerSecond => TotalDurationMilliseconds <= 0
        ? 0d
        : TotalDamage * 1000d / TotalDurationMilliseconds;
}

internal sealed record ComboAnalysisResult(
    IReadOnlyList<ComboCast> Casts,
    IReadOnlyList<ComboChainStat> Chains,
    long ActiveMilliseconds,
    int IdleGapCount,
    long IdleMilliseconds,
    long LongestIdleGapMilliseconds,
    double CastsPerMinute)
{
    public static ComboAnalysisResult Empty { get; } = new([], [], 0, 0, 0, 0, 0d);
}

internal static class ComboAnalyzer
{
    /// <summary>Hits of the same skill closer than this belong to one cast (multi-hit skills).</summary>
    public const long MergeWindowMilliseconds = 700;

    /// <summary>A gap longer than this ends a combat streak; chains never span it.</summary>
    public const long StreakBreakMilliseconds = 4_000;

    /// <summary>A gap longer than this inside a streak counts as idle time.</summary>
    public const long IdleThresholdMilliseconds = 2_000;

    public const int DefaultChainLength = 3;

    public static string CreateKey(IReadOnlyList<int> skills) => string.Join(',', skills);

    public static bool TryParseKey(string key, out int[] skills)
    {
        var parts = key.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        skills = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) || value <= 0)
            {
                skills = [];
                return false;
            }

            skills[i] = value;
        }

        return skills.Length > 0;
    }

    public static List<ComboCast> BuildCasts(IEnumerable<ComboHit> hits)
    {
        var ordered = hits
            .Where(static hit => hit.SkillId > 0 && hit.AtMilliseconds >= 0)
            .OrderBy(static hit => hit.AtMilliseconds)
            .ToList();

        var casts = new List<ComboCast>();
        var lastHitAt = new List<long>();
        var lastCastIndexBySkill = new Dictionary<int, int>();

        foreach (var hit in ordered)
        {
            if (hit.IsPeriodic)
            {
                // Ticks belong to the latest cast of their skill and never start a new one.
                if (lastCastIndexBySkill.TryGetValue(hit.SkillId, out var owner))
                    casts[owner] = casts[owner] with { Damage = casts[owner].Damage + Math.Max(0, hit.Damage) };
                continue;
            }

            var lastIndex = casts.Count - 1;
            if (lastIndex >= 0 &&
                casts[lastIndex].SkillId == hit.SkillId &&
                hit.AtMilliseconds - lastHitAt[lastIndex] <= MergeWindowMilliseconds)
            {
                casts[lastIndex] = casts[lastIndex] with { Damage = casts[lastIndex].Damage + Math.Max(0, hit.Damage) };
                lastHitAt[lastIndex] = hit.AtMilliseconds;
                continue;
            }

            casts.Add(new ComboCast(hit.AtMilliseconds, hit.SkillId, Math.Max(0, hit.Damage)));
            lastHitAt.Add(hit.AtMilliseconds);
            lastCastIndexBySkill[hit.SkillId] = casts.Count - 1;
        }

        return casts;
    }

    public static ComboAnalysisResult Analyze(IEnumerable<ComboHit> hits, int chainLength = DefaultChainLength)
        => AnalyzeCasts(BuildCasts(hits), chainLength);

    public static ComboAnalysisResult AnalyzeCasts(IReadOnlyList<ComboCast> casts, int chainLength = DefaultChainLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chainLength, 2);
        if (casts.Count == 0)
            return ComboAnalysisResult.Empty;

        long activeMilliseconds = 0;
        long idleMilliseconds = 0;
        long longestIdle = 0;
        var idleCount = 0;
        var gaps = new List<long>(Math.Max(0, casts.Count - 1));
        for (var i = 1; i < casts.Count; i++)
        {
            var gap = casts[i].AtMilliseconds - casts[i - 1].AtMilliseconds;
            if (gap > StreakBreakMilliseconds)
                continue;

            activeMilliseconds += gap;
            gaps.Add(gap);
            if (gap > IdleThresholdMilliseconds)
            {
                idleCount++;
                idleMilliseconds += gap;
                longestIdle = Math.Max(longestIdle, gap);
            }
        }

        var typicalGap = Median(gaps, fallback: 1_000);
        var aggregates = new Dictionary<string, (int[] Skills, int Count, long Damage, long Duration)>(StringComparer.Ordinal);
        for (var start = 0; start + chainLength <= casts.Count; start++)
        {
            if (!TryMeasureChain(casts, start, chainLength, typicalGap, out var skills, out var damage, out var duration))
                continue;

            var key = CreateKey(skills);
            aggregates[key] = aggregates.TryGetValue(key, out var existing)
                ? (existing.Skills, existing.Count + 1, existing.Damage + damage, existing.Duration + duration)
                : (skills, 1, damage, duration);
        }

        var chains = aggregates.Values
            .Select(static value => new ComboChainStat(value.Skills, value.Count, value.Damage, value.Duration))
            .OrderByDescending(static chain => chain.Count >= 2)
            .ThenByDescending(static chain => chain.DamagePerSecond)
            .ThenByDescending(static chain => chain.Count)
            .ToList();

        var castsPerMinute = activeMilliseconds > 0 ? casts.Count * 60_000d / activeMilliseconds : 0d;
        return new ComboAnalysisResult(casts, chains, activeMilliseconds, idleCount, idleMilliseconds, longestIdle, castsPerMinute);
    }

    private static bool TryMeasureChain(
        IReadOnlyList<ComboCast> casts,
        int start,
        int length,
        long typicalGap,
        out int[] skills,
        out long damage,
        out long duration)
    {
        skills = new int[length];
        damage = 0;
        duration = 0;
        for (var offset = 0; offset < length; offset++)
        {
            var cast = casts[start + offset];
            if (offset > 0)
            {
                // Spamming one skill is not a combo, and a break in combat ends the chain.
                if (cast.SkillId == skills[offset - 1] ||
                    cast.AtMilliseconds - casts[start + offset - 1].AtMilliseconds > StreakBreakMilliseconds)
                {
                    return false;
                }
            }

            skills[offset] = cast.SkillId;
            damage += cast.Damage;
        }

        var last = casts[start + length - 1];
        var nextIndex = start + length;
        var tail = nextIndex < casts.Count && casts[nextIndex].AtMilliseconds - last.AtMilliseconds <= StreakBreakMilliseconds
            ? casts[nextIndex].AtMilliseconds - last.AtMilliseconds
            : typicalGap;
        duration = last.AtMilliseconds - casts[start].AtMilliseconds + Math.Max(1, tail);
        return true;
    }

    private static long Median(List<long> values, long fallback)
    {
        if (values.Count == 0)
            return fallback;

        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }
}
