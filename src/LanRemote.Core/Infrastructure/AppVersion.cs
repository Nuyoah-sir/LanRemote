using System.Reflection;

namespace LanRemote.Core.Infrastructure;

/// <summary>
/// 应用程序版本。
/// </summary>
/// <remarks>
/// 取自程序集的 <see cref="AssemblyInformationalVersionAttribute"/>，
/// 由 <c>Directory.Build.props</c> 里的 <c>LanRemoteVersion</c> 统一驱动，
/// 因此 discovery announcement 里的 <c>appVersion</c> 不需要单独维护一份常量。
/// </remarks>
public static class AppVersion
{
    /// <summary>当前版本字符串。</summary>
    public static string Current { get; } = ReadInformationalVersion();

    private static string ReadInformationalVersion()
    {
        string? version = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(version))
        {
            return "0.0.0-unknown";
        }

        // InformationalVersion 可能带 +<SourceRevisionId>；对外公布的版本去掉这一段。
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? version[..plus] : version;
    }
}
