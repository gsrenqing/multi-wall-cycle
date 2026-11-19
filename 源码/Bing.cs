using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Web.Script.Serialization;

namespace MultiWallCycle
{
    /// <summary>v2.2：必应"最近若干天"壁纸条目（每日一张，含当天与之前 7 天）。</summary>
    public class BingEntry
    {
        /// <summary>yyyy-MM-dd（取自接口 startdate；同一张图的缓存名与描述键都用它，跨时区也唯一）。</summary>
        public string Date = string.Empty;
        /// <summary>形如 /th?id=OHR.DiwaliLamps_ZH-CN2462894749，拼上 _UHD.jpg 即为 4K 直链。</summary>
        public string UrlBase = string.Empty;
        /// <summary>接口给的"标题+地点+摄影师"，直接用于文字描述。</summary>
        public string Copyright = string.Empty;
    }

    /// <summary>
    /// Bing 每日壁纸来源（v1.1.2）：
    /// - 调用 Bing HPImageArchive 接口获取当日图（UHD 4K 优先，失败回退 1080p）与 copyright 描述文字；
    /// - 维护"日期→描述文字"映射（与 config 同目录 bing_meta.json，滚动保留最近 90 天，体积恒定）；
    /// - GDI+ 在图片右下角渲染"标题+地点+摄影师"文字条（半透明黑底白字，超长省略）。
    /// v2.2：接口 idx=0&n=8 可一次取到"当天 + 之前 7 天"共 8 张，仅必应来源改为在这 8 张里轮换。
    /// 来源为 Bing 官方公开接口（个人使用版权合规：显示 © 信息即保留署名）。
    /// </summary>
    public static class BingWallpaper
    {
        private const int MetaKeepDays = 90;
        private static string MetaPath { get { return Path.Combine(ConfigIO.ConfigDir, "bing_meta.json"); } }

        static BingWallpaper()
        {
            // .NET 4.8 WebClient 在 Win11 默认 TLS 协商可能失败（"基础连接已经关闭"），显式启用 TLS 1.2
            // v2.4（采纳审查 #13）：改成按位加，不再覆盖 .NET 4.8 默认的其它协议
            try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; }
            catch { }
            try { System.Net.ServicePointManager.Expect100Continue = false; } catch { }
        }

        /// <summary>本地文件夹中由 Bing 来源的文件（旧格式 Bing_YYYY-MM-DD.jpg 或 Bing 官方原始文件名 *_UHD.jpg / *_1920x1080.jpg）。</summary>
        public static bool IsBingFile(string path)
        {
            string name = Path.GetFileName(path);
            if (name.StartsWith("Bing_", StringComparison.OrdinalIgnoreCase)) { return true; }
            if (name.EndsWith("_UHD.jpg", StringComparison.OrdinalIgnoreCase)) { return true; }
            if (name.EndsWith("_1920x1080.jpg", StringComparison.OrdinalIgnoreCase)) { return true; }
            return false;
        }

        /// <summary>
        /// 取描述映射键：Bing_YYYY-MM-DD.jpg → 日期；Bing 原始文件名（*_UHD.jpg / *_1920x1080.jpg）→ 完整文件名。
        /// 两者都能在 bing_meta.json 中命中对应描述（v1.1.2 起入库同时写日期键与原始文件名键）。
        /// </summary>
        public static string FileDate(string path)
        {
            string name = Path.GetFileName(path);
            if (name.StartsWith("Bing_", StringComparison.OrdinalIgnoreCase) && name.Length >= 15)
            {
                return name.Substring(5, 10);
            }
            if (name.EndsWith("_UHD.jpg", StringComparison.OrdinalIgnoreCase)) { return name; }
            if (name.EndsWith("_1920x1080.jpg", StringComparison.OrdinalIgnoreCase)) { return name; }
            return string.Empty;
        }

