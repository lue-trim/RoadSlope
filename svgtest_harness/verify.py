#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""验证 svgtest harness 产出的 SVG：XML 合法性 + 元素统计 + 规则断言。"""
import sys, os, glob
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, 'out')
SVG_NS = '{http://www.w3.org/2000/svg}'

results = []
def check(name, cond, detail=""):
    results.append((name, bool(cond), detail))
    print(('PASS' if cond else 'FAIL') + ' | ' + name + (' | ' + detail if detail else ''))

road_files = sorted(glob.glob(OUT + '/slope_road_*.svg'))
rail_files = sorted(glob.glob(OUT + '/slope_rail_*.svg'))
check('road 文件存在', len(road_files) >= 1, str(road_files))
check('rail 文件存在', len(rail_files) >= 1, str(rail_files))

def analyze(path, mode):
    print('\n=== ' + os.path.basename(path) + ' ===')
    raw = open(path, encoding='utf-8').read()
    check(mode + ': 声明 UTF-8', 'encoding="UTF-8"' in raw)
    check(mode + ': 无 BOM', not raw.startswith('\ufeff'))
    try:
        root = ET.fromstring(raw)
    except Exception as e:
        check(mode + ': XML 解析', False, str(e))
        return
    check(mode + ': XML 解析', True)
    check(mode + ': 根元素 svg', root.tag == SVG_NS + 'svg')
    w, h = root.get('width'), root.get('height')
    check(mode + ': width/height', w == '6000', 'width=' + str(w) + ' height=' + str(h))

    paths = root.findall('.//' + SVG_NS + 'path')
    texts = root.findall('.//' + SVG_NS + 'text')
    rects = root.findall('.//' + SVG_NS + 'rect')
    linecaps = sum(1 for p in paths if p.get('stroke-linecap') == 'round')
    check(mode + ': path 全部 round cap', linecaps == len(paths), str(linecaps) + '/' + str(len(paths)))

    # 坐标不越界（所有 d 数字对应在 [0,W]×[0,H]+ε 内）
    import re as _re
    wf, hf = float(w), float(h)
    oob = 0
    for p in paths:
        nums = [float(v) for v in _re.findall(r'-?\d+\.?\d*', p.get('d') or '')]
        for i in range(0, len(nums) - 1, 2):
            x, y = nums[i], nums[i + 1]
            if x < -0.01 or x > wf + 0.01 or y < -0.01 or y > hf + 0.01:
                oob += 1
    check(mode + ': 路径坐标不越界', oob == 0, 'oob=' + str(oob))

    # 标注（含 % 的 text、非图例——图例文字含 "数字 = 坡度"）
    pct_labels = [t.text for t in texts if t.text and t.text.endswith('%')]
    print('  路径数=%d, text数=%d, rect数=%d, 标标注=%s' % (len(paths), len(texts), len(rects), pct_labels))

    # 颜色统计
    strokes = {}
    for p in paths:
        c = p.get('stroke')
        strokes[c] = strokes.get(c, 0) + 1
    print('  stroke 颜色统计:', strokes)

    # 图例转义验证（原始文本里应是 &lt; / &gt; 实体）
    esc_needle = '&lt;8%' if mode == 'road' else '&lt;3%'
    esc_needle2 = '&gt;20%' if mode == 'road' else '&gt;10%'
    check(mode + ': 图例实体转义', esc_needle in raw and esc_needle2 in raw,
          'needle=' + esc_needle + ',' + esc_needle2)
    # 解析后应还原为 < / >
    has_lt = any('<' in (t.text or '') for t in texts)
    check(mode + ': 解析后呈 < 符号', has_lt)
    check(mode + ': 城市名在信息行', any('测试城市' in (t.text or '') for t in texts))
    return len(paths), pct_labels, strokes

r1 = analyze(road_files[-1], 'road')
r2 = analyze(rail_files[-1], 'rail')

if r1:
    npaths, labels, strokes = r1
    check('road: 20 条 path（6车段×3+2其他）', npaths == 20, str(npaths))
    check('road: 仅 1 个标注=25%', labels == ['25%'], str(labels))
    check('road: 含 25% 红色 #c92a2a', strokes.get('#c92a2a', 0) >= 1, str(strokes))
    check('road: 含 RBR 缘色 #c43d3d', strokes.get('#c43d3d', 0) >= 2, str(strokes))
    check('road: 含管线色 #3f4f6e', strokes.get('#3f4f6e', 0) >= 1, str(strokes))

if r2:
    npaths, labels, strokes = r2
    check('rail: 4 条 path（轨道1+共线3）', npaths == 4, str(npaths))
    check('rail: 仅 1 个标注=12%', labels == ['12%'], str(labels))
    check('rail: 12% 按轨道标准应为红 #c92a2a（>10%）', strokes.get('#c92a2a', 0) >= 1, str(strokes))
    check('rail: 共线 3% 应为黄 #fcc419（3-6%）', strokes.get('#fcc419', 0) >= 1, str(strokes))

fails = [r for r in results if not r[1]]
print('\n%d checks, %d failed' % (len(results), len(fails)))
sys.exit(1 if fails else 0)
