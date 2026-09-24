"""旧 M2 打包入口已停用，不再复制旧脚本、旧手册或生成旧包。"""


def main() -> None:
    raise SystemExit(
        "旧 M2 打包入口已禁用，不执行复制、发布或打包。"
        "请使用当前 scripts/acceptance/make-m3-package.py，"
        "通过 --manual 指定当前验收手册，并通过 --output-dir 指定已有输出目录。"
    )


if __name__ == "__main__":
    main()
