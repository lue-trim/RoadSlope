// RoadSlopeViewer v0.4 — 城市天际线 CS1：在游戏内查看道路 / 轨道交通坡度
//
// v0.4 变更（依官方 mod wiki 规范 + 反编译实证整理）：
//   1) UI 规范化（参考 wiki UI Framework）：所有挂到 UIView 的组件都设置 name；
//      面板拖动改用官方 UIDragHandle（自带 constrainToScreen，不会拖出屏幕）；
//      控件悬停提示（tooltip）、贴图按 atlas 探测。
//   2) 快捷键绑定移入 选项 → 模组设置（IUserMod.OnSettingsUI）。
//      并修复 v0.3 持久化 bug：SavedInputKey 的文件名必须用已注册的
//      Settings.gameSettingsFile，v0.3 用了未注册的 "RoadSlopeViewer" 文件名，
//      导致改键实际不会写入设置文件（重启恢复默认）。
//   3) SVG 导出集成到选项面板（SvgExporter.cs）：一键导出整城坡度图，
//      复刻 cities_map/map_export.py --slope / --rail 的配色与标注规则。
//   4) 面板开关（道路/轨道/覆盖层/只看陡坡/点击查看）用 SavedBool 持久化。
//
// 游戏内面板仅保留查看功能；改键与导出入口都在 选项 → 模组设置 → 道路坡度查看器。
using System;
using System.Collections.Generic;
using ColossalFramework;
using ColossalFramework.UI;
using ICities;
using UnityEngine;

namespace RoadSlopeViewer
{
    public class ModInfo : IUserMod
    {
        public string Name { get { return "道路坡度查看器 Road Slope Viewer"; } }
        public string Description { get { return "查看道路/轨道交通坡度：坡度覆盖层、点选明细、整城 SVG 导出。快捷键与导出设置在 选项 → 模组设置 中调整。"; } }

        public void OnSettingsUI(UIHelperBase helper)
        {
            OptionsUI.Build(helper);
        }
    }

    public class SlopeLoader : ILoadingExtension
    {
        public void OnCreated(ILoading loading) { }
        public void OnReleased() { }

        public void OnLevelLoaded(LoadMode mode)
        {
            SvgExporter.LevelReady = true;
            SlopeUI.Create();
        }

        public void OnLevelUnloading()
        {
            SvgExporter.LevelReady = false;
            SlopeUI.Destroy();
        }
    }

    internal class SlopeUpdater : UIComponent
    {
        public override void Update() { base.Update(); SlopeUI.Tick(); }
    }

    internal class SlopeUI
    {
        // ---- 参数（想改直接改这里）----
        private const int PoolSize = 90;             // 覆盖层标签上限
        private const float MaxDist = 420f;          // 只标注相机附近这个半径内的路段
        private const float SteepPct = 5f;           // “只看陡坡”阈值
        private const float ClickPx = 26f;           // 点选容差（屏幕像素）
        private const float TickInterval = 0.16f;    // 刷新间隔（秒）
        private static readonly Color32 BoxColor = new Color32(0, 0, 0, 170);   // 标签黑底
        private static readonly Color32 BoxWhite = new Color32(255, 255, 255, 255);

        // ---- 运行时状态 ----
        private static UILabel[] _pool;
        private static UILabel _marker;          // 选中标记（独立组件，不占标签池）
        private static UILabel _hint;            // 底部快捷键提示
        private static string _hintCache;
        private static SlopeUpdater _updater;
        private static UIPanel _panel;
        private static UICheckBox _cbRoad, _cbRail, _cbOverlay, _cbSteep, _cbInspect;
        private static UILabel[] _lines;
        private static bool _overlay = true, _steepOnly = true, _inspect = true, _road = true, _rail;
        private static ushort _selected;
        private static float _next;
        private static string _err;
        private static bool _keyWasDown;
        private static string _sBox = "ButtonMenu";

        // ================= 创建 / 销毁 =================

