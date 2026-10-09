using Cloris.Aion2Flow.SceneRuntime.Model;

namespace Cloris.Aion2Flow.SceneRuntime.Combat;

public readonly record struct SceneCombatantMetrics
{
    internal SceneCombatantMetrics(CharacterClass? characterClass, bool isVisiblePlayerCombatant, double damagePerSecond, double healingPerSecond, long damageAmount, long healingAmount, long periodicHealingAmount, long drainDamageAmount, long drainHealingAmount, long regenerationHealingAmount, long shieldAmount, int shieldTimes, long shieldAbsorbedAmount, int shieldAbsorbedTimes, double damageContribution)
    {
        CharacterClass = characterClass;
        IsVisiblePlayerCombatant = isVisiblePlayerCombatant;
        DamagePerSecond = damagePerSecond;
        HealingPerSecond = healingPerSecond;
        DamageAmount = damageAmount;
        HealingAmount = healingAmount;
        PeriodicHealingAmount = periodicHealingAmount;
        DrainDamageAmount = drainDamageAmount;
        DrainHealingAmount = drainHealingAmount;
        RegenerationHealingAmount = regenerationHealingAmount;
        ShieldAmount = shieldAmount;
        ShieldTimes = shieldTimes;
        ShieldAbsorbedAmount = shieldAbsorbedAmount;
        ShieldAbsorbedTimes = shieldAbsorbedTimes;
        DamageContribution = damageContribution;
    }

    public CharacterClass? CharacterClass { get; init; }

    public bool IsVisiblePlayerCombatant { get; init; }

    public double DamagePerSecond { get; init; }

    public double HealingPerSecond { get; init; }

    public long DamageAmount { get; init; }

    public long HealingAmount { get; init; }

    public long PeriodicHealingAmount { get; init; }

    public long DrainDamageAmount { get; init; }

    public long DrainHealingAmount { get; init; }

    public long RegenerationHealingAmount { get; init; }

    public long ShieldAmount { get; init; }

    public int ShieldTimes { get; init; }

    public long ShieldAbsorbedAmount { get; init; }

    public int ShieldAbsorbedTimes { get; init; }

    public double DamageContribution { get; init; }

    public int HitCount { get; init; }

    public int CriticalHitCount { get; init; }

    public int BackHitCount { get; init; }

    public double CriticalRate => HitCount > 0 ? (double)CriticalHitCount / HitCount : 0d;

    public double BackRate => HitCount > 0 ? (double)BackHitCount / HitCount : 0d;
}

internal struct SceneCombatantMetricsAccumulator
{
    public CharacterClass? CharacterClass;
    public bool IsVisiblePlayerCombatant;
    public double DamagePerSecond;
    public double HealingPerSecond;
    public long DamageAmount;
    public long HealingAmount;
    public long PeriodicHealingAmount;
    public long DrainDamageAmount;
    public long DrainHealingAmount;
    public long RegenerationHealingAmount;
    public long ShieldAmount;
    public int ShieldTimes;
    public long ShieldAbsorbedAmount;
    public int ShieldAbsorbedTimes;
    public double DamageContribution;
    public int HitCount;
    public int CriticalHitCount;
    public int BackHitCount;

    public void ApplyMechanicTotals(int hitCount, int criticalHitCount, int backHitCount)
    {
        HitCount += hitCount;
        CriticalHitCount += criticalHitCount;
        BackHitCount += backHitCount;
    }

    public void Reset()
    {
        this = default;
    }

    public void ApplyCombatTotals(long damageAmount, long healingAmount, long periodicHealingAmount, long drainDamageAmount, long drainHealingAmount, long regenerationHealingAmount, long shieldAmount, int shieldTimes, long shieldAbsorbedAmount, int shieldAbsorbedTimes)
    {
        DamageAmount += damageAmount;
        HealingAmount += healingAmount;
        PeriodicHealingAmount += periodicHealingAmount;
        DrainDamageAmount += drainDamageAmount;
        DrainHealingAmount += drainHealingAmount;
        RegenerationHealingAmount += regenerationHealingAmount;
        ShieldAmount += shieldAmount;
        ShieldTimes += shieldTimes;
        ShieldAbsorbedAmount += shieldAbsorbedAmount;
        ShieldAbsorbedTimes += shieldAbsorbedTimes;
    }

    public readonly SceneCombatantMetrics ToSnapshot()
    {
        return new SceneCombatantMetrics(CharacterClass, IsVisiblePlayerCombatant, DamagePerSecond, HealingPerSecond, DamageAmount, HealingAmount, PeriodicHealingAmount, DrainDamageAmount, DrainHealingAmount, RegenerationHealingAmount, ShieldAmount, ShieldTimes, ShieldAbsorbedAmount, ShieldAbsorbedTimes, DamageContribution)
        {
            HitCount = HitCount,
            CriticalHitCount = CriticalHitCount,
            BackHitCount = BackHitCount,
        };
    }
}
