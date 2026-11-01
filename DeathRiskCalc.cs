using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace DeathRiskHint;

/// <summary>一次死亡风险估算的明细，用于日志与调试。</summary>
public readonly struct RiskEstimate
{
    public bool Lethal { get; init; }
    public int HpBefore { get; init; }
    public int BlockBefore { get; init; }
    public int HpAfter { get; init; }
    public bool OstyPresent { get; init; }
    public int OstyHpBefore { get; init; }
    public int OstyAbsorbed { get; init; }
    public int AttackTotal { get; init; }
    public int BlockedByAttack { get; init; }
    public int CardAndPowerDamage { get; init; }
    public int BlockedByCardAndPower { get; init; }
    public bool Doomed { get; init; }
    public int DoomAmount { get; init; }

    public string Signature =>
        $"lethal={Lethal} hp={HpBefore}(+{BlockBefore}block)->{HpAfter} " +
        $"attack={AttackTotal}(blocked {BlockedByAttack}) " +
        $"other={CardAndPowerDamage}(blocked {BlockedByCardAndPower}) " +
        $"osty={(OstyPresent ? $"{OstyHpBefore}hp absorbed {OstyAbsorbed}" : "none")} " +
        $"doom={(Doomed ? $"lethal({DoomAmount})" : DoomAmount > 0 ? DoomAmount.ToString() : "none")}";
}

/// <summary>
/// 估算「如果现在结束本回合，玩家会不会死」。
/// <para>
/// 模拟顺序贴近游戏真实结算：
/// <list type="number">
/// <item>玩家回合结束时先结算手牌里的「回合结束效果」卡（可格挡 / 不可格挡 / 直接失去生命）；</item>
/// <item>再结算玩家身上的束缚等「回合结束造成伤害」的 buff（可格挡）；</item>
/// <item>灾厄在此刻判定致死（无视格挡与奥提斯）；</item>
/// <item>敌人回合按意图逐段攻击：先由玩家格挡吸收，未格挡部分优先由奥提斯代挡，奥提斯没血了才落到玩家身上；</item>
/// <item>最后结算玩家身上的中毒（不可格挡，下回合开始生效）。</item>
/// </list>
/// 奥提斯只代挡「攻击」类伤害：<c>DieForYouPower</c> 只对 <c>ValueProp.Move</c> 且不带 <c>Unpowered</c>
/// 的伤害生效，所以中毒、束缚、状态卡这类 <c>Unpowered</c> 伤害会绕过它。
/// </para>
/// </summary>
internal static class DeathRiskCalc
{
    /// <summary>
    /// 计算一次死亡风险。
    /// </summary>
    /// <param name="estimate">估算明细。</param>
    /// <returns>true 表示现在结束回合会死亡。</returns>
    public static bool TryEvaluate(out RiskEstimate estimate)
    {
        estimate = default;

        CombatManager manager = CombatManager.Instance;
        if (manager == null || !manager.IsInProgress)
        {
            return false;
        }

        CombatState? state = manager.DebugOnlyGetState();
        if (state == null || state.CurrentSide != CombatSide.Player)
        {
            return false;
        }

        Player? me = LocalContext.GetMe((ICombatState)state);
        Creature? player = me?.Creature;
        if (me == null || player == null || !player.IsAlive)
        {
            return false;
        }

        // 玩家已经按下结束回合按钮（按钮处于「撤销」状态）时，不必再提示。
        if (manager.IsPlayerReadyToEndTurn(me))
        {
            return false;
        }

        estimate = Simulate(state, me, player);
        return estimate.Lethal;
    }