        public static void Create()
        {
            try
            {
                UIView view = UIView.GetAView();
                if (view == null) return;
                if (_panel != null && _updater != null && _pool != null) return;

                // 恢复持久化开关（选项面板/SavedBool 写入）
                _road = Rsv.CbRoad.value;
                _rail = Rsv.CbRail.value;
                _overlay = Rsv.CbOverlay.value;
                _steepOnly = Rsv.CbSteep.value;
                _inspect = Rsv.CbInspect.value;

                if (_updater == null)
                {
                    _updater = view.AddUIComponent(typeof(SlopeUpdater)) as SlopeUpdater;
                    _updater.name = "RSV_Updater";
                    _updater.isInteractive = false;
                    _updater.isVisible = false;
                }

                _sBox = PickSprite("MenuPanel", "GenericPanel", "MenuPanel2", "ButtonMenu");

                if (_pool == null)
                {
                    _pool = new UILabel[PoolSize];
                    for (int i = 0; i < PoolSize; i++)
                    {
                        UILabel l = view.AddUIComponent(typeof(UILabel)) as UILabel;
                        l.name = "RSV_SlopeLabel" + i;
                        l.isInteractive = false;
                        l.isVisible = false;
                        l.size = new Vector2(58f, 20f);                     // 比文字大一圈
                        l.textScale = 0.75f;
                        l.textAlignment = UIHorizontalAlignment.Center;
                        l.verticalAlignment = UIVerticalAlignment.Middle;
                        l.backgroundSprite = _sBox;                          // 半透明黑底
                        l.color = BoxColor;
                        l.padding = new RectOffset(2, 2, 1, 1);
                        l.useOutline = true;
                        l.outlineColor = new Color32(0, 0, 0, 255);
                        l.outlineSize = 1;
                        l.text = "";
                        _pool[i] = l;
                    }
                }

                if (_marker == null)
                {
                    UILabel m = view.AddUIComponent(typeof(UILabel)) as UILabel;
                    m.name = "RSV_Marker";
                    m.isInteractive = false;
                    m.isVisible = false;
                    m.size = new Vector2(76f, 20f);
                    m.textScale = 0.75f;
                    m.textAlignment = UIHorizontalAlignment.Center;
                    m.verticalAlignment = UIVerticalAlignment.Middle;
                    m.backgroundSprite = _sBox;
                    m.color = new Color32(22, 96, 150, 235);              // 蓝底，和黑色坡度标签区分开
                    m.textColor = new Color32(235, 246, 255, 255);
                    m.useOutline = true;
                    m.outlineColor = new Color32(0, 0, 0, 255);
                    m.outlineSize = 1;
                    m.padding = new RectOffset(2, 2, 1, 1);
                    m.text = "";
                    _marker = m;
                }

                if (_panel == null) BuildPanel(view);
                else RefreshHint();
            }
            catch (Exception e) { LogOnce("Create: " + e); }
        }

        public static void Destroy()
        {
            try
            {
                if (_pool != null)
                {
                    for (int i = 0; i < _pool.Length; i++) if (_pool[i] != null) UnityEngine.Object.Destroy(_pool[i].gameObject);
                    _pool = null;
                }
                if (_marker != null) { UnityEngine.Object.Destroy(_marker.gameObject); _marker = null; }
                if (_panel != null) { UnityEngine.Object.Destroy(_panel.gameObject); _panel = null; }
                if (_updater != null) { UnityEngine.Object.Destroy(_updater.gameObject); _updater = null; }
                _cbRoad = _cbRail = _cbOverlay = _cbSteep = _cbInspect = null;
                _lines = null; _hint = null; _hintCache = null; _selected = 0;
            }
            catch (Exception e) { LogOnce("Destroy: " + e); }
        }

