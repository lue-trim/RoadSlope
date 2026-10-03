// RoadSlopeViewer — 游戏内 SVG 导出器（v0.4 新增）
//
// 直接读取运行中的 NetManager（无需解码存档），复刻离线工具
// cities_map/map_export.py --slope / --rail 的渲染规则：
//   - 道路坡度色：<8% 绿 / 8-12% 黄 / 12-20% 橙 / >20% 红
//   - 坡度 = |Δy| ÷ 实际道路长度（NetSegment.m_averageLength，各车道曲线弧长均值；
//     与游戏内面板同一算法，曲线段不会因两点直线距离而高估坡度）
//   - 轨道坡度色：<3% / 3-6% / 6-10% / >10%
//   - 标注：道路 >20% 且长 >60m；轨道 >10% 且长 >80m（均排除 >60% 垂直过渡段）
//   - 分类描边（RBR/RBH 自制红 / 原版灰 / 其他 mod 蓝）与图层序（管→栅→人→航→轨→路）
//   - 贝塞尔曲线用游戏自身的 NetSegment.CalculateMiddlePoints（与游戏渲染一致）
//
// 线程模型：Collect 在主线程完成（访问 Unity 数据），字符串生成 + 写盘在后台线程，
// 完成后通过 volatile 标志交由 RsvPump（ThreadingExtension）转回主线程更新 UI。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using ColossalFramework;
using ColossalFramework.Plugins;
using UnityEngine;

namespace RoadSlopeViewer
{
    internal static class SvgExporter
    {
        public static bool LevelReady;          // 由 SlopeLoader 维护
        public static volatile bool Busy;       // 导出进行中

        private static readonly object _lock = new object();
        private static volatile bool _notify;
        private static string _notifyMsg = "";
        private static string _notifyPath = "";
        private static bool _notifyOk;

        // 供选项面板重建时读取上次结果
        public static volatile string LastResultFile = "";
        public static volatile string LastResultPath = "";

        // ================= 数据 =================

        private struct Seg
        {
            public float x1, z1, cx1, cz1, cx2, cz2, x2, z2;
            public float pct, dist, yavg, w;
            public int cat;
            public bool car;
            public bool railKeep;
        }

        private class Item
        {
            public int lay;
            public float y;
            public int sub;
            public string s;
        }

        private struct Map2Svg
        {
            public float minx, minz, spanx, spanz, w, h;
            public void P(float x, float z, out float px, out float py)
            {
                px = (x - minx) / spanx * w;
                py = h - (z - minz) / spanz * h;
            }
        }

        // 分类 CAT_EDGE 索引：0=vanilla 1=rbr 2=mod 3=track 4=path 5=pipe 6=ped 7=fence 8=other
        private const int CatVanilla = 0, CatRbr = 1, CatMod = 2, CatTrack = 3, CatPath = 4,
                          CatPipe = 5, CatPed = 6, CatFence = 7, CatOther = 8;
        private static readonly string[] CAT_EDGE = {
            "#8a93a0", "#c43d3d", "#3f6fbf", "#b06ad4", "#4fc3b7",
            "#3f4f6e", "#7bc47f", "#8a7157", "#e8a13a" };
        // 图层序（与 map_export.py LAYER_ORDER 相同：pipe,fence,ped,path,track,other,vanilla,mod,rbr）
        private static readonly int[] LAYER = { 6, 8, 7, 4, 3, 0, 2, 1, 5 };

        private static readonly string[] TRACK_KW = { "Train", "Metro", "Monorail", "Tram Track", "CableCar", "Bus Line", "Ferry Line", "Blimp Line" };
        private static readonly string[] PATH_KW = { "Ship Path", "Ferry Path", "Airplane", "Blimp", "Connection Path", "Helicopter", "Line", "Cargo Connection", "Ship Dock", "Ferry Dock", "Ship Line" };
        private static readonly string[] PIPE_KW = { "Water Pipe", "Heating Pipe" };
        private static readonly string[] PED_KW = { "Pedestrian", "Path", "Walkway", "Nature Reserve" };
        private static readonly string[] FENCE_KW = { "Fence", "Wall", "Quay", "Canal", "Dam", "Ruins", "Castle" };
        private static readonly string[] VANILLA_KW = {
            "Basic", "Large", "Highway", "Gravel", "Medium", "Oneway", "Avenue",
            "Four Lane", "Six Lane", "Eight Lane", "Two Lane", "Three Lane", "Five Lane",
            "Small 4 Lane", "Small 3 Lane", "Small 2 Lane", "Asymmetr", "Industry Road",
            "Tram Depot", "Bus Station", "Harbor", "HighwayRamp", "Pedestrian Gravel",
            "Pedestrian Pavement", "Custom 1W", "City Quay" };

