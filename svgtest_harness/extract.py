#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从 src/SvgExporter.cs 自动提取渲染核心，生成可独立执行的测试程序（SvgX + Main）。
仅做：片段提取 + Unity 依赖最小替换（Mathf→Math、Debug→Console）+ 可见性放宽。
渲染逻辑本身逐字搬运，保证与 mod 源文件同源。
用法: python3 extract.py && dotnet run && python3 verify.py
"""
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))          # svgtest_harness/
BASE = os.path.dirname(HERE)                               # 仓库根
SRC = os.path.join(BASE, 'src', 'SvgExporter.cs')
OUT = os.path.join(HERE, 'Program.cs')
OUT_DIR = os.path.join(HERE, 'out').replace('\\', '/')

src = open(SRC, encoding='utf-8').read()
lines = src.split('\n')

def find_line(tag):
    for i, l in enumerate(lines):
        if tag in l:
            return i
    raise Exception('not found: ' + tag)

i_data   = find_line('public static bool LevelReady;')
i_entry  = find_line('================= 入口')
i_render = find_line('================= 渲染')
i_pump   = find_line('================= Pump')
i_tools  = find_line('================= 工具')

data   = '\n'.join(lines[i_data:i_entry])
render = '\n'.join(lines[i_render:i_pump])

tools_lines = lines[i_tools:]
while tools_lines and tools_lines[-1].strip() == '':
    tools_lines.pop()
assert tools_lines[-1].strip() == '}'
assert tools_lines[-2].strip() == '}'
tools_lines = tools_lines[:-2]
tools = '\n'.join(tools_lines)

def conv(t):
    t = t.replace('Debug.LogError(', 'Console.WriteLine(')
    t = t.replace('Debug.Log(', 'Console.WriteLine(')
    t = re.sub(r'Mathf\.Sqrt\(([^()]*)\)', r'(float)Math.Sqrt(\1)', t)
    t = t.replace('Mathf.Max', 'Math.Max')
    return t

data, render, tools = conv(data), conv(render), conv(tools)

# 删除 Horiz（仅 Collect 使用，依赖 Vector3）
horiz = """        private static float Horiz(Vector3 a, Vector3 b)
        {
            float dx = b.x - a.x, dz = b.z - a.z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

"""
assert horiz in tools, 'Horiz block not found'
tools = tools.replace(horiz, '')

# 可见性放宽（测试程序需要构造 Seg / 调 RenderThread）
render = render.replace('private static void RenderThread', 'public static void RenderThread')
data = data.replace('private struct Seg', 'public struct Seg')
data = data.replace('private class Item', 'public class Item')
data = data.replace('private struct Map2Svg', 'public struct Map2Svg')

main_code = '''
    public static class Program
    {
        public static int Main()
        {
            List<SvgX.Seg> segs = new List<SvgX.Seg>();
            // 1 直路 vanilla 5%，不标注
            segs.Add(Mk(0, 0, 0, 90, 0, 210, 0, 300, 5f, 300f, 40f, 8f, 0, true, false));
            // 2 RBR 陡坡 25% 长200m -> 标注
            segs.Add(Mk(100, 0, 160, 0, 240, 0, 300, 0, 25f, 200f, 50f, 10f, 1, true, false));
            // 3 弯道 mod 10%
            segs.Add(Mk(400, 0, 420, 80, 480, 140, 500, 200, 10f, 220f, 60f, 12f, 2, true, false));
            // 4 轨道 12% 150m -> rail 模式标注
            segs.Add(Mk(600, 0, 600, 45, 600, 105, 600, 150, 12f, 150f, 45f, 5f, 3, false, true));
            // 5 电车共线道路 3%（rail 模式保留）
            segs.Add(Mk(700, 0, 730, 0, 770, 0, 800, 0, 3f, 100f, 30f, 8f, 0, true, true));
            // 6 管线 0%
            segs.Add(Mk(900, 0, 930, 0, 970, 0, 1000, 0, 0f, 100f, 30f, 2f, 5, false, false));
            // 7 垂直过渡 70% -> 即使很长也不标注
            segs.Add(Mk(1100, 0, 1100, 30, 1100, 70, 1100, 100, 70f, 100f, 30f, 8f, 0, true, false));
            // 8 缓坡 19% -> 不标注
            segs.Add(Mk(1200, 0, 1230, 0, 1270, 0, 1300, 0, 19f, 100f, 30f, 8f, 0, true, false));

            SvgX.RenderThread(segs, false, true, "测试城市", "__OUTDIR__");
            SvgX.RenderThread(segs, true, true, "测试城市", "__OUTDIR__");

            string[] files = Directory.GetFiles("__OUTDIR__");
            Console.WriteLine("OUTPUT FILES: " + files.Length);
            for (int i = 0; i < files.Length; i++) Console.WriteLine("  " + files[i]);
            return 0;
        }

        private static SvgX.Seg Mk(float x1, float z1, float cx1, float cz1, float cx2, float cz2, float x2, float z2,
            float pct, float dist, float yavg, float w, int cat, bool car, bool railKeep)
        {
            SvgX.Seg s = new SvgX.Seg();
            s.x1 = x1; s.z1 = z1; s.cx1 = cx1; s.cz1 = cz1; s.cx2 = cx2; s.cz2 = cz2; s.x2 = x2; s.z2 = z2;
            s.pct = pct; s.dist = dist; s.yavg = yavg; s.w = w; s.cat = cat; s.car = car; s.railKeep = railKeep;
            return s;
        }
    }
}
'''
main_code = main_code.replace('__OUTDIR__', OUT_DIR)

header = '''using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace SvgTest
{
    internal static class SvgX
    {
'''

out = header + data + '\n' + render + '\n' + tools + '\n    }\n\n' + main_code
open(OUT, 'w', encoding='utf-8').write(out)
print('generated:', OUT, len(out), 'chars')