        private static void BuildPanel(UIView view)
        {
            _panel = view.AddUIComponent(typeof(UIPanel)) as UIPanel;
            _panel.name = "RSV_Panel";
            _panel.size = new Vector2(272f, 288f);
            _panel.backgroundSprite = _sBox;
            _panel.color = new Color32(26, 30, 38, 238);
            _panel.relativePosition = new Vector3(14f, view.GetScreenResolution().y * 0.5f);

            // 官方拖动方式（wiki UI Framework）：UIDragHandle 挂到面板顶部 28px 区域，
            // 自动限制在屏幕内，拖动时把面板置顶。须显式指定 size（否则它只覆盖默认宽度）。
            UIDragHandle drag = _panel.AddUIComponent(typeof(UIDragHandle)) as UIDragHandle;
            drag.name = "RSV_DragHandle";
            drag.size = new Vector2(272f, 28f);
            drag.relativePosition = new Vector3(0f, 0f, 0f);
            drag.target = _panel;
            drag.constrainToScreen = true;

            UILabel title = _panel.AddUIComponent(typeof(UILabel)) as UILabel;
            title.name = "RSV_Title";
            title.text = "坡度查看器  v0.4";
            title.textScale = 0.85f;
            title.size = new Vector2(244f, 20f);
            title.relativePosition = new Vector3(14f, 6f);
            title.textColor = BoxWhite;
            title.useOutline = true; title.outlineColor = new Color32(0, 0, 0, 200); title.outlineSize = 1;
            title.isInteractive = false;

            // 服务选择
            _cbRoad = MakeCheck(_panel, 30f, "道路", true);
            _cbRail = MakeCheck(_panel, 52f, "轨道交通（火车/地铁/单轨/电车）", false);
            _cbRoad.tooltip = "标注 / 点选 汽车道路（含单轨、电车共线路）";
            _cbRail.tooltip = "标注 / 点选 火车、地铁、单轨、电车轨道";
            _cbRoad.eventCheckChanged += delegate(UIComponent c, bool v) { _road = v; Rsv.CbRoad.value = v; };
            _cbRail.eventCheckChanged += delegate(UIComponent c, bool v) { _rail = v; Rsv.CbRail.value = v; };

            // 显示选项
            _cbOverlay = MakeCheck(_panel, 82f, "坡度覆盖层", true);
            _cbSteep = MakeCheck(_panel, 104f, "只看陡坡 ≥" + FmtPct(SteepPct), true);
            _cbInspect = MakeCheck(_panel, 126f, "点击查看（左键点路面/轨道）", true);
            _cbOverlay.tooltip = "在相机附近的陡坡路段上叠加坡度百分比标签";
            _cbSteep.tooltip = "覆盖层只显示 ≥" + FmtPct(SteepPct) + " 的路段";
            _cbInspect.tooltip = "左键点路面/轨道查看坡度、高差与整条线汇总（右键取消选中）";
            _cbOverlay.eventCheckChanged += delegate(UIComponent c, bool v) { _overlay = v; Rsv.CbOverlay.value = v; };
            _cbSteep.eventCheckChanged += delegate(UIComponent c, bool v) { _steepOnly = v; Rsv.CbSteep.value = v; };
            _cbInspect.eventCheckChanged += delegate(UIComponent c, bool v) { _inspect = v; Rsv.CbInspect.value = v; };

            UILabel sep = _panel.AddUIComponent(typeof(UILabel)) as UILabel;
            sep.name = "RSV_Sep";
            sep.text = "── 选中路段 ──";
            sep.textScale = 0.72f;
            sep.textColor = new Color32(170, 180, 195, 255);
            sep.size = new Vector2(244f, 16f);
            sep.relativePosition = new Vector3(14f, 154f);
            sep.isInteractive = false;

            _lines = new UILabel[5];
            for (int i = 0; i < 5; i++)
            {
                UILabel l = _panel.AddUIComponent(typeof(UILabel)) as UILabel;
                l.name = "RSV_Line" + i;
                l.textScale = 0.72f;
                l.textColor = new Color32(220, 226, 235, 255);
                l.size = new Vector2(248f, 16f);
                l.relativePosition = new Vector3(14f, 174f + i * 18f);
                l.isInteractive = false;
                l.text = i == 0 ? "（左键点击路面/轨道查看）" : "";
                _lines[i] = l;
            }

            _hint = _panel.AddUIComponent(typeof(UILabel)) as UILabel;
            _hint.name = "RSV_Hint";
            _hint.textScale = 0.68f;
            _hint.textColor = new Color32(150, 160, 175, 255);
            _hint.size = new Vector2(248f, 14f);
            _hint.relativePosition = new Vector3(14f, 258f);
            _hint.isInteractive = false;

            RefreshHint();
            ApplyCheckStates();
        }

