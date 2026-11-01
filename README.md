# Death Risk Hint（死亡风险提示）

《杀戮尖塔 2》(Slay the Spire 2) v0.111.0 战斗界面辅助模组 · 作者 **Grisxxx**

## 它做什么

在战斗中的「结束回合」按钮下方加一行红色小字 **死亡风险**：
当你现在按下结束回合、接下来的一整套结算会把你打死时出现。

- 纯客户端 UI 提示，**不影响游戏平衡**（`affects_gameplay: false`）。
- 只在轮到你自己行动、且尚未按下结束回合时计算。
- 敌人攻击伤害直接取自游戏自身的意图计算（`AttackIntent.GetSingleDamage` × `Repeats`），因此力量、虚弱、易伤、钉刺等修正都已包含，与敌人头顶显示的意图数字一致。
- 敌人意图、你的格挡/生命、手牌变化时实时刷新，间隔 0.1 秒。

## 调试

游戏日志里搜索 `[DeathRiskHint]`：

- `[DeathRiskHint] 已加载：...` —— 补丁安装成功。
- `[DeathRiskHint] 提示已挂载。buttonSize=..., visualsSize=..., labelPos=..., resolved=True` —— 小字节点创建成功及定位参数。
- `[DeathRiskHint] risky=True => lethal=True hp=3(+0block)->0 attack=16(blocked 0) other=0(...) osty=... doom=none` —— 计算结果变化记录，含各分项明细。

日志路径：`%APPDATA%\SlayTheSpire2\logs\godot.log`。
