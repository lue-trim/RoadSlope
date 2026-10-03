// RoadSlopeViewer — 选项面板（IUserMod.OnSettingsUI）与持久化设置
//
// 依据官方 mod wiki《Mod Options Panel》与《Modding API》实现：
//   - 实现 IUserMod.OnSettingsUI(UIHelperBase) → 条目出现在 选项 → 模组设置；
//   - 可用的控件只有 AddGroup/AddButton/AddSpace/AddCheckbox/AddSlider/AddDropdown/AddTextfield；
//   - UIHelper.AddButton 实际创建并返回 UIButton 实例（反编译实锤），
//     因此可以 cast 后实时更新按钮文字（改键交互依赖这一点）。
//
// 改键交互完全照抄游戏原版 OptionsKeymappingPanel：
//   点击按钮 → 按钮文本变“请按新键” → UIButton.Focus() + UIView.PushModal(btn)
//   → 按钮 eventKeyDown 捕获 → p.Use() + UIView.PopModal() → SavedInputKey.value 保存。
//   Esc 取消；Backspace 清除绑定。
//
// 持久化说明（v0.4 修复）：SavedValue 的文件名必须能被 GameSettings 找到
// （SavedValue.settingsFile == null 时 setter 静默丢弃，v0.3 用了未注册的
//  "RoadSlopeViewer" 文件名所以没存上）。这里统一用游戏已注册的
//  Settings.gameSettingsFile（= "gameSettings"，与原版键位同文件），键名 rsv_ 前缀防冲突。
using System;
using ColossalFramework;
using ColossalFramework.Plugins;
using ColossalFramework.UI;
using ICities;
using UnityEngine;

namespace RoadSlopeViewer
{
    /// <summary>集中管理的持久化设置（写入 gameSettings 文件）。</summary>
    internal static class Rsv
    {
        public static readonly SavedInputKey ToggleKey =
            new SavedInputKey("rsv_toggleKey", Settings.gameSettingsFile, KeyCode.R, false, false, true, true);

        public static readonly SavedBool CbRoad =
            new SavedBool("rsv_cb_road", Settings.gameSettingsFile, true, true);
        public static readonly SavedBool CbRail =
            new SavedBool("rsv_cb_rail", Settings.gameSettingsFile, false, true);
        public static readonly SavedBool CbOverlay =
            new SavedBool("rsv_cb_overlay", Settings.gameSettingsFile, true, true);
        public static readonly SavedBool CbSteep =
            new SavedBool("rsv_cb_steep", Settings.gameSettingsFile, true, true);
        public static readonly SavedBool CbInspect =
            new SavedBool("rsv_cb_inspect", Settings.gameSettingsFile, true, true);

        public static readonly SavedInt ExportMode =
            new SavedInt("rsv_export_mode", Settings.gameSettingsFile, 0, true);
        public static readonly SavedBool ExportLabels =
            new SavedBool("rsv_export_labels", Settings.gameSettingsFile, true, true);

        // 标签显示（游戏内覆盖层）
        public static readonly SavedInt LabelDist =
            new SavedInt("rsv_label_dist", Settings.gameSettingsFile, 420, true);   // 可视距离（米）
        public static readonly SavedInt LabelMax =
            new SavedInt("rsv_label_max", Settings.gameSettingsFile, 90, true);     // 同时显示标签上限
        // 标签颜色阈值（%）：绿→黄 / 黄→橙 / 橙→红
        public static readonly SavedInt RoadT1 =
            new SavedInt("rsv_road_t1", Settings.gameSettingsFile, 8, true);
        public static readonly SavedInt RoadT2 =
            new SavedInt("rsv_road_t2", Settings.gameSettingsFile, 12, true);
        public static readonly SavedInt RoadT3 =
            new SavedInt("rsv_road_t3", Settings.gameSettingsFile, 20, true);
        public static readonly SavedInt RailT1 =
            new SavedInt("rsv_rail_t1", Settings.gameSettingsFile, 3, true);
        public static readonly SavedInt RailT2 =
            new SavedInt("rsv_rail_t2", Settings.gameSettingsFile, 6, true);
        public static readonly SavedInt RailT3 =
            new SavedInt("rsv_rail_t3", Settings.gameSettingsFile, 10, true);