        private static UICheckBox MakeCheck(UIComponent parent, float y, string text, bool initial)
        {
            UICheckBox cb = parent.AddUIComponent(typeof(UICheckBox)) as UICheckBox;
            cb.name = "RSV_Cb" + text;
            cb.size = new Vector2(244f, 20f);
            cb.relativePosition = new Vector3(14f, y);

            UISprite box = cb.AddUIComponent(typeof(UISprite)) as UISprite;
            box.name = "RSV_CbBox";
            box.size = new Vector2(16f, 16f);
            box.relativePosition = new Vector3(0f, 2f);
            box.spriteName = PickSprite("ToggleBase", "ButtonMenu", _sBox, _sBox);

            UISprite mark = box.AddUIComponent(typeof(UISprite)) as UISprite;
            mark.name = "RSV_CbMark";
            mark.size = new Vector2(16f, 16f);
            mark.relativePosition = new Vector3(0f, 0f);
            mark.spriteName = PickSprite("ToggleBaseFocused", "ButtonMenuFocused", _sBox, _sBox);

            cb.checkedBoxObject = mark;

            UILabel lab = cb.AddUIComponent(typeof(UILabel)) as UILabel;
            lab.name = "RSV_CbLabel";
            lab.text = text;
            lab.textScale = 0.76f;
            lab.textColor = new Color32(225, 232, 245, 255);
            lab.relativePosition = new Vector3(22f, 1f);
            lab.size = new Vector2(220f, 18f);
            lab.isInteractive = false;

            cb.isChecked = initial;
            mark.isVisible = initial;
            return cb;
        }

        private static void ApplyCheckStates()
        {
            SetCheck(_cbRoad, _road); SetCheck(_cbRail, _rail);
            SetCheck(_cbOverlay, _overlay); SetCheck(_cbSteep, _steepOnly); SetCheck(_cbInspect, _inspect);
        }

        private static void SetCheck(UICheckBox cb, bool v)
        {
            if (cb == null) return;
            cb.isChecked = v;
            if (cb.checkedBoxObject != null) cb.checkedBoxObject.isVisible = v;
        }

        /// <summary>底部提示行（快捷键变化时更新，防空转）。</summary>
        private static void RefreshHint()
        {
            if (_hint == null) return;
            string k;
            try { k = Rsv.KeyLabel(); } catch { k = "?"; }
            if (_hintCache == k) return;
            _hintCache = k;
            _hint.text = k + " 显示/隐藏 · 选项→模组设置 可改";
        }

        private static string PickSprite(string a, string b, string c, string d)
        {
            try
            {
                UIView v = UIView.GetAView();
                if (v != null && v.defaultAtlas != null)
                {
                    string[] names = new string[] { a, b, c, d };
                    for (int i = 0; i < names.Length; i++)
                        if (!string.IsNullOrEmpty(names[i]) && v.defaultAtlas[names[i]] != null) return names[i];
                }
            }
            catch { }
            return a;
        }

        // ================= 每帧 =================

        public static void Tick()
        {
            try
            {
                if (_panel == null) { Create(); if (_panel == null) return; }

                RefreshHint();

                // 快捷键：仅响应按下瞬间（IsPressed 是“按住”状态，若每帧翻转会造成连续切换）
                bool keyDown = Rsv.ToggleKey.IsPressed();
                if (keyDown && !_keyWasDown && !UIView.HasModalInput())
                    _panel.isVisible = !_panel.isVisible;
                _keyWasDown = keyDown;

                if (_inspect && !UIView.IsInsideUI() && !UIView.HasModalInput())
                {
                    if (Input.GetMouseButtonDown(0)) HandleClick();
                    else if (Input.GetMouseButtonDown(1) && _selected != 0) { _selected = 0; ShowDetail(); }
                }

                float now = Time.realtimeSinceStartup;
                if (now < _next) return;
                _next = now + TickInterval;

                if (_overlay) RefreshOverlay(); else HideAll();
                UpdateMarker();
            }
            catch (Exception e) { LogOnce("Tick: " + e); }
        }