    private static RiskEstimate Simulate(CombatState state, Player me, Creature player)
    {
        int hpBefore = Math.Max(0, player.CurrentHp);
        int blockBefore = Math.Max(0, player.Block);
        int hp = hpBefore;
        int block = blockBefore;

        Creature? osty = me.Osty;
        bool ostyPresent = osty != null && osty.IsAlive;
        int ostyHp = ostyPresent ? Math.Max(0, osty!.CurrentHp) : 0;
        int ostyHpBefore = ostyHp;
        int ostyAbsorbed = 0;

        int attackTotal = 0;
        int blockedByAttack = 0;
        int otherTotal = 0;
        int blockedByOther = 0;

        // 1) 手牌里的「回合结束效果」卡。
        PlayerCombatState? combatState = me.PlayerCombatState;
        int handCount = combatState?.Hand.Cards.Count ?? 0;
        if (combatState != null)
        {
            foreach (CardModel card in combatState.Hand.Cards)
            {
                if (!card.HasTurnEndInHandEffect)
                {
                    continue;
                }

                try
                {
                    if (card is Regret)
                    {
                        // 悔恨：按手牌数造成不可格挡伤害。
                        otherTotal += handCount;
                        hp -= handCount;
                        continue;
                    }

                    DynamicVarSet? vars = card.DynamicVars;
                    if (vars == null)
                    {
                        continue;
                    }

                    if (vars.ContainsKey("HpLoss"))
                    {
                        // 直接失去生命（如霉运、召唤）：不可格挡、也不经奥提斯。
                        int amount = Math.Max(0, (int)vars["HpLoss"].BaseValue);
                        otherTotal += amount;
                        hp -= amount;
                    }
                    else if (vars.ContainsKey("Damage"))
                    {
                        // 造成伤害（如燃烧、腐朽、感染、剧毒、凋零）：可以被格挡，但带 Unpowered，奥提斯不代挡。
                        int amount = Math.Max(0, (int)vars["Damage"].BaseValue);
                        otherTotal += amount;
                        int blocked = Math.Min(block, amount);
                        block -= blocked;
                        blockedByOther += blocked;
                        hp -= amount - blocked;
                    }
                }
                catch (Exception)
                {
                    // 个别卡取不到数值时忽略，不影响整体估算。
                }
            }
        }

        // 2) 玩家身上「回合结束造成伤害」的 buff，目前是束缚（可格挡、不经奥提斯）。
        foreach (PowerModel power in player.Powers)
        {
            if (power is ConstrictPower constrict)
            {
                int amount = Math.Max(0, constrict.Amount);
                otherTotal += amount;
                int blocked = Math.Min(block, amount);
                block -= blocked;
                blockedByOther += blocked;
                hp -= amount - blocked;
            }
        }

        // 3) 灾厄：玩家回合结束时若剩余生命 <= 灾厄层数，直接死亡（无视格挡与奥提斯）。
        bool doomed = false;
        int doomAmount = 0;
        DoomPower? doom = player.Powers.OfType<DoomPower>().FirstOrDefault();
        if (doom != null)
        {
            doomAmount = Math.Max(0, doom.Amount);
            if (doomAmount > 0 && hp <= doomAmount)
            {
                doomed = true;
                hp = 0;
            }
        }

        // 4) 敌人下回合的攻击意图，逐段结算。
        if (!doomed && hp > 0)
        {
            IReadOnlyList<Creature> players = state.PlayerCreatures;
            foreach (Creature enemy in state.Enemies)
            {
                if (enemy == null || !enemy.IsAlive || enemy.Monster == null)
                {
                    continue;
                }

                MoveState? move = enemy.Monster.NextMove;
                if (move?.Intents == null)
                {
                    continue;
                }

                foreach (AbstractIntent intent in move.Intents)
                {
                    if (intent is not AttackIntent attack)
                    {
                        continue;
                    }

                    int single;
                    int repeats;
                    try
                    {
                        single = Math.Max(0, attack.GetSingleDamage(players, enemy));
                        repeats = Math.Max(0, attack.Repeats);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (single <= 0)
                    {
                        continue;
                    }

                    if (repeats <= 0)
                    {
                        repeats = 1;
                    }

                    for (int i = 0; i < repeats; i++)
                    {
                        if (hp <= 0)
                        {
                            break;
                        }

                        attackTotal += single;

                        int blocked = Math.Min(block, single);
                        block -= blocked;
                        blockedByAttack += blocked;

                        int unblocked = single - blocked;
                        if (unblocked <= 0)
                        {
                            continue;
                        }

                        // 奥提斯优先替玩家接住未格挡的攻击伤害；它扛不住时剩余部分才由玩家承担。
                        if (ostyHp > 0)
                        {
                            int absorbed = Math.Min(ostyHp, unblocked);
                            ostyHp -= absorbed;
                            ostyAbsorbed += absorbed;
                        }
                        else
                        {
                            hp -= unblocked;
                        }
                    }
                }
            }
        }

        // 5) 中毒：玩家下回合开始时结算，不可格挡、不经奥提斯。
        if (!doomed && hp > 0)
        {
            PoisonPower? poison = player.Powers.OfType<PoisonPower>().FirstOrDefault();
            if (poison != null)
            {
                int amount;
                try
                {
                    amount = Math.Max(0, poison.CalculateTotalDamageNextTurn());
                }
                catch (Exception)
                {
                    amount = Math.Max(0, poison.Amount);
                }

                otherTotal += amount;
                hp -= amount;
            }
        }

        return new RiskEstimate
        {
            Lethal = doomed || hp <= 0,
            HpBefore = hpBefore,
            BlockBefore = blockBefore,
            HpAfter = Math.Max(0, hp),
            OstyPresent = ostyPresent,
            OstyHpBefore = ostyHpBefore,
            OstyAbsorbed = ostyAbsorbed,
            AttackTotal = attackTotal,
            BlockedByAttack = blockedByAttack,
            CardAndPowerDamage = otherTotal,
            BlockedByCardAndPower = blockedByOther,
            Doomed = doomed,
            DoomAmount = doomAmount,
        };
    }
}