        // ================= 入口（主线程） =================

        public static bool Start(out string err)
        {
            err = null;
            if (Busy) { err = "上一次导出仍在进行"; return false; }
            if (!LevelReady) { err = "请先进入城市地图再导出"; return false; }

            NetManager nm = NetManager.instance;
            SimulationManager sm = SimulationManager.instance;
            if (nm == null) { err = "游戏尚未就绪"; return false; }

            bool railMode = Rsv.ExportMode.value == 1;
            bool labels = Rsv.ExportLabels.value;
            string city = "";
            try
            {
                if (sm != null && sm.m_metaData != null && sm.m_metaData.m_CityName != null)
                    city = sm.m_metaData.m_CityName;
            }
            catch { }

            int skipped;
            List<Seg> segs = Collect(nm, out skipped);
            if (segs.Count == 0) { err = "没有可导出的路段"; return false; }

            string outDir = ExportDirPath();
            Busy = true;
            Thread th = new Thread(delegate() { RenderThread(segs, railMode, labels, city, outDir); });
            th.IsBackground = true;
            th.Priority = System.Threading.ThreadPriority.BelowNormal;
            th.Start();
            Debug.Log("[RoadSlopeViewer] SVG 导出开始：" + segs.Count + " 段（跳过 " + skipped + "），模式=" + (railMode ? "rail" : "road"));
            return true;
        }

        public static string ExportDirPath()
        {
            try
            {
                string dll = Assembly.GetExecutingAssembly().Location;
                string dir = Path.GetDirectoryName(dll);
                if (!string.IsNullOrEmpty(dir)) return Path.Combine(dir, "Exports");
            }
            catch { }
            return "Exports";
        }

        // ================= 收集（主线程访问 NetManager） =================

        private static List<Seg> Collect(NetManager nm, out int skipped)
        {
            skipped = 0;
            List<Seg> list = new List<Seg>(40000);
            NetSegment[] segBuf = nm.m_segments.m_buffer;
            NetNode[] nodeBuf = nm.m_nodes.m_buffer;
            int size = (int)nm.m_segments.m_size;
            for (int i = 1; i < size; i++)
            {
                if ((segBuf[i].m_flags & NetSegment.Flags.Created) == 0) continue;
                NetInfo info = PrefabCollection<NetInfo>.GetPrefab(segBuf[i].m_infoIndex);
                if (info == null) { skipped++; continue; }
                string infoName = info.name;

                int cat = Classify(infoName);
                bool car = cat <= CatMod;
                bool railKeep = cat == CatTrack || (car && (Contains(infoName, "Tram") || Contains(infoName, "Monorail")));

                Vector3 a = nodeBuf[segBuf[i].m_startNode].m_position;
                Vector3 b = nodeBuf[segBuf[i].m_endNode].m_position;
                float straight = Horiz(a, b);
                float len = segBuf[i].m_averageLength;      // 实际道路长度（各车道曲线弧长均值）
                if (len < 0.1f) len = straight;             // 0 值兜底，同游戏做法
                if (len < 0.01f) { skipped++; continue; }

                Vector3 c1, c2;
                NetSegment.CalculateMiddlePoints(a, segBuf[i].m_startDirection, b, segBuf[i].m_endDirection, true, true, out c1, out c2);

                float pct = Mathf.Abs(b.y - a.y) / len * 100f;
                float w = info.m_halfWidth * 2f;
                if (w < 1f) w = 6f;

                Seg s = new Seg();
                s.x1 = a.x; s.z1 = a.z; s.x2 = b.x; s.z2 = b.z;
                s.cx1 = c1.x; s.cz1 = c1.z; s.cx2 = c2.x; s.cz2 = c2.z;
                s.pct = pct; s.dist = len; s.yavg = (a.y + b.y) * 0.5f; s.w = w;
                s.cat = cat; s.car = car; s.railKeep = railKeep;
                list.Add(s);
            }
            return list;
        }

        // ================= 渲染（后台线程；只碰纯数据） =================

