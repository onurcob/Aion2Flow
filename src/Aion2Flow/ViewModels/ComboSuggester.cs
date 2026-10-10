namespace Cloris.Aion2Flow.ViewModels;

/// <summary>How a suggestion relates to what the player just did.</summary>
internal enum ComboSuggestionMatch : byte
{
    /// <summary>Starts one of the player's best chains.</summary>
    Opener = 0,

    /// <summary>Follows the last skill the way a learned chain did.</summary>
    FollowsLast = 1,

    /// <summary>Completes a learned chain whose first two skills were just used.</summary>
    CompletesChain = 2
}

internal readonly record struct ComboSuggestion(int SkillId, ComboSuggestionMatch Match, ComboChainStat Basis, double Score);

internal static class ComboSuggester
{
    /// <summary>Chains seen fewer times than this are treated as noise.</summary>
    public const int MinimumChainCount = 2;

    public static List<ComboSuggestion> Suggest(
        IReadOnlyList<int> recentSkills,
        IReadOnlyList<ComboChainStat> chains,
        Func<int, bool> isReady,
        int maxSuggestions = 3)
    {
        var best = new Dictionary<int, ComboSuggestion>();
        var last = recentSkills.Count > 0 ? recentSkills[^1] : 0;
        var beforeLast = recentSkills.Count > 1 ? recentSkills[^2] : 0;

        foreach (var chain in chains)
        {
            if (chain.Count < MinimumChainCount || chain.Skills.Length < 2 || chain.DamagePerSecond <= 0)
                continue;

            var weight = chain.DamagePerSecond * (1d + Math.Log(chain.Count));
            if (chain.Skills.Length >= 3 && beforeLast > 0 && chain.Skills[0] == beforeLast && chain.Skills[1] == last)
                Offer(best, new ComboSuggestion(chain.Skills[2], ComboSuggestionMatch.CompletesChain, chain, weight), isReady);
            if (last > 0 && chain.Skills[0] == last)
                Offer(best, new ComboSuggestion(chain.Skills[1], ComboSuggestionMatch.FollowsLast, chain, weight), isReady);
        }

        if (best.Count == 0)
        {
            foreach (var chain in chains)
            {
                if (chain.Count < MinimumChainCount || chain.Skills.Length == 0 || chain.DamagePerSecond <= 0)
                    continue;

                var weight = chain.DamagePerSecond * (1d + Math.Log(chain.Count));
                Offer(best, new ComboSuggestion(chain.Skills[0], ComboSuggestionMatch.Opener, chain, weight), isReady);
            }
        }

        return best.Values
            .OrderByDescending(static suggestion => suggestion.Match)
            .ThenByDescending(static suggestion => suggestion.Score)
            .Take(maxSuggestions)
            .ToList();
    }

    public static List<ComboChainStat> TopChains(IReadOnlyList<ComboChainStat> chains, int count, int minimumCount = MinimumChainCount)
        => chains
            .Where(chain => chain.Count >= minimumCount && chain.DamagePerSecond > 0)
            .OrderByDescending(static chain => chain.DamagePerSecond)
            .ThenByDescending(static chain => chain.Count)
            .Take(count)
            .ToList();

    private static void Offer(Dictionary<int, ComboSuggestion> best, ComboSuggestion candidate, Func<int, bool> isReady)
    {
        if (!isReady(candidate.SkillId))
            return;

        if (!best.TryGetValue(candidate.SkillId, out var existing) ||
            candidate.Match > existing.Match ||
            candidate.Match == existing.Match && candidate.Score > existing.Score)
        {
            best[candidate.SkillId] = candidate;
        }
    }
}
