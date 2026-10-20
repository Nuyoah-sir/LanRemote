using System.Runtime.CompilerServices;

// 仅向 App 和指定测试程序集开放内部协作；不增加 Transport 公开 API。
[assembly: InternalsVisibleTo("LanRemote.Transport.Tests")]
[assembly: InternalsVisibleTo("LanRemote.App")]
[assembly: InternalsVisibleTo("LanRemote.Acceptance.Tests")]