        private static void RenderThread(List<Seg> segs, bool railMode, bool labelsOn, string city, string outDir)
        {
            string fileName = null, fullPath = null, failMsg = null;
            bool ok = false;
            try
            {
                // ---- 包围盒（含控制点，防曲线溢出）----
                float minx = float.MaxValue, maxx = float.MinValue, minz = float.MaxValue, maxz = float.MinValue;
                for (int i = 0; i < segs.Count; i++)
                {
                    Seg s = segs[i];
                    if (s.x1 < minx) minx = s.x1; if (s.x1 > maxx) maxx = s.x1;
                    if (s.z1 < minz) minz = s.z1; if (s.z1 > maxz) maxz = s.z1;
                    if (s.cx1 < minx) minx = s.cx1; if (s.cx1 > maxx) maxx = s.cx1;
                    if (s.cz1 < minz) minz = s.cz1; if (s.cz1 > maxz) maxz = s.cz1;
                    if (s.cx2 < minx) minx = s.cx2; if (s.cx2 > maxx) maxx = s.cx2;
                    if (s.cz2 < minz) minz = s.cz2; if (s.cz2 > maxz) maxz = s.cz2;
                    if (s.x2 < minx) minx = s.x2; if (s.x2 > maxx) maxx = s.x2;
                    if (s.z2 < minz) minz = s.z2; if (s.z2 > maxz) maxz = s.z2;
                }
                // 边缘留白 20m：保证路肩缘线（半路宽法向外扩）不越出画布
                minx -= 20f; maxx += 20f; minz -= 20f; maxz += 20f;

                float spanx = maxx - minx; if (spanx < 1f) spanx = 1f;
                float spanz = maxz - minz; if (spanz < 1f) spanz = 1f;
                float W = 6000f;
                float H = W * spanz / spanx;
                if (H > 9000f) { float sc = 9000f / H; H = 9000f; W = W * sc; }
                // 与离线工具一致：尺寸先取整，坐标计算用整型尺寸（否则坐标可能微超 viewBox 半像素）
                W = (int)W; H = (int)H;

                Map2Svg map = new Map2Svg();
                map.minx = minx; map.minz = minz; map.spanx = spanx; map.spanz = spanz;
                map.w = W; map.h = H;

                // ---- 元素 ----
                List<Item> items = new List<Item>(segs.Count * 2);
                List<string> labels = new List<string>();
                float[] px = new float[11];
                float[] pz = new float[11];

                float labelMin = railMode ? 10f : 20f;
                float labelMinLen = railMode ? 80f : 60f;

                for (int i = 0; i < segs.Count; i++)
                {
                    Seg s = segs[i];
                    if (railMode && !s.railKeep) continue;

                    string d = PathD(s, map);
                    if (s.car)
                    {
                        int lay = LAYER[s.cat];
                        string fc = railMode ? RailSlopeColor(s.pct) : SlopeColor(s.pct);
                        float sw = Mathf.Max(1f, s.w * (map.w / map.spanx) * 0.9f);
                        Item it = new Item();
                        it.lay = lay; it.y = s.yavg; it.sub = 1;
                        it.s = "<path d=\"" + d + "\" fill=\"none\" stroke=\"" + fc + "\" stroke-width=\"" + F2(sw) + "\" stroke-linecap=\"round\"/>";
                        items.Add(it);

                        // 路肩缘线（类别色）
                        EdgePaths(s, map, px, pz, ref items, lay);

                        // 坡度标注
                        if (labelsOn && s.pct > labelMin && s.pct <= 60f && s.dist > labelMinLen)
                        {
                            string lb = LabelAt(s, map, px, pz, 28f, 15);
                            if (lb != null) labels.Add(lb);
                        }
                    }
                    else
                    {
                        int lay = LAYER[s.cat];
                        string col2 = railMode ? RailSlopeColor(s.pct) : CAT_EDGE[s.cat];
                        float sw = Mathf.Max(0.8f, s.w * (map.w / map.spanx) * 0.9f);
                        Item it = new Item();
                        it.lay = lay; it.y = s.yavg; it.sub = 1;
                        it.s = "<path d=\"" + d + "\" fill=\"none\" stroke=\"" + col2 + "\" stroke-width=\"" + F2(sw) + "\" stroke-linecap=\"round\"/>";
                        items.Add(it);

                        if (railMode && labelsOn && s.pct > labelMin && s.pct <= 60f && s.dist > labelMinLen)
                        {
                            string lb = LabelAt(s, map, px, pz, 22f, 13);
                            if (lb != null) labels.Add(lb);
                        }
                    }
                }

                // ---- 排序（层 → 高度 → 主/缘）----
                items.Sort(delegate(Item x, Item y)
                {
                    int c = x.lay.CompareTo(y.lay);
                    if (c != 0) return c;
                    c = x.y.CompareTo(y.y);
                    if (c != 0) return c;
                    return x.sub.CompareTo(y.sub);
                });

                // ---- 组装 ----
                StringBuilder sb = new StringBuilder(1 << 22);
                sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
                sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append((int)W)
                  .Append("\" height=\"").Append((int)H)
                  .Append("\" viewBox=\"0 0 ").Append((int)W).Append(' ').Append((int)H).Append("\">\n");
                sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"#0d1117\"/>\n");
                for (int i = 0; i < items.Count; i++) { sb.Append(items[i].s); sb.Append('\n'); }
                for (int i = 0; i < labels.Count; i++) { sb.Append(labels[i]); sb.Append('\n'); }
                sb.Append(Legend(map, railMode, city, segs.Count));
                sb.Append("\n</svg>");

                // ---- 写文件 ----
                fileName = (railMode ? "slope_rail_" : "slope_road_")
                    + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".svg";
                Directory.CreateDirectory(outDir);
                fullPath = Path.Combine(outDir, fileName);
                File.WriteAllText(fullPath, sb.ToString(), new UTF8Encoding(false));
                ok = true;
            }
            catch (Exception e)
            {
                failMsg = e.Message;
                Debug.LogError("[RoadSlopeViewer] SVG 导出失败: " + e);
            }
            finally
            {
                lock (_lock)
                {
                    _notify = true;
                    _notifyOk = ok;
                    _notifyMsg = ok ? fileName : (failMsg != null ? failMsg : "未知错误");
                    _notifyPath = fullPath != null ? fullPath : "";
                }
                if (ok)
                {
                    LastResultFile = fileName;
                    LastResultPath = fullPath;
                    Debug.Log("[RoadSlopeViewer] SVG 已导出: " + fullPath);
                }
                Busy = false;
            }
        }

