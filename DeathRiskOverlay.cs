using System;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.addons.mega_text;

namespace DeathRiskHint;

/// <summary>
/// 挂在结束回合按钮上的小部件：定时估算死亡风险，并在按钮上显示/隐藏「死亡风险」小字。
/// <para>
/// 注意：本模组是纯 C# 类库，没有 Godot 的 C# 源生成器，因此 <c>Node._Process</c> 这类
/// 引擎虚方法不会被回调。每帧刷新改用 <see cref="SceneTree.ProcessFrame"/> 信号驱动
/// （纯 C# 委托，不依赖源生成器）。
/// </para>
/// </summary>
internal sealed class DeathRiskOverlay : Node
{
    private const string OverlayName = "DeathRiskOverlay";
    private const string LabelName = "DeathRiskLabel";
    private const string RiskText = "死亡风险";
    private const double RefreshInterval = 0.1;
    private const float LabelWidth = 360f;
    private const float LabelHeight = 28f;

    private Label? _label;
    private Control? _visuals;
    private MegaLabel? _originalLabel;
    private NEndTurnButton? _button;
    private SceneTree? _tree;
    private Action? _tickHandler;
    private double _accumulator;
    private bool _loggedError;
    private bool _layoutResolved;
    private string _lastSignature = string.Empty;

    public static void Attach(NEndTurnButton button)
    {
        if (button == null || !GodotObject.IsInstanceValid(button))
        {
            return;
        }

        try
        {
            if (button.GetNodeOrNull<DeathRiskOverlay>(OverlayName) != null)
            {
                return;
            }

            var overlay = new DeathRiskOverlay { Name = OverlayName };
            button.AddChild(overlay);
            overlay.Build(button);
        }
        catch (Exception ex)
        {
            Log.Error($"[DeathRiskHint] 挂载失败：{ex}");
        }
    }

    private void Build(NEndTurnButton button)
    {
        _button = button;
        _visuals = button.GetNodeOrNull<Control>("Visuals");
        _originalLabel = button.GetNodeOrNull<MegaLabel>("Visuals/Label");

        Node parent = _visuals ?? (Node)button;

        var label = new Label
        {
            Name = LabelName,
            Text = RiskText,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };

        // 字体必须和按钮文字一致，否则中文会显示成方块。
        Font? font = _originalLabel?.GetThemeFont("font") ?? ThemeDB.FallbackFont;
        if (font != null)
        {
            label.AddThemeFontOverride("font", font);
        }

        int baseFontSize = _originalLabel?.GetThemeFontSize("font_size") ?? 0;
        int smallSize = baseFontSize > 0 ? (int)MathF.Round(baseFontSize * 0.58f) : 18;
        label.AddThemeFontSizeOverride("font_size", Math.Clamp(smallSize, 13, 26));
        label.AddThemeColorOverride("font_color", StsColors.red);
        label.AddThemeColorOverride("font_outline_color", Colors.Black);
        label.AddThemeConstantOverride("outline_size", 4);
        label.Size = new Vector2(LabelWidth, LabelHeight);

        parent.AddChild(label);
        _label = label;

        ApplyLayout();

        _tree = GetTree();
        _tickHandler = OnProcessFrame;
        if (_tree != null)
        {
            _tree.ProcessFrame += _tickHandler;
        }

        Log.Info(
            $"[DeathRiskHint] 提示已挂载。buttonSize={button.Size}, visualsSize={_visuals?.Size ?? Vector2.Zero}, " +
            $"originalLabelPos={_originalLabel?.Position}, originalLabelSize={_originalLabel?.Size ?? Vector2.Zero}, " +
            $"labelPos={label.Position}, fontSize={label.GetThemeFontSize("font_size")}, " +
            $"font={(font != null ? font.GetType().Name : "null")}, resolved={_layoutResolved}, " +
            $"tree={(_tree != null ? "ok" : "null")}");
    }

    /// <summary>
    /// 计算并应用小字位置。节点刚创建时尺寸可能还是 0，因此每帧都会重试，直到成功。
    /// </summary>
    private void ApplyLayout()
    {
        if (_label == null || _layoutResolved || _button == null || !GodotObject.IsInstanceValid(_button))
        {
            return;
        }

        Vector2 buttonSize = _button.Size;
        Vector2 visualsSize = _visuals != null && GodotObject.IsInstanceValid(_visuals) ? _visuals.Size : Vector2.Zero;
        Vector2 originalSize = _originalLabel != null && GodotObject.IsInstanceValid(_originalLabel) ? _originalLabel.Size : Vector2.Zero;

        float anchorWidth = visualsSize.X > 1f ? visualsSize.X : (buttonSize.X > 1f ? buttonSize.X : 0f);
        float anchorHeight = visualsSize.Y > 1f ? visualsSize.Y : (buttonSize.Y > 1f ? buttonSize.Y : 0f);

        Vector2 position;
        if (anchorWidth > 1f && anchorHeight > 1f)
        {
            // 贴在按钮贴图正下方居中。
            position = new Vector2(anchorWidth * 0.5f - LabelWidth * 0.5f, anchorHeight + 2f);
            _layoutResolved = true;
        }
        else if (_originalLabel != null && GodotObject.IsInstanceValid(_originalLabel) && originalSize.X > 1f && originalSize.Y > 1f)
        {
            // 贴图尺寸不可用时，用按钮文字的位置推算。
            position = new Vector2(
                _originalLabel.Position.X + originalSize.X * 0.5f - LabelWidth * 0.5f,
                _originalLabel.Position.Y + originalSize.Y + 2f);
            _layoutResolved = true;
        }
        else
        {
            // 兜底：等下一帧继续尝试。
            position = new Vector2(-LabelWidth * 0.5f, 56f);
        }

        _label.Position = position;
    }

    private void OnProcessFrame()
    {
        // 按钮被释放（战斗结束/重进）时，本节点也会随之失效，这里主动退订，避免空转。
        if (!GodotObject.IsInstanceValid(this) || _label == null || !GodotObject.IsInstanceValid(_label))
        {
            Detach();
            return;
        }

        ApplyLayout();

        _accumulator += GetProcessDeltaTime();
        if (_accumulator < RefreshInterval)
        {
            return;
        }
        _accumulator = 0.0;
        Refresh();
    }

    private void Detach()
    {
        _label = null;
        if (_tree != null && _tickHandler != null)
        {
            try
            {
                _tree.ProcessFrame -= _tickHandler;
            }
            catch (Exception ex)
            {
                Log.Warn($"[DeathRiskHint] 退订失败：{ex.Message}");
            }
        }
        _tree = null;
        _tickHandler = null;
    }

    private void Refresh()
    {
        if (_label == null || !GodotObject.IsInstanceValid(_label))
        {
            return;
        }

        try
        {
            bool risky = DeathRiskCalc.TryEvaluate(out RiskEstimate estimate);
            _label.Visible = risky;

            // 只在结果变化时写日志，方便排查问题又不会刷屏。
            string signature = $"{(risky ? 1 : 0)}|{estimate.Signature}";
            if (signature != _lastSignature)
            {
                _lastSignature = signature;
                Log.Info($"[DeathRiskHint] risky={risky} => {estimate.Signature}");
            }
        }
        catch (Exception ex)
        {
            _label.Visible = false;
            if (!_loggedError)
            {
                _loggedError = true;
                Log.Warn($"[DeathRiskHint] 计算死亡风险时出错：{ex}");
            }
        }
    }
}
