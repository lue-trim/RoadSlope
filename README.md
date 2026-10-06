# RoadSlopeViewer · 道路坡度查看器

Cities: Skylines 1 本地 mod：在游戏内查看道路 / 轨道交通坡度，并一键导出整城坡度 SVG 图。

## 功能

- **坡度覆盖层**：镜头附近的陡坡路段实时显示坡度百分比标签（颜色分级，坡度越大越红）
- **点选明细**：左键点击路面/轨道 → 坡度、高差、水平长度、游戏路长、资产名、两端海拔，
  并汇总整条连通线路（段数 / 总长 / 平均坡度 / 最大单段坡度）
- **SVG 导出**（选项 → 模组设置）：道路坡度图 / 轨道交通坡度图两种图；
  配色与标注规则与离线工具 `map_export.py` 一致；后台线程生成，不卡游戏；
  输出到 mod 目录的 `Exports` 子文件夹
- **快捷键**：显示/隐藏面板（默认 Alt+R，可在「选项 → 模组设置」中修改）
- 面板开关（道路/轨道/覆盖层/陡坡/点选）持久化，重进游戏保持
- 覆盖层可调参数（选项 → 模组设置）：标签可视距离 / 最大标签数 / 道路与轨道各自的配色阈值

## 安装

1. 取得 `RoadSlopeViewer.dll`（从 `RoadSlopeViewer-v*.zip` 交付包取，或按下方自行构建）
2. 放到：`%LOCALAPPDATA%\Colossal Order\Cities_Skylines\Addons\Mods\RoadSlopeViewer\`
3. 游戏 → 内容管理 → 模组 → 勾选启用

## 构建

```bash
bash build.sh     # 输出 out/RoadSlopeViewer.dll（net35）
# 环境变量可覆盖默认路径：MANAGED=<游戏 Managed 目录>  DOTNET_DIR=<dotnet 安装目录>
```

## 验证

- `svgtest_harness/`：离线验证工具 —— 从 `src/SvgExporter.cs` 自动抽取渲染核心并真跑，
  31 项断言（XML 合法性 / 元素计数 / 标注规则 / 配色 / 转义 / 坐标不越界）。用法见其 README。
- 编译产物 API 级校验：`ilspycmd` 反编译检查关键调用是否在 IL 中。

## 版本

- **v0.7**：坡度高差改用道路两端真实连接点（`NetLane.m_bezier` 端点，落在节点边缘）
  而非节点中心——Node Controller 把节点改成倾斜面后，节点中心高差会大于道路实际高差
  导致坡度偏大；游戏内面板与 SVG 导出同步
- **v0.6**：坡度算法修正——坡度 = 高差 ÷ 实际道路长度（`m_averageLength`，各车道曲线
  弧长均值），曲线段不再按两点直线距离高估坡度；游戏内查看与 SVG 导出同步；明细面板
  同时显示路长（实际）与直线距离
- **v0.5**：修复覆盖层黑框（误用 MenuPanel 贴图）与导出完成通知不达（扩展类必须 public，
  `GetExportedTypes` 只实例化 public 类型）；新增可调选项：标签可视距离 / 最大标签数 /
  道路与轨道各自的配色阈值（各 3 档）
- **v0.4**：UI 规范化（官方 UIDragHandle 拖动 / 组件命名 / tooltip / 开关持久化）；
  快捷键绑定与 SVG 导出移入「选项 → 模组设置」；修复两个 bug（改键不持久化、按住快捷键连翻）
- v0.3 / v0.2 / v0.1：历史版本（v0.3 源码快照见 `src/RoadSlopeViewer.v0.3.bak.cs` 与 git 历史）

## 结构

```
src/                mod 源码（C#，net35）
build.sh            编译脚本（对游戏 Managed 程序集）
svgtest_harness/    渲染逻辑离线验证（python + dotnet）
dist/               分发文件（打包交付用，不入库）
```