        private static void HideAll()
        {
            if (_pool == null) return;
            for (int i = 0; i < _pool.Length; i++) if (_pool[i] != null) _pool[i].isVisible = false;
        }

        private struct Cand { public ushort id; public float sqrDist; public Vector3 sp; public float pct; }

        private static void RefreshOverlay()
        {
            UIView view = UIView.GetAView();
            Camera cam = Camera.main;
            NetManager nm = NetManager.instance;
            if (view == null || cam == null || nm == null || _pool == null) { HideAll(); return; }

            Vector3 camPos = cam.transform.position;
            float maxSqr = MaxDist * MaxDist;
            NetSegment[] segBuf = nm.m_segments.m_buffer;
            NetNode[] nodeBuf = nm.m_nodes.m_buffer;
            List<Cand> list = new List<Cand>(512);

            int size = (int)nm.m_segments.m_size;
            for (int i = 1; i < size; i++)
            {
                if ((segBuf[i].m_flags & NetSegment.Flags.Created) == 0) continue;
                NetInfo info = PrefabCollection<NetInfo>.GetPrefab(segBuf[i].m_infoIndex);
                if (!Wanted(info)) continue;

                Vector3 a = nodeBuf[segBuf[i].m_startNode].m_position;
                Vector3 b = nodeBuf[segBuf[i].m_endNode].m_position;
                Vector3 mid = (a + b) * 0.5f;
                float sq = (mid - camPos).sqrMagnitude;
                if (sq > maxSqr) continue;

                float pct = SlopePct(a, b);
                if (_steepOnly && pct < SteepPct) continue;

                Vector3 sp = cam.WorldToScreenPoint(mid);
                if (sp.z <= 0f) continue;
                if (sp.x < -40f || sp.x > Screen.width + 40f || sp.y < -40f || sp.y > Screen.height + 40f) continue;

                if (i == _selected) continue;                        // 选中段只显示蓝色标记
                Cand cd = new Cand();
                cd.id = (ushort)i; cd.sqrDist = sq; cd.sp = sp; cd.pct = pct;
                list.Add(cd);
            }

            list.Sort(delegate(Cand x, Cand y) { return x.sqrDist.CompareTo(y.sqrDist); });

            int n = Mathf.Min(list.Count, _pool.Length);
            for (int k = 0; k < n; k++)
            {
                Cand cd = list[k];
                UILabel l = _pool[k];
                string txt = FmtPct(cd.pct);
                if (l.text != txt) l.text = txt;
                l.textColor = ColorFor(cd.pct);
                Vector2 gui = view.ScreenPointToGUI(cd.sp / view.inputScale);
                l.relativePosition = new Vector3(gui.x - l.width * 0.5f, gui.y - l.height * 0.5f, 0f);
                if (!l.isVisible) l.isVisible = true;
            }
            for (int k = n; k < _pool.Length; k++) if (_pool[k] != null) _pool[k].isVisible = false;
        }

        private static void UpdateMarker()
        {
            NetManager nm = NetManager.instance;
            Camera cam = Camera.main;
            UIView view = UIView.GetAView();
            if (nm == null || cam == null || view == null || _marker == null) return;
            UILabel mk = _marker;
            if (_selected == 0) { mk.isVisible = false; return; }
            NetSegment seg = nm.m_segments.m_buffer[_selected];
            if ((seg.m_flags & NetSegment.Flags.Created) == 0) { _selected = 0; mk.isVisible = false; return; }
            Vector3 a = nm.m_nodes.m_buffer[seg.m_startNode].m_position;
            Vector3 b = nm.m_nodes.m_buffer[seg.m_endNode].m_position;
            Vector3 sp = cam.WorldToScreenPoint((a + b) * 0.5f);
            if (sp.z <= 0f) { mk.isVisible = false; return; }
            string txt = "选中 " + FmtPct(SlopePct(a, b));
            if (mk.text != txt) mk.text = txt;
            Vector2 gui = view.ScreenPointToGUI(sp / view.inputScale);
            mk.relativePosition = new Vector3(gui.x - mk.width * 0.5f, gui.y - mk.height * 0.5f - 17f, 0f);
            mk.isVisible = true;
        }

