using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace EtherTransfer.UI;

/// <summary>
/// 失焦不清除选区的文本块。SelectableTextBlock 在 OnLostFocus 中固定调用 ClearSelection，
/// 导致选中文本后点击其他区域选区立即消失；聊天场景下应保留选区（点别处仍可看到蓝色高亮）。
///
/// 关键 1：Avalonia 样式按精确类型匹配，子类必须复写 StyleKeyOverride 才能吃到
///         SelectableTextBlock 的主题样式（否则 SelectionBrush 为空 → 选区不可见）。
/// 关键 2：**绝不夺取键盘焦点**。文本块一旦获得焦点，外层滚动容器就会把"当前聚焦的元素"
///         滚进可视区；聊天里超长气泡比视口还高，这一滚就是明显的跳动（切走再回来时还会
///         因为焦点恢复再滚一次，表现为闪一下）。为了从根上避免，焦点交给窗口本身，
///         复制所需的 Ctrl+C 由主窗口统一处理（见 MainWindow.HandleCopyHotkey）。
/// 注意：OnLostFocus 故意不调用 base（它会清空选区），上层基类该方法无额外逻辑。
/// </summary>
public class PersistentSelectableTextBlock : SelectableTextBlock
{
    /// <summary>最近一次被点击的文本块（供窗口级 Ctrl+C 使用）。</summary>
    public static WeakReference<PersistentSelectableTextBlock>? LastActive { get; private set; }

    /// <summary>复用 SelectableTextBlock 的主题样式（否则选中高亮画刷为 null，选区看不见）。</summary>
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        LastActive = new WeakReference<PersistentSelectableTextBlock>(this);

        // 不调用 Focus()：避免滚动容器把本元素滚进可视区导致跳动/闪烁。
        // 键盘焦点由主窗口在点击消息区时接管（窗口级），Ctrl+C 在窗口层处理。
        base.OnPointerPressed(e);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        // 保留选区：不调用 SelectableTextBlock.OnLostFocus（其中调用 ClearSelection）
    }
}