        /// <summary>
        /// 下载当日 Bing 壁纸原图（UHD 4K 优先，失败回退 1080p）。成功返回 true 并写入元数据。
        /// targetDir：目标目录；useOriginalName：true → 保存为 Bing 原始文件名（入库，如 AlbaceteSpain_ZH-CN1597281896_UHD.jpg），
        /// false → 保存为 bing_YYYY-MM-DD.jpg（内部当日缓存，便于当天复用判断）。
        /// 描述映射同时写"本地日期键"与"原始文件名键"（v1.1.2 起，轮换命中任一文件名格式都能取到描述）。
        /// </summary>
        public static bool TryDownloadToday(string targetDir, bool useOriginalName,
            out string caption, out string fileName)
        {
            caption = string.Empty;
            fileName = string.Empty;
            try
            {
                // 国内网络下 www.bing.com 可能被重置，cn.bing.com 更稳 → cn 优先，www 回退
                string[] hosts = { "https://cn.bing.com", "https://www.bing.com" };
                string json = null;
                string usedHost = null;
                foreach (string host in hosts)
                {
                    try
                    {
                        using (WebClient wc = new WebClient())
                        {
                            wc.Encoding = System.Text.Encoding.UTF8;
                            wc.Headers.Add("User-Agent", "Mozilla/5.0");
                            json = wc.DownloadString(host + "/HPImageArchive.aspx?format=js&idx=0&n=1&mkt=zh-CN");
                        }
                        usedHost = host;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Bing 接口访问失败(" + host + "): " + ex.Message);
                    }
                }
                if (json == null || usedHost == null)
                {
                    Log.Warn("Bing 接口全部域名访问失败");
                    return false;
                }
                JavaScriptSerializer js = new JavaScriptSerializer();
                Dictionary<string, object> root = null;
                try { root = js.Deserialize<Dictionary<string, object>>(json); }
                catch (Exception ex) { Log.Warn("Bing 接口响应解析失败: " + ex.Message); }
                object imagesObj;
                if (root == null || !root.TryGetValue("images", out imagesObj))
                {
                    Log.Warn("Bing 接口响应缺少 images 字段: " + (json.Length > 200 ? json.Substring(0, 200) : json));
                    return false;
                }
                // JavaScriptSerializer 把 JSON 数组反序列化为 ArrayList（非 List<object>），两种都兼容
                List<object> images = new List<object>();
                if (imagesObj is List<object>) { images = (List<object>)imagesObj; }
                else if (imagesObj is System.Collections.ArrayList)
                {
                    foreach (object o in (System.Collections.ArrayList)imagesObj) { images.Add(o); }
                }
                if (images.Count == 0)
                {
                    Log.Warn("Bing 接口响应 images 为空");
                    return false;
                }
                Dictionary<string, object> img = images[0] as Dictionary<string, object>;
                if (img == null) { Log.Warn("Bing 接口响应 images[0] 结构异常"); return false; }

                string urlbase = img.ContainsKey("urlbase") ? img["urlbase"].ToString() : string.Empty;
                if (img.ContainsKey("copyright")) { caption = img["copyright"].ToString(); }
                // v2.4：短标题（必应 title 字段）与检索链接，一并写进 EXIF，和《动态壁纸》的存量文件同款
                string shortTitle = img.ContainsKey("title") ? img["title"].ToString() : string.Empty;
                string searchUrl = img.ContainsKey("url") ? img["url"].ToString() : string.Empty;
                if (!string.IsNullOrEmpty(searchUrl) && searchUrl.StartsWith("/", StringComparison.Ordinal))
                {
                    searchUrl = "https://www.bing.com" + searchUrl;
                }
                // 元数据日期统一用本地日期（与缓存名一致，避免 startdate 美东时区差一天导致取不到描述）
                string date = DateTime.Now.ToString("yyyy-MM-dd");
                if (string.IsNullOrEmpty(urlbase)) { return false; }

                // 原始文件名：urlbase 形如 "/th?id=OHR.AlbaceteSpain_ZH-CN1597281896" → "AlbaceteSpain_ZH-CN1597281896_UHD.jpg"
                string baseName = urlbase;
                int q = baseName.IndexOf('?');
                if (q >= 0) { baseName = baseName.Substring(q + 1); }
                if (baseName.StartsWith("id=", StringComparison.OrdinalIgnoreCase)) { baseName = baseName.Substring(3); }
                if (baseName.StartsWith("OHR.", StringComparison.OrdinalIgnoreCase)) { baseName = baseName.Substring(4); }
                if (baseName.IndexOf('/') >= 0) { baseName = baseName.Substring(baseName.LastIndexOf('/') + 1); }
                if (string.IsNullOrEmpty(baseName)) { baseName = "Bing" + date.Replace("-", ""); }

                // 图片用与接口相同的成功域名；UHD 4K 优先 → 1080p 回退
                // 先下载到临时名，成功后按实际成功候选定最终文件名（保证文件名与内容一致）
                string[] candidates = new[] {
                    usedHost + urlbase + "_UHD.jpg",
                    usedHost + urlbase + "_1920x1080.jpg"
                };
                string tmpPath = Path.Combine(targetDir, "_dl_" + DateTime.Now.ToString("HHmmssfff") + ".jpg");
                string savedUrl = DownloadFirst(tmpPath, candidates);
                if (savedUrl == null)
                {
                    Log.Warn("Bing 图片下载失败(全部候选): " + string.Join(" | ", candidates));
                    return false;
                }
                string suffix = savedUrl.EndsWith("_1920x1080.jpg", StringComparison.OrdinalIgnoreCase)
                    ? "_1920x1080.jpg" : "_UHD.jpg";
                fileName = baseName + suffix;
                string finalPath = Path.Combine(targetDir,
                    useOriginalName ? fileName : ("bing_" + date + ".jpg"));
                if (!finalPath.Equals(tmpPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(finalPath)) { try { File.Delete(finalPath); } catch { } }
                    File.Move(tmpPath, finalPath);
                }
                SaveMeta(date, caption, fileName);

                // v2.4：把标题真正写进图片本体（EXIF），不再只依赖 bing_meta.json——
                // meta 会在 90 天后被裁剪、换机器/换数据目录就丢，写进文件则走到哪跟到哪。
                if (useOriginalName)
                {
                    string desc, artist;
                    SplitCopyright(caption, out desc, out artist);
                    string err;
                    if (ImageMeta.TryWriteTitle(finalPath, desc, artist, shortTitle, searchUrl, out err))
                    {
                        Log.Info("已把必应标题写入图片 EXIF: " + fileName);
                    }
                    else
                    {
                        Log.Warn("必应标题写入 EXIF 失败（不影响使用，仍可用 meta 显示）: " + err);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Bing 壁纸获取失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// v2.4：把必应的 "正文 (© 摄影师)" 拆成正文与摄影师两段——
        /// 《动态壁纸》的存量文件就是这么分开存的（0x010E 只放正文，摄影师单独进 0x013B）。
        /// </summary>
        private static void SplitCopyright(string caption, out string desc, out string artist)
        {
            desc = caption == null ? string.Empty : caption.Trim();
            artist = string.Empty;
            if (string.IsNullOrEmpty(desc)) { return; }
            int p = desc.LastIndexOf("(©", StringComparison.Ordinal);
            if (p < 0) { return; }
            int close = desc.LastIndexOf(')');
            if (close <= p) { return; }
            artist = desc.Substring(p + 2, close - p - 2).Trim();
            desc = desc.Substring(0, p).Trim();
        }

        /// <summary>按候选 URL 依次下载到 savePath；成功返回该 URL，全部失败返回 null。</summary>
        private static string DownloadFirst(string savePath, string[] candidates)
        {
            foreach (string url in candidates)
            {
                try
                {
                    using (WebClient wc = new WebClient())
                    {
                        wc.Headers.Add("User-Agent", "Mozilla/5.0");
                        wc.DownloadFile(url, savePath);
                    }
                    // v2.4（采纳审查 #6）：不能只看 >0 字节——错误页/半截文件也会被当成壁纸。
                    // 统一按"≥20KB 且以 JPEG SOI(FF D8) 开头"校验，与 TryDownloadEntry 一致。
                    FileInfo fi = new FileInfo(savePath);
                    if (fi.Length >= 20 * 1024 && LooksLikeJpeg(savePath)) { return url; }
                    Log.Warn("下载结果不是有效 JPEG 或体积过小: " + url + " (" + fi.Length + " 字节)");
                }
                catch { }
            }
            return null;
        }

        /// <summary>文件头是否为 JPEG SOI（FF D8 FF）。</summary>
        internal static bool LooksLikeJpeg(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int a = fs.ReadByte(), b = fs.ReadByte(), c = fs.ReadByte();
                    return a == 0xFF && b == 0xD8 && c == 0xFF;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// v2.3：「必应每日壁纸」字样的预览链接落点（修正 v2.2 指向 Bing 首页的 BUG）。
        /// 必应每日壁纸的第三方归档站 bing.wdbyte.com 按「日」出页，地址规则为
        ///   https://bing.wdbyte.com/day/{yyyyMM}/{dd}.html
        /// 例如 2026-11-13 → https://bing.wdbyte.com/day/202611/13.html （实测可访问）。
        /// 优先用"当前日期"即时生成的当日页；当日页尚未发布（站方通常次日才归档）或网络不可达时，
        /// 依次退回昨天，最终退回该站首页（用户明确接受此降级方案）。
        /// </summary>
        public const string HomeUrl = "https://bing.wdbyte.com/";

        public static string DayUrl(DateTime day)
        {
            return HomeUrl + "day/" + day.ToString("yyyyMM") + "/" + day.ToString("dd") + ".html";
        }

        private static string _previewCacheDay = string.Empty;
        private static string _previewCacheUrl = null;

        /// <summary>按当前日期解析出可用的必应每日壁纸页地址（结果按天缓存，点击时才发请求）。</summary>
        public static string ResolvePreviewUrl()
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (_previewCacheUrl != null && string.Equals(_previewCacheDay, today, StringComparison.Ordinal))
            {
                return _previewCacheUrl;
            }
            string url = HomeUrl;
            DateTime now = DateTime.Now;
            for (int back = 0; back <= 1; back++)
            {
                string cand = DayUrl(now.AddDays(-back));
                if (UrlExists(cand))
                {
                    url = cand;
                    Log.Info("必应每日壁纸预览链接: " + cand);
                    break;
                }
            }
            if (string.Equals(url, HomeUrl, StringComparison.Ordinal))
            {
                Log.Info("必应每日壁纸当日页不可用，回退首页: " + HomeUrl);
            }
            _previewCacheDay = today;
            _previewCacheUrl = url;
            return url;
        }

        /// <summary>
        /// 轻量探测：该日页是否存在。先用 HEAD（不下载正文，站点响应快），
        /// HEAD 被拒（405/501 等）时退回 GET；两条路径都只认 200，任何异常按"不可用"处理。
        /// v2.3 实测：wdbyte 站点 GET 整页常需 3 秒以上，用 2.5 秒的 GET 探测会把"今天"误判成不可用，
        /// 因此改为 HEAD-first 并按 4 秒超时。
        /// </summary>
        private static bool UrlExists(string url)
        {
            if (ProbeOne(url, "HEAD", 4000)) { return true; }
            return ProbeOne(url, "GET", 4000);
        }

        private static bool ProbeOne(string url, string method, int timeoutMs)
        {
            HttpWebRequest req = null;
            HttpWebResponse resp = null;
            try
            {
                req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = method;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.AllowAutoRedirect = true;
                req.UserAgent = "Mozilla/5.0";
                resp = (HttpWebResponse)req.GetResponse();
                return resp.StatusCode == HttpStatusCode.OK;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { if (resp != null) { resp.Close(); } } catch { }
            }
        }

        /// <summary>
        /// v2.2：拉取"最近 count 天"的壁纸条目（实测 n=8 可取 8 条：当天 + 之前 7 天）。
        /// 成功时顺带把这些日期的描述写进 bing_meta.json（一次落盘），供后续按日期/文件名取描述。
        /// </summary>
        public static bool TryGetRecent(int count, out List<BingEntry> entries, out string host)
        {
            entries = new List<BingEntry>();
            host = null;
            if (count < 1) { count = 1; }
            string json = FetchArchiveJson(count, out host);
            if (json == null || host == null) { return false; }

            JavaScriptSerializer js = new JavaScriptSerializer();
            Dictionary<string, object> root = null;
            try { root = js.Deserialize<Dictionary<string, object>>(json); }
            catch (Exception ex) { Log.Warn("Bing 近几日接口响应解析失败: " + ex.Message); }

            object imagesObj;
            if (root == null || !root.TryGetValue("images", out imagesObj)) { return false; }
            List<object> images = new List<object>();
            if (imagesObj is List<object>) { images = (List<object>)imagesObj; }
            else if (imagesObj is System.Collections.ArrayList)
            {
                foreach (object o in (System.Collections.ArrayList)imagesObj) { images.Add(o); }
            }

            Dictionary<string, string> metaBatch = new Dictionary<string, string>();
            foreach (object o in images)
            {
                Dictionary<string, object> img = o as Dictionary<string, object>;
                if (img == null) { continue; }
                string urlbase = img.ContainsKey("urlbase") ? img["urlbase"].ToString() : string.Empty;
                if (string.IsNullOrEmpty(urlbase)) { continue; }
                string startdate = img.ContainsKey("startdate") ? img["startdate"].ToString() : string.Empty;
                string caption = img.ContainsKey("copyright") ? img["copyright"].ToString() : string.Empty;

                BingEntry e = new BingEntry();
                e.UrlBase = urlbase;
                e.Date = ConvertBingDate(startdate);
                e.Copyright = caption;
                entries.Add(e);

                if (!string.IsNullOrEmpty(e.Date) && caption.Length > 0)
                {
                    metaBatch[e.Date] = caption;
                    // 原始文件名键（双勾入库模式按文件名取描述用）
                    string baseName = BaseNameOf(urlbase);
                    if (baseName.Length > 0)
                    {
                        metaBatch[baseName + "_UHD.jpg"] = caption;
                        metaBatch[baseName + "_1920x1080.jpg"] = caption;
                    }
                }
            }
            if (metaBatch.Count > 0) { SaveMetaBatch(metaBatch); }
            return entries.Count > 0;
        }

        /// <summary>把接口 startdate（yyyyMMdd）转成 yyyy-MM-dd；异常返回空串。</summary>
        private static string ConvertBingDate(string startdate)
        {
            try
            {
                if (startdate == null || startdate.Length < 8) { return string.Empty; }
                DateTime d = DateTime.ParseExact(startdate.Substring(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture);
                return d.ToString("yyyy-MM-dd");
            }
            catch { return string.Empty; }
        }

        /// <summary>urlbase（/th?id=OHR.AlbaceteSpain_ZH-CN1597281896）→ AlbaceteSpain_ZH-CN1597281896。</summary>
        private static string BaseNameOf(string urlbase)
        {
            string baseName = urlbase ?? string.Empty;
            int q = baseName.IndexOf('?');
            if (q >= 0) { baseName = baseName.Substring(q + 1); }
            if (baseName.StartsWith("id=", StringComparison.OrdinalIgnoreCase)) { baseName = baseName.Substring(3); }
            if (baseName.StartsWith("OHR.", StringComparison.OrdinalIgnoreCase)) { baseName = baseName.Substring(4); }
            if (baseName.IndexOf('/') >= 0) { baseName = baseName.Substring(baseName.LastIndexOf('/') + 1); }
            return baseName;
        }

        /// <summary>按 count 拉取 HPImageArchive JSON；cn 优先 www 回退，成功返回 json 并给出可用域名。</summary>
        private static string FetchArchiveJson(int count, out string host)
        {
            host = null;
            string[] hosts = { "https://cn.bing.com", "https://www.bing.com" };
            foreach (string h in hosts)
            {
                try
                {
                    using (WebClient wc = new WebClient())
                    {
                        wc.Encoding = System.Text.Encoding.UTF8;
                        wc.Headers.Add("User-Agent", "Mozilla/5.0");
                        string json = wc.DownloadString(h + "/HPImageArchive.aspx?format=js&idx=0&n=" + count + "&mkt=zh-CN");
                        if (json != null && json.IndexOf("\"images\"", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            host = h;
                            return json;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Bing 近几日接口访问失败(" + h + "): " + ex.Message);
                }
            }
            Log.Warn("Bing 近几日接口全部域名访问失败");
            return null;
        }

        /// <summary>
        /// v2.2：把某一天的必应图下载到 tempDir\bing_&lt;日期&gt;.jpg（已存在且体积正常则直接复用）。
        /// UHD 4K 优先，失败回退 1920x1080；下载体积小于 20KB 视为错误页并继续尝试。
        /// </summary>
        public static bool TryDownloadEntry(BingEntry entry, string host, string tempDir, out string savedPath)
        {
            savedPath = string.Empty;
            if (entry == null || string.IsNullOrEmpty(entry.UrlBase) || string.IsNullOrEmpty(entry.Date)) { return false; }
            try
            {
                if (!Directory.Exists(tempDir)) { Directory.CreateDirectory(tempDir); }
                string finalPath = Path.Combine(tempDir, "bing_" + entry.Date + ".jpg");
                if (File.Exists(finalPath) && new FileInfo(finalPath).Length >= 20 * 1024)
                {
                    savedPath = finalPath;
                    return true;
                }

                List<string> hosts = new List<string>();
                if (!string.IsNullOrEmpty(host)) { hosts.Add(host); }
                if (!hosts.Contains("https://cn.bing.com")) { hosts.Add("https://cn.bing.com"); }
                if (!hosts.Contains("https://www.bing.com")) { hosts.Add("https://www.bing.com"); }

                string tmpPath = Path.Combine(tempDir, "_dl_" + DateTime.Now.ToString("HHmmssfff") + ".jpg");
                foreach (string h in hosts)
                {
                    string[] candidates = new[] {
                        h + entry.UrlBase + "_UHD.jpg",
                        h + entry.UrlBase + "_1920x1080.jpg"
                    };
                    foreach (string url in candidates)
                    {
                        try
                        {
                            if (File.Exists(tmpPath)) { try { File.Delete(tmpPath); } catch { } }
                            using (WebClient wc = new WebClient())
                            {
                                wc.Headers.Add("User-Agent", "Mozilla/5.0");
                                wc.DownloadFile(url, tmpPath);
                            }
                            if (!File.Exists(tmpPath) || new FileInfo(tmpPath).Length < 20 * 1024) { continue; }
                            if (File.Exists(finalPath)) { try { File.Delete(finalPath); } catch { } }
                            File.Move(tmpPath, finalPath);
                            savedPath = finalPath;
                            return true;
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("Bing 图片下载失败(" + url + "): " + ex.Message);
                        }
                    }
                }
                try { if (File.Exists(tmpPath)) { File.Delete(tmpPath); } } catch { }
                Log.Warn("Bing 图片全部候选下载失败: " + entry.UrlBase);
                return false;
            }
            catch (Exception ex)
            {
                Log.Warn("Bing 近几日图片获取失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>按"日期键或原始文件名键"取描述文字（本地映射；无则空串，调用方不显示）。</summary>
        public static string GetCaption(string key)
        {
            try
            {
                if (string.IsNullOrEmpty(key)) { return string.Empty; }
                Dictionary<string, string> meta = LoadMeta();
                string c;
                if (meta.TryGetValue(key, out c)) { return c; }
            }
            catch { }
            return string.Empty;
        }

        /// <summary>
        /// v2.4：任意源图的描述文字统一入口（不再要求文件名像必应图）。
        /// 顺序：meta（日期键 → 原始文件名键 → 纯文件名键）→ EXIF（0x010E / 0x9C9B）→ XMP dc:title。
        /// 本地文件夹里自己拍的照片、小红书存的图，只要文件里带标题，现在也能显示；
        /// 没有标题就返回空串，调用方不显示文字（不报错、不提示）。
        /// </summary>
        public static string GetCaptionForFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) { return string.Empty; }
                string name = Path.GetFileName(path);
                string caption = GetCaption(FileDate(path));
                if (caption.Length == 0) { caption = GetCaption(name); }
                if (caption.Length == 0)
                {
                    string inline = ImageMeta.ReadTitle(path);
                    if (inline.Length > 0)
                    {
                        CacheMetaKey(name, inline);
                        caption = inline;
                    }
                }
                return caption;
            }
            catch { return string.Empty; }
        }

        /// <summary>
        /// v2.4：保留旧名字，内部改走 ImageMeta（不再用 GDI+ 整幅解码图片取 PropertyItems）。
        /// 命中后仍写回 meta 文件名键缓存，避免每次轮换重复解析大文件。
        /// </summary>
        public static string GetExifCaption(string path)
        {
            try
            {
                string title = ImageMeta.ReadTitle(path);
                if (title.Length == 0) { return string.Empty; }
                CacheMetaKey(Path.GetFileName(path), title);
                return title;
            }
            catch { return string.Empty; }
        }

        /// <summary>UTF-8 严格解码 EXIF 文本（动态壁纸写入）；失败回退系统默认（旧版 ASCII 写入）。</summary>
        private static string DecodeExifUtf8(byte[] bytes)
        {
            try
            {
                if (bytes == null || bytes.Length == 0) { return string.Empty; }
                int len = bytes.Length;
                while (len > 0 && (bytes[len - 1] == 0 || bytes[len - 1] == 0x20)) { len--; }
                try
                {
                    return new System.Text.UTF8Encoding(false, true).GetString(bytes, 0, len).Trim();
                }
                catch { }
                try
                {
                    return new System.Text.ASCIIEncoding().GetString(bytes, 0, len).Trim();
                }
                catch { return string.Empty; }
            }
            catch { return string.Empty; }
        }

        /// <summary>UTF-16LE 解码 EXIF 文本（XPTitle 固定此编码）。</summary>
        private static string DecodeExifUtf16LE(byte[] bytes)
        {
            try
            {
                if (bytes == null || bytes.Length == 0) { return string.Empty; }
                int len = bytes.Length;
                while (len >= 2 && bytes[len - 1] == 0 && bytes[len - 2] == 0) { len -= 2; }
                return System.Text.Encoding.Unicode.GetString(bytes, 0, len).Trim();
            }
            catch { return string.Empty; }
        }

        /// <summary>把文件名键写入 meta 缓存（不触发 90 天清理，仅补充 EXIF 提取结果）。</summary>
        private static void CacheMetaKey(string key, string caption)
        {
            try
            {
                if (string.IsNullOrEmpty(key)) { return; }
                Dictionary<string, string> meta = LoadMeta();
                string c;
                if (meta.TryGetValue(key, out c)) { return; }   // 已有则不动（保留接口/缓存优先级）
                meta[key] = caption;
                JavaScriptSerializer js = new JavaScriptSerializer();
                string dir = Path.GetDirectoryName(MetaPath);
                if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
                File.WriteAllText(MetaPath, js.Serialize(meta));
            }
            catch { }
        }

        private static void SaveMeta(string date, string caption, string fileName)
        {
            try
            {
                Dictionary<string, string> meta = LoadMeta();
                meta[date] = caption;
                if (!string.IsNullOrEmpty(fileName)) { meta[fileName] = caption; }
                WriteMeta(meta);
            }
            catch { }
        }

        /// <summary>v2.2：一次写入多天描述（拉取"最近 8 天"列表时用，避免 8 次读写文件）。</summary>
        private static void SaveMetaBatch(Dictionary<string, string> batch)
        {
            try
            {
                if (batch == null || batch.Count == 0) { return; }
                Dictionary<string, string> meta = LoadMeta();
                foreach (KeyValuePair<string, string> kv in batch)
                {
                    if (kv.Key == null || kv.Key.Length == 0) { continue; }
                    meta[kv.Key] = kv.Value ?? string.Empty;
                }
                WriteMeta(meta);
            }
            catch { }
        }

        /// <summary>落盘描述映射，并滚动清理 90 天前的"日期键"（原始文件名键保留）。</summary>
        private static void WriteMeta(Dictionary<string, string> meta)
        {
            // 只滚动清理"日期键"（90 天）；原始文件名键体积很小（~40KB/年）且与日期无关联，保留
            DateTime cutoff = DateTime.Today.AddDays(-MetaKeepDays);
            List<string> drop = new List<string>();
            foreach (KeyValuePair<string, string> kv in meta)
            {
                DateTime d;
                if (DateTime.TryParseExact(kv.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out d) && d < cutoff) { drop.Add(kv.Key); }
            }
            foreach (string k in drop) { meta.Remove(k); }
            JavaScriptSerializer js = new JavaScriptSerializer();
            string dir = Path.GetDirectoryName(MetaPath);
            if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
            File.WriteAllText(MetaPath, js.Serialize(meta));
        }

        private static Dictionary<string, string> LoadMeta()
        {
            try
            {
                if (File.Exists(MetaPath))
                {
                    JavaScriptSerializer js = new JavaScriptSerializer();
                    Dictionary<string, string> m = js.Deserialize<Dictionary<string, string>>(File.ReadAllText(MetaPath));
                    if (m != null) { return m; }
                }
            }
            catch { }
            return new Dictionary<string, string>();
        }

        /// <summary>
        /// 按全局样式在图片上渲染描述文字（v1.1.2，对齐"动态壁纸"效果）：
        /// 无背景条、纯文字 + 偏移阴影、按图片宽度自动换行完整显示（不截断）、6 个锚点位置。
        /// v1.1.2 第三轮：所有锚点距屏幕边缘不小于 80 像素（按目标屏物理分辨率换算到原图），
        /// 避免任务栏位于屏幕任意一边时遮挡文字。
        /// v2.2 BUG①：字号与阴影偏移按"原图宽/屏幕宽"换算成原图像素，
        /// 因此在 4K 图、1080p 图、竖屏图上"在屏幕上看到的字一样大"，不再随壁纸分辨率忽大忽小。
        /// 描述为空或渲染失败时原样返回输入路径（不显示文字）。
        /// </summary>
        public static string RenderCaption(string inputPath, int index, string caption,
            string tempDir, CaptionStyle style, int screenW = 0, int screenH = 0)
        {
            if (string.IsNullOrEmpty(caption)) { return inputPath; }
            try
            {
                using (Bitmap bmp = new Bitmap(inputPath))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.TextRenderingHint = TextRenderingHint.AntiAlias;
                        int imgW = bmp.Width;
                        int imgH = bmp.Height;

                        // v2.2 BUG①：字号锁定"屏幕视觉大小"——
                        // 原图会被系统按屏幕宽度缩放显示，故 原图像素 = 屏幕像素 × (原图宽/屏幕宽)。
                        // 例：4K 图在 2560 宽屏上缩放 1.5 倍，18px 字号要画成 27px；
                        // 1080p 图同屏要放大 1.33 倍，18px 只需画成 13.5px → 两者上屏后都是 18px。
                        // v2.3：若调用方没拿到显示器尺寸（Rect 无效等），退回 1920×1080 估算，
                        // 否则字号会按 1:1 画在 4K 图上（上屏后只有几个像素，等于"看不见"）。
                        if (screenW <= 0) { screenW = 1920; }
                        if (screenH <= 0) { screenH = 1080; }
                        float uiScale = 1f;
                        if (screenW > 0 && imgW > 0) { uiScale = imgW / (float)screenW; }
                        if (uiScale < 0.2f) { uiScale = 0.2f; }
                        if (uiScale > 8f) { uiScale = 8f; }

                        FontStyle fs = FontStyle.Regular;
                        if (style != null && style.Style == 1) { fs = FontStyle.Bold; }
                        else if (style != null && style.Style == 2) { fs = FontStyle.Italic; }
                        float baseSize = (style != null && style.FontSize >= 8) ? style.FontSize : 18f;
                        float fontSize = baseSize * uiScale;
                        using (Font font = MakeFont(style != null ? style.FontFamily : string.Empty, fontSize, fs))
                        {
                            using (StringFormat sf = new StringFormat())
                            {
                                sf.Alignment = StringAlignment.Near;
                                sf.LineAlignment = StringAlignment.Near;
                                sf.Trimming = StringTrimming.None;
                                sf.FormatFlags = StringFormatFlags.NoClip;

                                // 距屏幕边缘 ≥80 物理像素的限位：按 原图/屏幕 缩放比换算成原图像素
                                float scaleX = (screenW > 0 && imgW > 0) ? imgW / (float)screenW : 1f;
                                float scaleY = (screenH > 0 && imgH > 0) ? imgH / (float)screenH : 1f;
                                float edgeX = 80f * scaleX;
                                float edgeY = 80f * scaleY;
                                if (edgeX > imgW / 2f) { edgeX = imgW / 2f; }
                                if (edgeY > imgH / 2f) { edgeY = imgH / 2f; }
                                float maxW = Math.Max(80f, imgW - 2f * edgeX);
                                SizeF sz = g.MeasureString(caption, font, new SizeF(maxW, 9999f), sf);
                                float boxW = Math.Min(sz.Width, maxW);
                                float boxH = sz.Height;

                                float x, y;
                                int pos = (style != null) ? style.Position : 2;
                                switch (pos)
                                {
                                    case 0: x = edgeX; y = imgH - boxH - edgeY; break;              // 左下角
                                    case 1: x = (imgW - boxW) / 2f; y = imgH - boxH - edgeY; break;  // 下方居中
                                    case 3: x = edgeX; y = edgeY; break;                            // 左上角
                                    case 4: x = (imgW - boxW) / 2f; y = edgeY; break;               // 上部居中
                                    case 5: x = imgW - boxW - edgeX; y = edgeY; break;              // 右上角
                                    default: x = imgW - boxW - edgeX; y = imgH - boxH - edgeY; break; // 右下角
                                }
                                RectangleF rect = new RectangleF(x, y, boxW, boxH);

                                // 阴影层（偏移式投影；阴影开且大小≥1 才画）；偏移同样按屏幕像素锁定
                                if (style != null && style.Shadow && style.ShadowSize >= 1)
                                {
                                    float off = style.ShadowSize * uiScale;
                                    using (SolidBrush sb = new SolidBrush(FromHex(style.ShadowColor, 160)))
                                    {
                                        g.DrawString(caption, font, sb,
                                            new RectangleF(x + off, y + off, boxW, boxH), sf);
                                    }
                                }
                                else
                                {
                                    // v2.3：关阴影时自动加一道 1px 对比描边影——实测白字直接压在雪景/亮天空上
                                    // 几乎看不见（用户反馈"设了文字描述但看不到"的主因之一）。
                                    // 按文字区域亮度自动取黑/白，只偏移 1 像素，不破坏"无阴影"的观感。
                                    bool brightBg = IsBrightRegion(bmp, rect);
                                    Color halo = brightBg
                                        ? Color.FromArgb(185, 0, 0, 0)
                                        : Color.FromArgb(185, 255, 255, 255);
                                    float ow = Math.Max(1f, fontSize * 0.06f);
                                    using (SolidBrush ob = new SolidBrush(halo))
                                    {
                                        g.DrawString(caption, font, ob,
                                            new RectangleF(x + ow, y + ow, boxW, boxH), sf);
                                        g.DrawString(caption, font, ob,
                                            new RectangleF(x - ow * 0.55f, y - ow * 0.55f, boxW, boxH), sf);
                                    }
                                }
                                using (SolidBrush fg = new SolidBrush(FromHex(
                                    style != null ? style.FontColor : "#FFFFFF", 255)))
                                {
                                    g.DrawString(caption, font, fg, rect, sf);
                                }
                            }
                        }
                    }
                    string outPath = Path.Combine(tempDir,
                        "screen" + (index + 1) + "_cap_" + DateTime.Now.ToString("yyyyMMddHHmmssfff") + ".jpg");
                    ImageCodecInfo codec = GetJpegCodec();
                    if (codec != null)
                    {
                        // v2.4（采纳审查 #12）：EncoderParameters 持有非托管资源，要 using 释放，
                        // 否则每次渲染文字都漏一小块（轮换频繁时累积）
                        using (EncoderParameters ep = new EncoderParameters(1))
                        {
                            ep.Param[0] = new EncoderParameter(Encoder.Quality, 92L);
                            bmp.Save(outPath, codec, ep);
                        }
                    }
                    else
                    {
                        bmp.Save(outPath, ImageFormat.Jpeg);
                    }
                    return outPath;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("文字渲染失败，使用原图: " + ex.Message);
                return inputPath;
            }
        }

        /// <summary>
        /// v2.3：判断文字将要覆盖的区域整体偏亮还是偏暗（抽样求平均灰度）。
        /// 用于关闭阴影时自动决定描边颜色：亮底用黑边、暗底用白边。
        /// </summary>
        private static bool IsBrightRegion(Bitmap bmp, RectangleF rect)
        {
            try
            {
                int x0 = Math.Max(0, (int)rect.X);
                int y0 = Math.Max(0, (int)rect.Y);
                int x1 = Math.Min(bmp.Width - 1, (int)(rect.Right));
                int y1 = Math.Min(bmp.Height - 1, (int)(rect.Bottom));
                if (x1 <= x0 || y1 <= y0) { return true; }
                int stepX = Math.Max(1, (x1 - x0) / 40);
                int stepY = Math.Max(1, (y1 - y0) / 20);
                double sum = 0;
                int n = 0;
                for (int y = y0; y <= y1; y += stepY)
                {
                    for (int x = x0; x <= x1; x += stepX)
                    {
                        Color c = bmp.GetPixel(x, y);
                        sum += 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                        n++;
                    }
                }
                return n > 0 && (sum / n) > 128;
            }
            catch { return true; }
        }

        /// <summary>构造字体；字体族不存在时回退微软雅黑，再不行回退系统默认无衬线。</summary>
        private static Font MakeFont(string family, float size, FontStyle style)        {
            try
            {
                if (!string.IsNullOrEmpty(family))
                {
                    using (FontFamily ff = new FontFamily(family))
                    {
                        if (ff.IsStyleAvailable(style)) { return new Font(ff, size, style); }
                    }
                }
            }
            catch { }
            try { return new Font("Microsoft YaHei UI", size, style); }
            catch { return new Font(FontFamily.GenericSansSerif, size, style); }
        }

        /// <summary>解析 "#RRGGBB" 颜色；alpha 为固定透明度（阴影 160 / 正文 255）。失败回退白/黑。</summary>
        private static Color FromHex(string hex, int alpha)
        {
            try
            {
                if (!string.IsNullOrEmpty(hex))
                {
                    return Color.FromArgb(alpha, ColorTranslator.FromHtml(hex));
                }
            }
            catch { }
            return Color.FromArgb(alpha, Color.White);
        }

        private static ImageCodecInfo GetJpegCodec()
        {
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
            {
                if (c.MimeType == "image/jpeg") { return c; }
            }
            return null;
        }
    }
}