        // ================= 点选 =================

        private static void HandleClick()
        {
            NetManager nm = NetManager.instance;
            Camera cam = Camera.main;
            if (nm == null || cam == null) return;

            Vector3 mouse = Input.mousePosition;
            NetSegment[] segBuf = nm.m_segments.m_buffer;
            NetNode[] nodeBuf = nm.m_nodes.m_buffer;
            float best = ClickPx * ClickPx;
            ushort bestSeg = 0;
            int size = (int)nm.m_segments.m_size;
            for (int i = 1; i < size; i++)
            {
                if ((segBuf[i].m_flags & NetSegment.Flags.Created) == 0) continue;
                NetInfo info = PrefabCollection<NetInfo>.GetPrefab(segBuf[i].m_infoIndex);
                if (!Wanted(info)) continue;

                Vector3 a = nodeBuf[segBuf[i].m_startNode].m_position;
                Vector3 b = nodeBuf[segBuf[i].m_endNode].m_position;
                Vector3 mid = (a + b) * 0.5f;
                float d2 = ScreenDist2(cam, a, mid, b, mouse);
                if (d2 < best) { best = d2; bestSeg = (ushort)i; }
            }
            if (bestSeg != 0) { _selected = bestSeg; ShowDetail(); }
        }

        private static float ScreenDist2(Camera cam, Vector3 a, Vector3 mid, Vector3 b, Vector3 mouse)
        {
            float best = Mathf.Min(Dist2(cam, a, mouse), Dist2(cam, mid, mouse));
            return Mathf.Min(best, Dist2(cam, b, mouse));
        }

        private static float Dist2(Camera cam, Vector3 world, Vector3 mouse)
        {
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0f) return float.MaxValue;
            float dx = sp.x - mouse.x, dy = sp.y - mouse.y;
            return dx * dx + dy * dy;
        }

        // ================= 明细 =================

        private static void ShowDetail()
        {
            if (_lines == null) return;
            if (_selected == 0)
            {
                _lines[0].text = "（左键点击路面/轨道查看）";
                for (int i = 1; i < _lines.Length; i++) _lines[i].text = "";
                return;
            }
            NetManager nm = NetManager.instance;
            NetSegment seg = nm.m_segments.m_buffer[_selected];
            Vector3 a = nm.m_nodes.m_buffer[seg.m_startNode].m_position;
            Vector3 b = nm.m_nodes.m_buffer[seg.m_endNode].m_position;
            float dy = b.y - a.y;
            float dxz = Horiz(a, b);
            float pct = SlopePct(a, b);
            NetInfo info = PrefabCollection<NetInfo>.GetPrefab(seg.m_infoIndex);
            string roadName = info != null ? info.name : "?";

            int cs; float totH, totDy, maxPct;
            WalkChain(_selected, out cs, out totH, out totDy, out maxPct);

            _lines[0].text = "坡度 " + FmtPct(pct) + "   高差 " + dy.ToString("0.0") + " m";
            _lines[1].text = "水平 " + dxz.ToString("0.0") + " m   路长 " + seg.m_averageLength.ToString("0.0") + " m";
            _lines[2].text = "资产 " + Trunc(roadName, 24);
            _lines[3].text = "海拔 " + a.y.ToString("0.0") + " → " + b.y.ToString("0.0");
            _lines[4].text = "整条线 " + cs + "段 " + totH.ToString("0") + "m 均" + FmtPct(totH > 0.5f ? Mathf.Abs(totDy) / totH * 100f : 0f)
                             + " 峰" + FmtPct(maxPct);
            _lines[4].textColor = ColorFor(maxPct);
        }