        /// <summary>当前快捷键的显示名（本地化，失败回退枚举名）。</summary>
        public static string KeyLabel()
        {
            try
            {
                if (ToggleKey.Key == KeyCode.None) return "（未绑定）";
                string s = ToggleKey.ToLocalizedString("KEYNAME");
                return string.IsNullOrEmpty(s) ? "?" : s;
            }
            catch { return "?"; }
        }
    }

    /// <summary>选项面板 UI 构建（选项 → 模组设置 → 道路坡度查看器）。</summary>
    internal static class OptionsUI
    {
        private static UIButton _keyBtn;
        private static UIButton _exportBtn;
        private static bool _editingKey;

        public static void Build(UIHelperBase helper)
        {
            try
            {
                BuildLabelGroups(helper);
                BuildKeyGroup(helper);
                BuildExportGroup(helper);
            }
            catch (Exception e)
            {
                Debug.LogError("[RoadSlopeViewer] OnSettingsUI 构建失败: " + e);
                try { helper.AddGroup("道路坡度查看器 — 设置界面初始化失败（详见 output_log.txt）"); } catch { }
            }
        }

        // ---------- 标签显示与配色阈值（游戏内覆盖层） ----------

        private static void BuildLabelGroups(UIHelperBase helper)
        {
            UIHelperBase g = helper.AddGroup("标签显示（游戏内覆盖层）");
            AddIntSlider(g, "标签可视距离", " 米", 100, 2000, 25, Rsv.LabelDist, "RSV_SlDist",
                "只标注相机附近这个半径内的路段。");
            AddIntSlider(g, "最大显示标签数", " 个", 10, 400, 10, Rsv.LabelMax, "RSV_SlMax",
                "屏幕上同时显示的坡度标签上限（调整后立即生效）。");

            BuildThresholdGroup(helper, "标签配色阈值（道路）", Rsv.RoadT1, Rsv.RoadT2, Rsv.RoadT3,
                "道路标签 4 档配色的分界：低于第 1 档为绿，超过第 3 档为红（默认 8/12/20，参照现实市区限坡）。");
            BuildThresholdGroup(helper, "标签配色阈值（轨道交通）", Rsv.RailT1, Rsv.RailT2, Rsv.RailT3,
                "轨道交通标签 4 档配色的分界（默认 3/6/10，参照现实铁路限坡）。");
        }

        private static void BuildThresholdGroup(UIHelperBase helper, string groupName,
            SavedInt t1, SavedInt t2, SavedInt t3, string tip)
        {
            UIHelperBase g = helper.AddGroup(groupName);
            AddIntSlider(g, "绿→黄分界", "%", 1, 60, 1, t1, null, tip);
            AddIntSlider(g, "黄→橙分界", "%", 1, 60, 1, t2, null, tip);
            AddIntSlider(g, "橙→红分界", "%", 1, 60, 1, t3, null, tip);
        }

        /// <summary>整数滑块：标题实时显示当前值；值写入 SavedInt（选项面板重建时恢复）。</summary>
        private static void AddIntSlider(UIHelperBase g, string name, string unit,
            int min, int max, int step, SavedInt store, string compName, string tip)
        {
            UISlider sl = null;
            sl = g.AddSlider(name, min, max, step, store.value, delegate(float v)
            {
                int iv = Mathf.RoundToInt(v);
                store.value = iv;
                UpdateSliderText(sl, name, iv, unit);
            }) as UISlider;
            if (sl == null) return;
            try
            {
                sl.name = string.IsNullOrEmpty(compName) ? ("RSV_Slider_" + name) : compName;
                if (!string.IsNullOrEmpty(tip)) sl.tooltip = tip;
            }
            catch { }
            UpdateSliderText(sl, name, store.value, unit);
        }

