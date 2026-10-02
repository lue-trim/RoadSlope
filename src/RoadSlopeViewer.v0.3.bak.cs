// RoadSlopeViewer v0.2 — 城市天际线 CS1：在游戏内查看道路 / 轨道交通坡度
//
// v0.2 变更（依实测反馈）：
//   1) 修 bug：上下拖动方向反了 —— UIMouseEventParameter.position 是"屏幕像素(y 向上)"，
//      而 relativePosition 是"UI 坐标(y 向下)"，必须经 view.ScreenPointToGUI(pos / view.inputScale) 转换。
//   2) 三个开关改为真正的复选框（UICheckBox + ToggleBase/ToggleBaseFocused，贴图名取自游戏政策面板）。
//   3) 坡度标签加半透明黑底框（UILabel.backgroundSprite + label.color；RenderBackground 用的正是 color）。
//   4) 新增快捷键：默认 Alt+R 显示/隐藏面板，SavedInputKey 持久化；面板内可点击改键。
//   5) 新增「轨道交通」开关（火车/地铁/单轨/电车），与「道路」可分别开关。
//
// API 均已对本机 Managed 程序集反编译核对：
//   UIView.GetAView/ScreenPointToGUI/inputScale/defaultAtlas/IsInsideUI/HasModalInput
//   UILabel.RenderBackground → atlas[backgroundSprite] + color；textColor 管文字
//   UICheckBox.isChecked/checkedBoxObject/eventCheckChanged(UIComponent, bool)
//   SavedInputKey(name,fileName,key,ctrl,shift,alt,autoUpdate) / IsPressed() / Encode() / value
//   NetSegment.m_flags/m_startNode/m_endNode/m_infoIndex/m_averageLength
//   NetNode.m_position/m_segment0..7；ItemClass.Service / SubService.PublicTransport*
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
        public string Description { get { return "查看道路/轨道交通坡度：覆盖层标注 + 左键点选查看坡度、高差与整条路汇总。默认 Alt+R 显示/隐藏面板。"; } }
    }

    public class SlopeLoader : ILoadingExtension
    {
        public void OnCreated(ILoading loading) { }
        public void OnReleased() { }
        public void OnLevelLoaded(LoadMode mode) { SlopeUI.Create(); }
        public void OnLevelUnloading() { SlopeUI.Destroy(); }
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
        private static SlopeUpdater _updater;
        private static UIPanel _panel;
        private static UICheckBox _cbRoad, _cbRail, _cbOverlay, _cbSteep, _cbInspect;
        private static UIButton _btnKey;
        private static UILabel[] _lines;
        private static bool _overlay = true, _steepOnly = true, _inspect = true, _road = true, _rail;
        private static ushort _selected;
        private static float _next;
        private static string _err;
        private static bool _awaitingKey;
        private static string _sBox = "ButtonMenu";

        // 快捷键（持久化到 SavedInputKey；默认 Alt+R）
        private static SavedInputKey _toggleKey =
            new SavedInputKey("rsv_toggleKey", "RoadSlopeViewer", KeyCode.R, false, false, true, true);

        // ================= 创建 / 销毁 =================

        public static void Create()
        {
            try
            {
                UIView view = UIView.GetAView();
                if (view == null) return;
                if (_panel != null && _updater != null && _pool != null) return;

                if (_updater == null)
                {
                    _updater = view.AddUIComponent(typeof(SlopeUpdater)) as SlopeUpdater;
                    _updater.isInteractive = false;
                    _updater.isVisible = false;
                }

                _sBox = PickSprite("GenericPanel", "MenuPanel2", "ButtonMenu", "InfoDisplay");

                if (_pool == null)
                {
                    _pool = new UILabel[PoolSize];
                    for (int i = 0; i < PoolSize; i++)
                    {
                        UILabel l = view.AddUIComponent(typeof(UILabel)) as UILabel;
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
                _btnKey = null; _lines = null; _selected = 0;
                _overlay = true; _steepOnly = true; _inspect = true; _road = true; _rail = false;
            }
            catch (Exception e) { LogOnce("Destroy: " + e); }
        }

        private static void BuildPanel(UIView view)
        {
            _panel = view.AddUIComponent(typeof(UIPanel)) as UIPanel;
            _panel.size = new Vector2(272f, 306f);
            _panel.backgroundSprite = _sBox;
            _panel.color = new Color32(26, 30, 38, 238);
            _panel.relativePosition = new Vector3(14f, view.GetScreenResolution().y * 0.5f);

            UILabel title = _panel.AddUIComponent(typeof(UILabel)) as UILabel;
            title.text = "坡度查看器  v0.2";
            title.textScale = 0.85f;
            title.size = new Vector2(244f, 20f);
            title.relativePosition = new Vector3(14f, 6f);
            title.textColor = BoxWhite;
            title.useOutline = true; title.outlineColor = new Color32(0, 0, 0, 200); title.outlineSize = 1;
            title.isInteractive = false;

            // 服务选择
            _cbRoad = MakeCheck(_panel, 30f, "道路", true);
            _cbRail = MakeCheck(_panel, 52f, "轨道交通（火车/地铁/单轨/电车）", false);
            _cbRoad.eventCheckChanged += delegate(UIComponent c, bool v) { _road = v; };
            _cbRail.eventCheckChanged += delegate(UIComponent c, bool v) { _rail = v; };

            // 显示选项
            _cbOverlay = MakeCheck(_panel, 82f, "坡度覆盖层", true);
            _cbSteep = MakeCheck(_panel, 104f, "只看陡坡 ≥" + FmtPct(SteepPct), true);
            _cbInspect = MakeCheck(_panel, 126f, "点击查看（左键点路面/轨道）", true);
            _cbOverlay.eventCheckChanged += delegate(UIComponent c, bool v) { _overlay = v; };
            _cbSteep.eventCheckChanged += delegate(UIComponent c, bool v) { _steepOnly = v; };
            _cbInspect.eventCheckChanged += delegate(UIComponent c, bool v) { _inspect = v; };

            // 快捷键
            _btnKey = _panel.AddUIComponent(typeof(UIButton)) as UIButton;
            _btnKey.size = new Vector2(244f, 24f);
            _btnKey.relativePosition = new Vector3(14f, 152f);
            _btnKey.normalBgSprite = PickSprite("ButtonMenu", "ButtonMenuFocused", _sBox, _sBox);
            _btnKey.textScale = 0.76f;
            _btnKey.textColor = new Color32(225, 232, 245, 255);
            _btnKey.eventClick += delegate(UIComponent c, UIMouseEventParameter p) { _awaitingKey = true; RefreshKeyButton(); };

            UILabel sep = _panel.AddUIComponent(typeof(UILabel)) as UILabel;
            sep.text = "── 选中路段 ──";
            sep.textScale = 0.72f;
            sep.textColor = new Color32(170, 180, 195, 255);
            sep.size = new Vector2(244f, 16f);
            sep.relativePosition = new Vector3(14f, 182f);
            sep.isInteractive = false;

            _lines = new UILabel[5];
            for (int i = 0; i < 5; i++)
            {
                UILabel l = _panel.AddUIComponent(typeof(UILabel)) as UILabel;
                l.textScale = 0.72f;
                l.textColor = new Color32(220, 226, 235, 255);
                l.size = new Vector2(248f, 16f);
                l.relativePosition = new Vector3(14f, 202f + i * 18f);
                l.isInteractive = false;
                l.text = i == 0 ? "（左键点击路面/轨道查看）" : "";
                _lines[i] = l;
            }

            // 面板拖动（修：经 UI 坐标转换，x/y 方向才对）
            bool dragging = false; Vector2 grab = Vector2.zero;
            _panel.eventMouseDown += delegate(UIComponent c, UIMouseEventParameter p)
            {
                if (p.buttons != UIMouseButton.Left) return;
                dragging = true;
                Vector2 g = ToGui(p.position);
                grab = g - (Vector2)_panel.relativePosition;
            };
            _panel.eventMouseMove += delegate(UIComponent c, UIMouseEventParameter p)
            {
                if (!dragging) return;
                Vector2 g = ToGui(p.position);
                _panel.relativePosition = new Vector3(g.x - grab.x, g.y - grab.y, 0f);
            };
            _panel.eventMouseUp += delegate(UIComponent c, UIMouseEventParameter p) { dragging = false; };

            RefreshKeyButton();
            ApplyCheckStates();
        }

        /// <summary>屏幕像素(y 向上) → UI 坐标(y 向下)，与标签投影同一套换算。</summary>
        private static Vector2 ToGui(Vector3 screenPos)
        {
            UIView view = UIView.GetAView();
            if (view == null) return new Vector2(screenPos.x, screenPos.y);
            return view.ScreenPointToGUI(screenPos / view.inputScale);
        }

        private static UICheckBox MakeCheck(UIComponent parent, float y, string text, bool initial)
        {
            UICheckBox cb = parent.AddUIComponent(typeof(UICheckBox)) as UICheckBox;
            cb.size = new Vector2(244f, 20f);
            cb.relativePosition = new Vector3(14f, y);

            UISprite box = cb.AddUIComponent(typeof(UISprite)) as UISprite;
            box.size = new Vector2(16f, 16f);
            box.relativePosition = new Vector3(0f, 2f);
            box.spriteName = PickSprite("ToggleBase", "ButtonMenu", _sBox, _sBox);

            UISprite mark = box.AddUIComponent(typeof(UISprite)) as UISprite;
            mark.size = new Vector2(16f, 16f);
            mark.relativePosition = new Vector3(0f, 0f);
            mark.spriteName = PickSprite("ToggleBaseFocused", "ButtonMenuFocused", _sBox, _sBox);

            cb.checkedBoxObject = mark;

            UILabel lab = cb.AddUIComponent(typeof(UILabel)) as UILabel;
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

        private static void RefreshKeyButton()
        {
            if (_btnKey == null) return;
            if (_awaitingKey) { _btnKey.text = "请按下新快捷键…（Esc 取消）"; return; }
            string k = "?";
            try { k = _toggleKey.ToString(); } catch { }
            _btnKey.text = "显示/隐藏面板快捷键: " + k + "  （点击改键）";
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

                if (_awaitingKey) { CaptureKey(); return; }                 // 改键模式：先接管按键

                if (_toggleKey.IsPressed()) _panel.isVisible = !_panel.isVisible;

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

        private static void CaptureKey()
        {
            if (Input.GetKeyDown(KeyCode.Escape)) { _awaitingKey = false; RefreshKeyButton(); return; }
            Array keys = Enum.GetValues(typeof(KeyCode));
            for (int i = 0; i < keys.Length; i++)
            {
                KeyCode kc = (KeyCode)keys.GetValue(i);
                if (kc == KeyCode.None) continue;
                if (kc == KeyCode.LeftControl || kc == KeyCode.RightControl ||
                    kc == KeyCode.LeftShift || kc == KeyCode.RightShift ||
                    kc == KeyCode.LeftAlt || kc == KeyCode.RightAlt ||
                    kc == KeyCode.LeftCommand || kc == KeyCode.RightCommand) continue;
                if (!Input.GetKeyDown(kc)) continue;

                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
                try { _toggleKey.value = SavedInputKey.Encode(kc, ctrl, shift, alt); } catch (Exception e) { LogOnce("改键: " + e); }
                _awaitingKey = false;
                RefreshKeyButton();
                return;
            }
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
