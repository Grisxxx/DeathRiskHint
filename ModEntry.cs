using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace DeathRiskHint;

/// <summary>
/// 模组入口。游戏加载本模组后会调用 <see cref="Init"/>。
/// </summary>
[ModInitializer(nameof(Init))]
public static class ModEntry
{
    public const string HarmonyId = "DeathRiskHint";

    public static void Init()
    {
        try
        {
            new Harmony(HarmonyId).PatchAll(typeof(ModEntry).Assembly);
            Log.Info("[DeathRiskHint] 已加载：结束回合按钮会显示「死亡风险」提示。");
        }
        catch (Exception ex)
        {
            Log.Error($"[DeathRiskHint] 补丁安装失败：{ex}");
        }
    }
}
