using Cloris.Aion2Flow.Services;
using Cloris.Aion2Flow.ViewModels;

namespace Cloris.Aion2Flow.Tests.App;

public sealed class ComboAnalyzerTests
{
    [Fact]
    public void BuildCasts_MergesMultiHitSkillAndAttributesTicks()
    {
        var casts = ComboAnalyzer.BuildCasts(
        [
            new ComboHit(1_000, 10, 100, IsPeriodic: false),
            new ComboHit(1_300, 10, 50, IsPeriodic: false),
            new ComboHit(2_000, 20, 200, IsPeriodic: false),
            new ComboHit(2_500, 10, 25, IsPeriodic: true),
            new ComboHit(3_500, 10, 80, IsPeriodic: false),
        ]);

        Assert.Equal(3, casts.Count);
        Assert.Equal(new ComboCast(1_000, 10, 175), casts[0]);
        Assert.Equal(new ComboCast(2_000, 20, 200), casts[1]);
        Assert.Equal(new ComboCast(3_500, 10, 80), casts[2]);
    }

    [Fact]
    public void BuildCasts_IgnoresTicksWithoutAPriorCast()
    {
        var casts = ComboAnalyzer.BuildCasts([new ComboHit(500, 30, 999, IsPeriodic: true)]);

        Assert.Empty(casts);
    }

    [Fact]
    public void Analyze_RanksRepeatedChainsByDamagePerSecond()
    {
        // A→B→C hits hard and is used twice; D→E→F is weaker.
        var hits = new List<ComboHit>();
        long t = 0;
        void Cast(int skill, long damage)
        {
            hits.Add(new ComboHit(t, skill, damage, IsPeriodic: false));
            t += 1_000;
        }

        Cast(1, 1_000); Cast(2, 1_000); Cast(3, 1_000);
        Cast(4, 100); Cast(5, 100); Cast(6, 100);
        Cast(1, 1_000); Cast(2, 1_000); Cast(3, 1_000);
        Cast(4, 100); Cast(5, 100); Cast(6, 100);

        var result = ComboAnalyzer.Analyze(hits);

        var best = result.Chains[0];
        Assert.Equal(new[] { 1, 2, 3 }, best.Skills);
        Assert.Equal(2, best.Count);
        Assert.Equal(1_000d, best.DamagePerSecond, 3);
        Assert.Contains(result.Chains, chain => chain.Key == "4,5,6" && chain.Count == 2);
    }

    [Fact]
    public void Analyze_DoesNotChainAcrossCombatBreaksOrRepeatedSkill()
    {
        var result = ComboAnalyzer.Analyze(
        [
            new ComboHit(0, 1, 10, false),
            new ComboHit(1_000, 2, 10, false),
            new ComboHit(10_000, 3, 10, false),
            new ComboHit(11_000, 3, 10, false),
            new ComboHit(12_000, 4, 10, false),
        ]);

        // 1→2 is cut off from 3 by a 9 s break, and 3→3 is the same skill twice.
        Assert.Empty(result.Chains);
    }

    [Fact]
    public void Analyze_ReportsIdleGapsInsideCombat()
    {
        var result = ComboAnalyzer.Analyze(
        [
            new ComboHit(0, 1, 10, false),
            new ComboHit(1_000, 2, 10, false),
            new ComboHit(4_000, 3, 10, false),
            new ComboHit(5_000, 1, 10, false),
            new ComboHit(20_000, 2, 10, false),
        ]);

        Assert.Equal(1, result.IdleGapCount);
        Assert.Equal(3_000, result.IdleMilliseconds);
        Assert.Equal(3_000, result.LongestIdleGapMilliseconds);
        Assert.Equal(5_000, result.ActiveMilliseconds);
    }

    [Fact]
    public void Keys_RoundTrip()
    {
        var key = ComboAnalyzer.CreateKey([11, 22, 33]);

        Assert.True(ComboAnalyzer.TryParseKey(key, out var skills));
        Assert.Equal(new[] { 11, 22, 33 }, skills);
        Assert.False(ComboAnalyzer.TryParseKey("1,x", out _));
    }

    [Fact]
    public void Suggest_PrefersCompletingTheCurrentChain()
    {
        ComboChainStat[] chains =
        [
            new([1, 2, 3], Count: 5, TotalDamage: 50_000, TotalDurationMilliseconds: 10_000),
            new([2, 4, 5], Count: 5, TotalDamage: 90_000, TotalDurationMilliseconds: 10_000),
        ];

        var suggestions = ComboSuggester.Suggest([1, 2], chains, _ => true);

        Assert.Equal(3, suggestions[0].SkillId);
        Assert.Equal(ComboSuggestionMatch.CompletesChain, suggestions[0].Match);
        Assert.Contains(suggestions, suggestion => suggestion.SkillId == 4 && suggestion.Match == ComboSuggestionMatch.FollowsLast);
    }

    [Fact]
    public void Suggest_SkipsSkillsOnCooldownAndFallsBackToOpeners()
    {
        ComboChainStat[] chains =
        [
            new([1, 2, 3], Count: 3, TotalDamage: 30_000, TotalDurationMilliseconds: 6_000),
            new([7, 8, 9], Count: 3, TotalDamage: 60_000, TotalDurationMilliseconds: 6_000),
        ];

        var suggestions = ComboSuggester.Suggest([1, 2], chains, skill => skill != 3);

        Assert.Equal(7, suggestions[0].SkillId);
        Assert.Equal(ComboSuggestionMatch.Opener, suggestions[0].Match);
        Assert.DoesNotContain(suggestions, suggestion => suggestion.SkillId == 3);
    }

    [Fact]
    public void Suggest_IgnoresChainsSeenOnlyOnce()
    {
        ComboChainStat[] chains = [new([1, 2, 3], Count: 1, TotalDamage: 30_000, TotalDurationMilliseconds: 3_000)];

        Assert.Empty(ComboSuggester.Suggest([1, 2], chains, _ => true));
    }

    [Fact]
    public void ProfileStore_AccumulatesChainsPerClassAndPersists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"combo-profile-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ComboProfileStore(path);
            var analysis = ComboAnalyzer.AnalyzeCasts(
            [
                new ComboCast(0, 1, 100),
                new ComboCast(1_000, 2, 100),
                new ComboCast(2_000, 3, 100),
                new ComboCast(3_000, 1, 100),
                new ComboCast(4_000, 2, 100),
                new ComboCast(5_000, 3, 100),
            ]);

            store.Record("Gladiator", analysis);
            store.Record("Gladiator", analysis);

            var reloaded = new ComboProfileStore(path);
            var chain = Assert.Single(reloaded.GetChains("Gladiator"), stat => stat.Key == "1,2,3");
            Assert.Equal(4, chain.Count);
            Assert.Equal("Gladiator", reloaded.LastClassKey);
            Assert.Equal(2, reloaded.GetEncounterCount("Gladiator"));
            Assert.Empty(reloaded.GetChains("Cleric"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