        /// <summary>把滑块标题更新为“名称：当前值”（模板内 "Label" 子组件；找不到则静默）。</summary>
        private static void UpdateSliderText(UISlider sl, string name, float v, string unit)
        {
            if (sl == null) return;
            try
            {
                UILabel lab = FindLabelUp(sl);
                if (lab != null) lab.text = name + "：" + Mathf.RoundToInt(v) + unit;
            }
            catch { }
        }

        private static UILabel FindLabelUp(UIComponent c)
        {
            try
            {
                UIComponent p = c.parent;
                for (int i = 0; i < 3 && p != null; i++)
                {
                    UILabel lab = p.Find<UILabel>("Label");
                    if (lab != null) return lab;
                    p = p.parent;
                }
            }
            catch { }
            return null;
        }

        // ---------- 快捷键 ----------

        private static void BuildKeyGroup(UIHelperBase helper)
        {
            UIHelperBase g = helper.AddGroup("快捷键");
            _keyBtn = g.AddButton(KeyBtnText(), OnKeyBtnClicked) as UIButton;
            if (_keyBtn != null)
            {
                _keyBtn.name = "RSV_KeyButton";
                _keyBtn.eventKeyDown += OnKeyBtnKeyDown;
                _keyBtn.tooltip = "点击后按下新的快捷键（可组合 Ctrl / Shift / Alt）。Esc 取消；退格键（Backspace）清除绑定。";
            }
        }

        private static string KeyBtnText()
        {
            return "显示/隐藏面板快捷键：" + Rsv.KeyLabel() + " — 点击更改";
        }

        private static void OnKeyBtnClicked()
        {
            if (_keyBtn == null) return;
            _editingKey = true;
            _keyBtn.text = "请按下新快捷键…（Esc 取消，Backspace 清除）";
            _keyBtn.Focus();
            UIView.PushModal(_keyBtn);   // 与原版 OptionsKeymappingPanel 相同的模态捕获
        }

        private static void OnKeyBtnKeyDown(UIComponent comp, UIKeyEventParameter p)
        {
            if (!_editingKey) return;
            KeyCode kc = p.keycode;
            // 单独按修饰键不算数（与游戏原版一致的 6 个修饰键）
            if (kc == KeyCode.LeftControl || kc == KeyCode.RightControl ||
                kc == KeyCode.LeftShift || kc == KeyCode.RightShift ||
                kc == KeyCode.LeftAlt || kc == KeyCode.RightAlt) return;

            p.Use();
            UIView.PopModal();
            _editingKey = false;
            try
            {
                if (kc == KeyCode.Escape)
                {
                    // 取消：不改动
                }
                else if (kc == KeyCode.Backspace)
                {
                    Rsv.ToggleKey.value = SavedInputKey.Empty;
                }
                else
                {
                    Rsv.ToggleKey.value = SavedInputKey.Encode(kc, p.control, p.shift, p.alt);
                }
            }
            catch (Exception e) { Debug.LogError("[RoadSlopeViewer] 保存快捷键失败: " + e); }

            if (_keyBtn != null) _keyBtn.text = KeyBtnText();
            DebugOutputPanel.AddMessage(PluginManager.MessageType.Message,
                "[RoadSlopeViewer] 显示/隐藏面板快捷键 = " + Rsv.KeyLabel());
        }

        // ---------- SVG 导出 ----------