        // ---- 单段路径 ----

        private static string PathD(Seg s, Map2Svg map)
        {
            float x1, y1, c1x, c1y, c2x, c2y, x2, y2;
            map.P(s.x1, s.z1, out x1, out y1);
            map.P(s.cx1, s.cz1, out c1x, out c1y);
            map.P(s.cx2, s.cz2, out c2x, out c2y);
            map.P(s.x2, s.z2, out x2, out y2);
            return "M " + F1(x1) + " " + F1(y1)
                 + " C " + F1(c1x) + " " + F1(c1y)
                 + " " + F1(c2x) + " " + F1(c2y)
                 + " " + F1(x2) + " " + F1(y2);
        }

        /// <summary>路肩左右缘线（贝塞尔采样 11 点 + 法线偏移 w/2），追加到 items。</summary>
        private static void EdgePaths(Seg s, Map2Svg map, float[] px, float[] pz, ref List<Item> items, int lay)
        {
            const int N = 10;
            for (int i = 0; i <= N; i++)
            {
                float t = i / (float)N;
                float mt = 1f - t;
                float a = mt * mt * mt, b = 3f * mt * mt * t, c = 3f * mt * t * t, d = t * t * t;
                px[i] = a * s.x1 + b * s.cx1 + c * s.cx2 + d * s.x2;
                pz[i] = a * s.z1 + b * s.cz1 + c * s.cz2 + d * s.z2;
            }
            float hw = s.w * 0.5f;
            StringBuilder l = new StringBuilder(160);
            StringBuilder r = new StringBuilder(160);
            bool started = false;
            for (int i = 0; i <= N; i++)
            {
                float dx, dz;
                if (i == 0) { dx = px[1] - px[0]; dz = pz[1] - pz[0]; }
                else if (i == N) { dx = px[N] - px[N - 1]; dz = pz[N] - pz[N - 1]; }
                else { dx = px[i + 1] - px[i - 1]; dz = pz[i + 1] - pz[i - 1]; }
                float len = Mathf.Sqrt(dx * dx + dz * dz);
                if (len < 1e-6f) continue;
                float nx = -dz / len, nz = dx / len;
                float lx, ly, rx, ry;
                map.P(px[i] + nx * hw, pz[i] + nz * hw, out lx, out ly);
                map.P(px[i] - nx * hw, pz[i] - nz * hw, out rx, out ry);
                if (started) { l.Append(" L "); r.Append(" L "); }
                else { l.Append("M "); r.Append("M "); started = true; }
                l.Append(F1(lx)).Append(' ').Append(F1(ly));
                r.Append(F1(rx)).Append(' ').Append(F1(ry));
            }
            if (!started) return;
            Item it1 = new Item();
            it1.lay = lay; it1.y = s.yavg; it1.sub = 2;
            it1.s = "<path d=\"" + l + "\" fill=\"none\" stroke=\"" + CAT_EDGE[s.cat] + "\" stroke-width=\"1.0\" stroke-linecap=\"round\"/>";
            items.Add(it1);
            Item it2 = new Item();
            it2.lay = lay; it2.y = s.yavg; it2.sub = 2;
            it2.s = "<path d=\"" + r + "\" fill=\"none\" stroke=\"" + CAT_EDGE[s.cat] + "\" stroke-width=\"1.0\" stroke-linecap=\"round\"/>";
            items.Add(it2);
        }