        private static void WalkChain(ushort start, out int count, out float totalH, out float totalDy, out float maxPct)
        {
            NetManager nm = NetManager.instance;
            count = 0; totalH = 0f; totalDy = 0f; maxPct = 0f;
            if (nm == null) return;
            NetSegment[] segBuf = nm.m_segments.m_buffer;
            NetNode[] nodeBuf = nm.m_nodes.m_buffer;
            NetInfo startInfo = PrefabCollection<NetInfo>.GetPrefab(segBuf[start].m_infoIndex);
            string infoName = startInfo != null ? startInfo.name : null;
            bool startIsRoad = startInfo != null && startInfo.m_class.m_service == ItemClass.Service.Road;

            HashSet<ushort> seen = new HashSet<ushort>();
            Queue<ushort> q = new Queue<ushort>();
            seen.Add(start); q.Enqueue(start);
            int guard = 0;
            while (q.Count > 0 && guard++ < 3000)
            {
                ushort sid = q.Dequeue();
                Vector3 a = nodeBuf[segBuf[sid].m_startNode].m_position;
                Vector3 b = nodeBuf[segBuf[sid].m_endNode].m_position;
                count++;
                totalH += Horiz(a, b);
                totalDy += b.y - a.y;
                float p = SlopePct(a, b);
                if (p > maxPct) maxPct = p;

                for (int e = 0; e < 2; e++)
                {
                    ushort nid = e == 0 ? segBuf[sid].m_startNode : segBuf[sid].m_endNode;
                    NetNode node = nodeBuf[nid];
                    int cnt = 0; ushort other = 0;
                    for (int k = 0; k < 8; k++)
                    {
                        ushort s2 = SegAt(node, k);
                        if (s2 == 0) continue;
                        cnt++;
                        if (s2 != sid) other = s2;
                    }
                    if (cnt != 2 || other == 0 || seen.Contains(other)) continue;
                    if ((segBuf[other].m_flags & NetSegment.Flags.Created) == 0) continue;
                    NetInfo oi = PrefabCollection<NetInfo>.GetPrefab(segBuf[other].m_infoIndex);
                    if (oi == null) continue;
                    if (startIsRoad != (oi.m_class.m_service == ItemClass.Service.Road)) continue;
                    if (infoName != null && oi.name != infoName) continue;
                    seen.Add(other); q.Enqueue(other);
                }
            }
        }

        private static ushort SegAt(NetNode n, int k)
        {
            switch (k)
            {
                case 0: return n.m_segment0;
                case 1: return n.m_segment1;
                case 2: return n.m_segment2;
                case 3: return n.m_segment3;
                case 4: return n.m_segment4;
                case 5: return n.m_segment5;
                case 6: return n.m_segment6;
                default: return n.m_segment7;
            }
        }

        // ================= 工具 =================

        /// <summary>当前开关下这条资产要不要标注/可选中。</summary>
        private static bool Wanted(NetInfo info)
        {
            if (info == null) return false;
            if (!_road && !_rail) return false;
            ItemClass.Service svc = info.m_class.m_service;
            if (svc == ItemClass.Service.Road) return _road;
            if (svc == ItemClass.Service.PublicTransport)
            {
                if (!_rail) return false;
                ItemClass.SubService sub = info.m_class.m_subService;
                return sub == ItemClass.SubService.PublicTransportTrain
                    || sub == ItemClass.SubService.PublicTransportMetro
                    || sub == ItemClass.SubService.PublicTransportMonorail
                    || sub == ItemClass.SubService.PublicTransportTram;
            }
            return false;
        }

        private static float Horiz(Vector3 a, Vector3 b)
        {
            float dx = b.x - a.x, dz = b.z - a.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static float SlopePct(Vector3 a, Vector3 b)
        {
            float h = Horiz(a, b);
            if (h < 0.05f) return 0f;
            return Mathf.Abs(b.y - a.y) / h * 100f;
        }

        private static string FmtPct(float p)
        {
            return p < 10f ? p.ToString("0.0") + "%" : p.ToString("0") + "%";
        }

        private static string Trunc(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "?";
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        private static Color32 ColorFor(float pct)
        {
            if (pct < 3f) return new Color32(120, 235, 130, 255);
            if (pct < 8f) return new Color32(250, 235, 120, 255);
            if (pct < 12f) return new Color32(255, 170, 70, 255);
            if (pct < 18f) return new Color32(255, 110, 70, 255);
            return new Color32(255, 70, 70, 255);
        }

        private static void LogOnce(string msg)
        {
            if (_err == msg) return;
            _err = msg;
            Debug.LogWarning("[RoadSlopeViewer] " + msg);
        }
    }
}
