#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""RoadSlopeViewer 封面图生成器 → 输出 SVG（用宿主机 ffmpeg/librsvg 渲染 PNG）。

用法:
    python3 gen_cover.py          # 生成 16:9 与方形两版 SVG
    python3 gen_cover.py 16x9     # 只生成 16:9

设计: 深色渐变背景 + 坡度测量示意图（路面色带 / 水平·垂直参考线 / 坡度标签）
      + 标题文字；底部一条绿→黄→橙→红渐变色带呼应 mod 的坡度配色。
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
FA = "'Noto Sans CJK SC','Noto Sans','Liberation Sans',sans-serif"
GREEN, YELLOW, ORANGE, RED = "#38d97a", "#f5d54a", "#ff9636", "#ff4242"
C_SUB, C_META, C_NOTE, C_FORMULA = "#b9cfe2", "#93a6ba", "#93a9bf", "#a9bed2"


def dots(w, h, step=48):
    out = []
    y = step / 2.0
    while y < h:
        x = step / 2.0
        while x < w:
            out.append('<circle cx="%.0f" cy="%.0f" r="1.6" fill="#ffffff" fill-opacity="0.05"/>' % (x, y))
            x += step
        y += step
    return "\n".join(out)


def defs(w, h, ax, ay, bx, by, warm_cx, warm_cy, cold_cx, cold_cy):
    return f'''<defs>
  <linearGradient id="bg" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="{w}" y2="{h}">
    <stop offset="0" stop-color="#0a0f1e"/>
    <stop offset="0.52" stop-color="#0f2534"/>
    <stop offset="1" stop-color="#0d3d42"/>
  </linearGradient>
  <linearGradient id="titleGrad" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="#ffffff"/>
    <stop offset="1" stop-color="#c9e4ff"/>
  </linearGradient>
  <linearGradient id="roadGrad" gradientUnits="userSpaceOnUse" x1="{ax}" y1="{ay}" x2="{bx}" y2="{by}">
    <stop offset="0" stop-color="{GREEN}"/>
    <stop offset="0.36" stop-color="{YELLOW}"/>
    <stop offset="0.68" stop-color="{ORANGE}"/>
    <stop offset="1" stop-color="{RED}"/>
  </linearGradient>
  <linearGradient id="band" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="{w}" y2="0">
    <stop offset="0" stop-color="{GREEN}"/>
    <stop offset="0.35" stop-color="{YELLOW}"/>
    <stop offset="0.68" stop-color="{ORANGE}"/>
    <stop offset="1" stop-color="{RED}"/>
  </linearGradient>
  <radialGradient id="glowWarm" cx="0.5" cy="0.5" r="0.5">
    <stop offset="0" stop-color="#ff8c3c" stop-opacity="0.13"/>
    <stop offset="1" stop-color="#ff8c3c" stop-opacity="0"/>
  </radialGradient>
  <radialGradient id="glowCool" cx="0.5" cy="0.5" r="0.5">
    <stop offset="0" stop-color="#2fd8c8" stop-opacity="0.10"/>
    <stop offset="1" stop-color="#2fd8c8" stop-opacity="0"/>
  </radialGradient>
</defs>
<rect width="{w}" height="{h}" fill="url(#bg)"/>
<circle cx="{warm_cx}" cy="{warm_cy}" r="{int(w*0.29)}" fill="url(#glowWarm)"/>
<circle cx="{cold_cx}" cy="{cold_cy}" r="{int(w*0.27)}" fill="url(#glowCool)"/>
{dots(w, h)}'''


def diagram(ax, ay, bx, by, label_cx, label_cy, road_w, lab_w, lab_h, fs_label):
    """坡度测量示意图：路面（渐变粗线）+ 水平/垂直参考虚线 + 直角符号 + 数值标签。"""
    hx = bx                    # 垂直参考线 x
    hy = ay                    # 水平参考线 y
    midx = (ax + bx) / 2.0
    midy = (ay + by) / 2.0
    lab_x, lab_y = label_cx - lab_w / 2.0, label_cy - lab_h / 2.0
    guide_top = lab_y + lab_h + 12                        # 引导线从标签下方 12px 起
    guide_bot = midy - road_w * 0.28 - 6                  # 止于路面上缘
    return f'''<line x1="{ax}" y1="{ay}" x2="{bx}" y2="{by}" stroke="url(#roadGrad)" stroke-width="{int(road_w*1.2)}" stroke-linecap="round" opacity="0.16"/>
<line x1="{ax}" y1="{ay}" x2="{bx}" y2="{by}" stroke="url(#roadGrad)" stroke-width="{road_w}" stroke-linecap="round" stroke-opacity="0.98"/>
<line x1="{ax}" y1="{ay}" x2="{hx}" y2="{hy}" stroke="#ffffff" stroke-opacity="0.38" stroke-width="3" stroke-dasharray="14 10"/>
<line x1="{hx}" y1="{hy}" x2="{hx}" y2="{by}" stroke="#ffffff" stroke-opacity="0.38" stroke-width="3" stroke-dasharray="14 10"/>
<path d="M {hx-20} {hy} L {hx-20} {hy-22} L {hx} {hy-22}" fill="none" stroke="#ffffff" stroke-opacity="0.4" stroke-width="2.5"/>
<circle cx="{ax}" cy="{ay}" r="{int(road_w*0.25)}" fill="#ffffff" fill-opacity="0.95"/>
<circle cx="{bx}" cy="{by}" r="{int(road_w*0.25)}" fill="#ffffff" fill-opacity="0.95"/>
<line x1="{label_cx}" y1="{guide_top}" x2="{label_cx}" y2="{guide_bot:.0f}" stroke="#ffffff" stroke-opacity="0.5" stroke-width="2.5" stroke-dasharray="6 6"/>
<circle cx="{label_cx}" cy="{guide_bot:.0f}" r="3.5" fill="#ffffff" fill-opacity="0.7"/>
<rect x="{lab_x:.0f}" y="{lab_y:.0f}" width="{lab_w}" height="{lab_h}" rx="16" fill="#0e1621" fill-opacity="0.96" stroke="{ORANGE}" stroke-width="3.5"/>
<text x="{label_cx}" y="{label_cy + fs_label*0.36:.0f}" font-family="{FA}" font-size="{fs_label}" font-weight="700" fill="{ORANGE}" text-anchor="middle">12%</text>'''