        /// <summary>陡坡标注（线段中点沿法线外偏 off 世界单位）。</summary>
        private static string LabelAt(Seg s, Map2Svg map, float[] px, float[] pz, float off, int fontSize)
        {
            const int N = 10;
            for (int i = 0; i <= N; i++)
            {
                float t = i / (float)N;
                float mt = 1f - t;
                float a = mt * mt * mt, b = 3f * mt * mt * t, c = 3f * mt * t * t, d = t * t * t;
                px[i] = a * s.x1 + b * s.cx1 + c * s.cx2 + d * s.x2;
                pz[i] = a * s.z1 + b * s.cz1 + c * s.cz2 + d * s.z2;
            }
            int mid = N / 2;
            int k = Math.Min(N / 2 + 1, N);
            float tx = px[k] - px[k - 2], tz = pz[k] - pz[k - 2];
            float len = Mathf.Sqrt(tx * tx + tz * tz);
            if (len < 1e-6f) return null;
            float nx = -tz / len, nz = tx / len;
            float lx, ly;
            map.P(px[mid] + nx * off, pz[mid] + nz * off, out lx, out ly);
            return "<text x=\"" + F1(lx) + "\" y=\"" + F1(ly) + "\" font-size=\"" + fontSize
                + "\" font-family=\"sans-serif\" fill=\"#ffd43b\" stroke=\"#0d1117\" stroke-width=\"3\" "
                + "paint-order=\"stroke\" text-anchor=\"middle\">" + (int)Math.Round(s.pct) + "%</text>";
        }

        // ---- 图例与标题（文案与 map_export.py 保持一致，已做 XML 转义）----

