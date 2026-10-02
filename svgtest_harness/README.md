# svgtest_harness — SvgExporter 离线验证

把 `../src/SvgExporter.cs` 的渲染核心**自动抽取**成独立程序并在 .NET 下真跑，
再用 `verify.py` 对生成的 SVG 做 31 项断言：

- XML 合法性 / UTF-8 / 无 BOM / 根元素
- 元素计数（path / text / rect）、全部 round cap
- 两种模式（道路 / 轨道）的标注规则：阈值、最小长度、>60% 垂直过渡段排除
- 配色：四档坡度色 + 路肩来源色（原版/自制/mod）+ 其他网络类别色
- 图例实体转义（&lt; / &gt;）与解析还原
- 坐标不越界（含边缘留白）

## 运行

```bash
cd svgtest_harness
python3 extract.py    # 1) 抽取 SvgExporter.cs 渲染段 → 生成 Program.cs（生成物，不入库）
dotnet run            # 2) 编译执行，输出 out/slope_*.svg + 控制台日志
python3 verify.py     # 3) 31 项断言，末行 "0 failed" 为通过
```

依赖：python3、任一近代 .NET SDK（csproj 目标 net8.0，如版本不符可改 `svgtest.csproj` 的 TargetFramework）。

## 注意

- `extract.py` 依赖 `SvgExporter.cs` 的分段注释标记（`// ================= 渲染` 等）
  与 `private struct Seg`、`RenderThread` 等成员命名；**修改源码结构时需同步更新 extract.py**。
- `Program.cs`、`bin/`、`obj/`、`out/` 均为生成物，已由 .gitignore 排除。
