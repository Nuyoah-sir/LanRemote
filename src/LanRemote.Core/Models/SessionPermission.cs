namespace LanRemote.Core.Models;

/// <summary>
/// 会话权限级别。
/// </summary>
/// <remarks>
/// <c>ViewOnly = 0</c> 必须在数值上小于 <c>Control = 1</c>，
/// 服务端可用它做「不超过请求上限」的比较。该枚举值参与认证 transcript，
/// 改动会破坏协议兼容性，属 breaking change。
/// </remarks>
public enum SessionPermission
{
    /// <summary>仅查看，禁止注入任何输入。</summary>
    ViewOnly = 0,

    /// <summary>查看并控制，允许键鼠注入（仍需通过服务端权限闸门）。</summary>
    Control = 1,
}