        private static string Legend(Map2Svg map, bool railMode, string city, int segCount)
        {
            string[] colors;
            string[] texts;
            if (railMode)
            {
                colors = new string[] { "#2f9e44", "#fcc419", "#f76707", "#c92a2a", "#ffd43b" };
                texts = new string[] {
                    "轨道坡度 &lt;3%（干线铁路限坡）",
                    "3-6%（地铁/单轨上限）",
                    "6-10%（电车极限）",
                    "&gt;10%（现实中无解）",
                    "数字 = 坡度 %（标注 &gt;10% 且长 &gt;80m 的段）" };
            }
            else
            {
                colors = new string[] { "#2f9e44", "#fcc419", "#f76707", "#c92a2a", "#8a93a0", "#c43d3d", "#3f6fbf", "#ffd43b" };
                texts = new string[] {
                    "坡度 &lt;8%（现实市区限坡）",
                    "8-12%（偏陡，山区道路极限）",
                    "12-20%（明显过度）",
                    "&gt;20%（现实中不可能的建筑）",
                    "路肩灰 = 原版道路",
                    "路肩红 = RBR/RBH 自制",
                    "路肩蓝 = 其他 mod 道路",
                    "数字 = 坡度 %（标注 &gt;20% 且长 &gt;60m 的段）" };
            }

            float lx = map.w - 340f;
            if (lx < 10f) lx = 10f;
            float ly = 30f;
            StringBuilder sw = new StringBuilder(1024);
            sw.Append("<g>");
            for (int i = 0; i < colors.Length; i++)
            {
                float y = ly + i * 28f;
                sw.Append("<rect x=\"").Append(F1(lx)).Append("\" y=\"").Append(F1(y))
                  .Append("\" width=\"14\" height=\"14\" rx=\"3\" fill=\"").Append(colors[i]).Append("\" opacity=\"0.9\"/>");
                sw.Append("<text x=\"").Append(F1(lx + 20f)).Append("\" y=\"").Append(F1(y + 12f))
                  .Append("\" fill=\"#cfd6dd\" font-size=\"14\" font-family=\"sans-serif\">").Append(texts[i]).Append("</text>");
            }
            // 信息行（范围 / 段数 / 时间 / 城市）
            string info = "范围 " + ((int)map.spanx) + "×" + ((int)map.spanz) + " 单位 · " + segCount + " 段 · 导出 "
                + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(city)) info += " · " + city;
            sw.Append("<text x=\"").Append(F1(lx)).Append("\" y=\"").Append(F1(ly + colors.Length * 28f + 4f))
              .Append("\" fill=\"#6b7680\" font-size=\"12\" font-family=\"sans-serif\">").Append(Esc(info)).Append("</text>");
            sw.Append("</g>");
            return sw.ToString();
        }

        // ================= Pump（主线程） =================

        /// <summary>把后台完成通知转回主线程（由 RsvPump 每帧调用；Debug.Log 已在渲染线程记录）。</summary>
        public static void Pump()
        {
            if (!_notify) return;
            string msg, path;
            bool ok;
            lock (_lock)
            {
                if (!_notify) return;
                msg = _notifyMsg; path = _notifyPath; ok = _notifyOk;
                _notify = false;
            }
            try
            {
                OptionsUI.NotifyExportDone(msg, path, ok);
                if (ok)
                {
                    DebugOutputPanel.AddMessage(PluginManager.MessageType.Message,
                        "[RoadSlopeViewer] SVG 已导出：" + path);
                }
                else
                {
                    DebugOutputPanel.AddMessage(PluginManager.MessageType.Warning,
                        "[RoadSlopeViewer] SVG 导出失败：" + msg);
                }
            }
            catch { }
        }

        // ================= 工具 =================

        private static int Classify(string name)
        {
            if (string.IsNullOrEmpty(name)) return CatOther;
            if (Contains(name, "RBR") || Contains(name, "RBH") || Contains(name, "反向")) return CatRbr;
            if (Contains(name, "Road"))   // 汽车道路（含 Road+Monorail/Tram 共线路）
                return IsDigit(name[0]) ? CatMod : CatVanilla;
            if (AnyKw(name, PIPE_KW)) return CatPipe;
            if (AnyKw(name, TRACK_KW)) return CatTrack;
            if (AnyKw(name, PATH_KW)) return CatPath;
            if (AnyKw(name, PED_KW)) return CatPed;
            if (AnyKw(name, FENCE_KW)) return CatFence;
            if (IsDigit(name[0])) return CatMod;   // 数字 ID 前缀 → workshop 资产
            if (AnyKw(name, VANILLA_KW)) return CatVanilla;
            return CatMod;
        }

        private static bool Contains(string s, string sub) { return s.IndexOf(sub, StringComparison.Ordinal) >= 0; }

        private static bool AnyKw(string s, string[] kws)
        {
            for (int i = 0; i < kws.Length; i++) if (Contains(s, kws[i])) return true;
            return false;
        }

        private static bool IsDigit(char c) { return c >= '0' && c <= '9'; }

        private static float Horiz(Vector3 a, Vector3 b)
        {
            float dx = b.x - a.x, dz = b.z - a.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static string SlopeColor(float pct)
        {
            if (pct < 8f) return "#2f9e44";
            if (pct < 12f) return "#fcc419";
            if (pct < 20f) return "#f76707";
            return "#c92a2a";
        }

        private static string RailSlopeColor(float pct)
        {
            if (pct < 3f) return "#2f9e44";
            if (pct < 6f) return "#fcc419";
            if (pct < 10f) return "#f76707";
            return "#c92a2a";
        }

        private static string F1(float v) { return v.ToString("0.0", CultureInfo.InvariantCulture); }
        private static string F2(float v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}
