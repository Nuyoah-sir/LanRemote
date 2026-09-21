using System.Runtime.CompilerServices;

// 只为「TLS 选项必须被显式设置」这类<b>无法从外部行为低成本观察</b>的约束开一个口子。
// 与 Discovery 的做法一致：不开 internal，就只能把这些约束写成注释，而注释不会变红。
[assembly: InternalsVisibleTo("LanRemote.Transport.Tests")]
