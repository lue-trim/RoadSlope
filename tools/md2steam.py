#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""md2steam.py — Markdown → Steam 创意工坊 BBCode 转换器（封装 pandoc + 针对性修正）

用法:
    python3 tools/md2steam.py <input.md> [output.txt]
    （默认输出 <input>.steam.txt；PANDOC 环境变量可覆盖 pandoc 路径）

依赖: 较新的 pandoc（自带 bbcode_steam writer，实测 3.12.1 可用；老版本没有）。
转换 = pandoc -f markdown+east_asian_line_breaks -t bbcode_steam + 以下修正
（pandoc 输出在 Steam 上的毛刺）:
  1) 破折号 — / – 会被 pandoc 转义成 ------ / --，先用占位符保护、转换后还原；
  2) 代码块会输出 [code=lang]，Steam 的 code 标签不接受参数，统一改回 [code]；
  3) east_asian_line_breaks 处理后若仍有「全角标点 + 空格」残留，清掉（不动 [code] 块内部）。
"""
import os
import re
import subprocess
import sys

PANDOC = os.environ.get("PANDOC", "/opt/data/tools/pandoc/bin/pandoc")
DASHES = ("\u2014", "\u2013")  # em dash — / en dash –


def fix_punct(text):
    """全角标点后的折行空格清理（跳过 code 块内部）。"""
    parts = re.split(r"(\[code\].*?\[/code\])", text, flags=re.S)
    for i, p in enumerate(parts):
        if not p.startswith("[code]"):
            parts[i] = re.sub(r"([，、。；：]) +", r"\1", p)
    return "".join(parts)


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    src = sys.argv[1]
    dst = sys.argv[2] if len(sys.argv) > 2 else os.path.splitext(src)[0] + ".steam.txt"

    with open(src, encoding="utf-8") as f:
        md = f.read()

    # 1) 保护破折号
    for i, ch in enumerate(DASHES):
        md = md.replace(ch, "@@DASH%d@@" % i)
    tmp = os.path.join(os.environ.get("TMPDIR", "/tmp"), "md2steam_src.md")
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(md)

    r = subprocess.run([PANDOC, tmp, "-f", "markdown+east_asian_line_breaks",
                        "-t", "bbcode_steam"],
                       capture_output=True, text=True)
    if r.returncode != 0:
        sys.stderr.write(r.stderr)
        sys.exit(r.returncode)
    out = r.stdout

    # 2) code 块去语言参数
    out = re.sub(r"\[code=[^\]]*\]", "[code]", out)
    # 3) 还原破折号
    for i, ch in enumerate(DASHES):
        out = out.replace("@@DASH%d@@" % i, ch)
    # 4) 标点空格清理
    out = fix_punct(out)

    with open(dst, "w", encoding="utf-8") as f:
        f.write(out)
    print("OK: %s  (%d chars)" % (dst, len(out)))


if __name__ == "__main__":
    main()
