#!/usr/bin/env bash
# 为当前 shell 会话加载本机级的 .NET SDK。
# 用法：source scripts/env.sh
#
# 说明：SDK 装在用户级目录而不是 Program Files，所以不在系统 PATH 里。
# 本脚本只影响当前会话，不修改任何持久化环境变量。

DOTNET_DIR="/c/Users/Administrator/.dotnet"

if [ ! -x "$DOTNET_DIR/dotnet.exe" ]; then
  echo "未找到 SDK: $DOTNET_DIR/dotnet.exe" >&2
  echo "请重新运行安装步骤，或手工指定 dotnet 路径。" >&2
  return 1 2>/dev/null || exit 1
fi

export DOTNET_ROOT="$DOTNET_DIR"
export PATH="$DOTNET_DIR:$PATH"
export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

echo "已加载 .NET SDK: $(dotnet --version)"
