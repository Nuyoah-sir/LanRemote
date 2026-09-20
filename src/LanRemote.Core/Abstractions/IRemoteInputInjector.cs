using LanRemote.Core.Models;

namespace LanRemote.Core.Abstractions;

/// <summary>
/// 远端输入注入器（被控端执行）。
/// </summary>
/// <remarks>
/// <para>硬约束（见 <c>START_HERE.md</c> 输入安全闸门）：只有以下全部成立时才允许调用 Win32 <c>SendInput</c>：</para>
/// <list type="number">
/// <item><description>session 已认证；</description></item>
/// <item><description>session permission == Control；</description></item>
/// <item><description>Host.AllowControl == true；</description></item>
/// <item><description>本机审批授予了 control；</description></item>
/// <item><description>session 处于 active 状态。</description></item>
/// </list>
/// <para>闸门必须在服务端（Host）落实，不能只依赖客户端「不发送」。</para>
/// </remarks>
public interface IRemoteInputInjector
{
    /// <summary>移动指针。</summary>
    /// <param name="point">归一化坐标。</param>
    /// <param name="target">目标显示器。</param>
    void MovePointer(NormalizedPoint point, DisplayInfo target);

    /// <summary>鼠标按键。</summary>
    /// <param name="e">按键事件。</param>
    /// <param name="target">目标显示器。</param>
    void MouseButton(MouseButtonEvent e, DisplayInfo target);

    /// <summary>滚轮。</summary>
    /// <param name="e">滚轮事件。</param>
    /// <param name="target">目标显示器。</param>
    void MouseWheel(MouseWheelEvent e, DisplayInfo target);

    /// <summary>键盘事件。</summary>
    /// <param name="e">键盘事件。</param>
    void Keyboard(KeyboardEvent e);
}
