using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace DeathRiskHint;

/// <summary>
/// 在结束回合按钮完成初始化后，挂上死亡风险提示。
/// </summary>
[HarmonyPatch(typeof(NEndTurnButton), "_Ready")]
internal static class EndTurnButtonReadyPatch
{
    [HarmonyPostfix]
    private static void Postfix(NEndTurnButton __instance)
    {
        DeathRiskOverlay.Attach(__instance);
    }
}