        private static void BuildExportGroup(UIHelperBase helper)
        {
            UIHelperBase g = helper.AddGroup("SVG 导出（当前城市）");

            object cbLabels = g.AddCheckbox("标注陡坡路段（道路 >20%、轨道 >10%）", Rsv.ExportLabels.value, OnExportLabelsChanged);
            UICheckBox cb = cbLabels as UICheckBox;
            if (cb != null) cb.tooltip = "在超过阈值且足够长的路段旁标注坡度数字（与离线工具 map_export.py 规则一致）。";

            object dd = g.AddDropdown("图形类型",
                new string[] { "道路坡度图", "轨道交通坡度图" },
                Rsv.ExportMode.value, OnExportModeChanged);
            UIDropDown dp = dd as UIDropDown;
            if (dp != null) dp.tooltip = "道路坡度图 = 离线工具 --slope 等价（含轨道/管网等其他网络）；轨道交通坡度图 = --rail 等价（仅轨道与共线路）。";

            _exportBtn = g.AddButton("立即导出 SVG…", OnExportClicked) as UIButton;
            if (_exportBtn != null)
            {
                _exportBtn.name = "RSV_ExportButton";
                _exportBtn.tooltip = "整城道路/轨道网导出为 SVG（后台生成，不卡游戏）。输出到 mod 文件夹的 Exports 子目录。";
                // 上次导出结果（比如打开选项前刚导完）
                string done = SvgExporter.LastResultFile;
                if (!string.IsNullOrEmpty(done))
                {
                    _exportBtn.text = "已导出：" + done;
                    _exportBtn.tooltip = "点击可重新导出。文件：" + SvgExporter.LastResultPath;
                }
            }

            g.AddButton("打开导出文件夹", OnOpenFolder);
            g.AddSpace(6);
        }

        private static void OnExportLabelsChanged(bool v)
        {
            Rsv.ExportLabels.value = v;
        }

        private static void OnExportModeChanged(int i)
        {
            Rsv.ExportMode.value = i;
        }

        private static void OnExportClicked()
        {
            if (_exportBtn == null) return;
            if (SvgExporter.Busy)
            {
                SetExportBtn("正在导出，请稍候…（可继续游戏）", "上一次导出尚未完成。");
                return;
            }
            string err;
            if (!SvgExporter.Start(out err))
            {
                SetExportBtn("导出失败：" + err, err);
                return;
            }
            SetExportBtn("正在导出…（后台进行，可继续游戏）", "正在后台生成 SVG。");
        }

        private static void SetExportBtn(string text, string tooltip)
        {
            if (_exportBtn != null)
            {
                _exportBtn.text = text;
                if (!string.IsNullOrEmpty(tooltip)) _exportBtn.tooltip = tooltip;
            }
        }

        private static void OnOpenFolder()
        {
            try
            {
                string dir = SvgExporter.ExportDirPath();
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception e)
            {
                Debug.LogError("[RoadSlopeViewer] 打开导出文件夹失败: " + e);
            }
        }

        /// <summary>导出完成回调（主线程，由 SvgExporter.Pump 调用）。</summary>
        public static void NotifyExportDone(string fileName, string fullPath, bool ok)
        {
            if (_exportBtn != null)
            {
                if (ok)
                {
                    _exportBtn.text = "已导出：" + fileName;
                    _exportBtn.tooltip = "点击可重新导出。文件：" + fullPath;
                }
                else
                {
                    _exportBtn.text = "导出失败：" + fileName;
                    _exportBtn.tooltip = fileName;
                }
            }
        }
    }

    /// <summary>
    /// 全局心跳：把后台导出完成的通知转回主线程 UI（任何场景都跑）。
    /// 必须是 public：游戏扫描扩展类用 Assembly.GetExportedTypes()（只返回 public 类型，
    /// 反编译 PluginInfo.GetInstances 实证）——internal 会静默不实例化、OnUpdate 永不调用。
    /// </summary>
    public class RsvPump : ThreadingExtensionBase
    {
        public override void OnUpdate(float realTimeDelta, float simulationTimeDelta)
        {
            try { SvgExporter.Pump(); }
            catch { }
        }
    }
}