def cover_169():
    W, H = 1920, 1080
    ax, ay, bx, by = 360, 880, 1560, 500
    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}">']
    parts.append(defs(W, H, ax, ay, bx, by, 1560, 170, 240, 980))
    # 标题区
    parts.append(f'<text x="960" y="215" font-family="{FA}" font-size="118" font-weight="700" fill="url(#titleGrad)" text-anchor="middle" letter-spacing="1">RoadSlopeViewer</text>')
    parts.append(f'<text x="960" y="300" font-family="{FA}" font-size="52" fill="{C_SUB}" text-anchor="middle" letter-spacing="8">道路坡度查看器</text>')
    parts.append(f'<text x="960" y="356" font-family="{FA}" font-size="27" fill="{C_META}" text-anchor="middle" letter-spacing="2">坡度覆盖层 · 点选明细 · 整城 SVG 导出</text>')
    # 示意图
    parts.append(diagram(ax, ay, bx, by, 960, 546, 30, 150, 74, 46))
    # 标注
    parts.append(f'<text x="960" y="935" font-family="{FA}" font-size="30" fill="{C_NOTE}" text-anchor="middle" letter-spacing="2">水平距离 L</text>')
    parts.append(f'<text x="1588" y="700" font-family="{FA}" font-size="30" fill="{C_NOTE}" text-anchor="start" letter-spacing="1">高差 Δh</text>')
    parts.append(f'<text x="960" y="825" font-family="{FA}" font-size="32" fill="{C_FORMULA}" text-anchor="middle" letter-spacing="1">坡度 = 高差 ÷ 水平距离 × 100%</text>')
    # 底部
    parts.append(f'<text x="960" y="996" font-family="{FA}" font-size="26" fill="#7b93a8" text-anchor="middle" letter-spacing="3">for Cities: Skylines</text>')
    parts.append(f'<rect x="0" y="1034" width="{W}" height="16" fill="url(#band)"/>')
    parts.append('</svg>')
    return "\n".join(parts)


def cover_square():
    W = H = 1080
    ax, ay, bx, by = 150, 790, 890, 546
    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}">']
    parts.append(defs(W, H, ax, ay, bx, by, 900, 140, 180, 950))
    parts.append(f'<text x="540" y="150" font-family="{FA}" font-size="86" font-weight="700" fill="url(#titleGrad)" text-anchor="middle" letter-spacing="0">RoadSlopeViewer</text>')
    parts.append(f'<text x="540" y="222" font-family="{FA}" font-size="44" fill="{C_SUB}" text-anchor="middle" letter-spacing="7">道路坡度查看器</text>')
    parts.append(f'<text x="540" y="268" font-family="{FA}" font-size="23" fill="{C_META}" text-anchor="middle" letter-spacing="1">坡度覆盖层 · 点选明细 · 整城 SVG 导出</text>')
    parts.append(diagram(ax, ay, bx, by, 540, 500, 26, 132, 66, 40))
    parts.append(f'<text x="540" y="845" font-family="{FA}" font-size="27" fill="{C_NOTE}" text-anchor="middle" letter-spacing="2">水平距离 L</text>')
    parts.append(f'<text x="935" y="670" font-family="{FA}" font-size="25" fill="{C_NOTE}" text-anchor="start" letter-spacing="1">高差 Δh</text>')
    parts.append(f'<text x="540" y="890" font-family="{FA}" font-size="27" fill="{C_FORMULA}" text-anchor="middle" letter-spacing="1">坡度 = 高差 ÷ 水平距离 × 100%</text>')
    parts.append(f'<text x="540" y="996" font-family="{FA}" font-size="24" fill="#7b93a8" text-anchor="middle" letter-spacing="3">for Cities: Skylines</text>')
    parts.append(f'<rect x="0" y="1034" width="{W}" height="16" fill="url(#band)"/>')
    parts.append('</svg>')
    return "\n".join(parts)


def main():
    which = sys.argv[1] if len(sys.argv) > 1 else "all"
    if which in ("all", "16x9"):
        p = os.path.join(HERE, "cover_16x9.svg")
        with open(p, "w", encoding="utf-8") as f:
            f.write(cover_169())
        print("OK:", p)
    if which in ("all", "square"):
        p = os.path.join(HERE, "cover_square.svg")
        with open(p, "w", encoding="utf-8") as f:
            f.write(cover_square())
        print("OK:", p)


if __name__ == "__main__":
    main()
