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
                BuildKeyGroup(helper);
                BuildExportGroup(helper);
            }
            catch (Exception e)
            {
                Debug.LogError("[RoadSlopeViewer] OnSettingsUI 构建失败: " + e);
                try { helper.AddGroup("道路坡度查看器 — 设置界面初始化失败（详见 output_log.txt）"); } catch { }
            }
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

    /// <summary>全局心跳：把后台导出完成的通知转回主线程 UI（任何场景都跑）。</summary>
    internal class RsvPump : ThreadingExtensionBase
    {
        public override void OnUpdate(float realTimeDelta, float simulationTimeDelta)
        {
            try { SvgExporter.Pump(); }
            catch { }
        }
    }
}
