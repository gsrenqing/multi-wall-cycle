using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MultiWallCycle
{
    /// <summary>
    /// v2.4：JPEG 文字描述元数据的读写（自己走字节流，不经过 GDI+ 重编码）。
    ///
    /// 为什么不用 System.Drawing：给图片"补"一个原本不存在的 EXIF 标签，
    /// GDI+ 只能整幅重编码再 Save，4K 壁纸会二次压缩掉画质；而且它只能改已存在的标签。
    /// 这里改成"在 JPEG 的段结构里插/换一段 APP1"，字节级操作，画质零损失。
    ///
    /// 写入规则照抄用户现存的《动态壁纸》导出文件（实测 ground truth，见 v2.4 变更说明）：
    ///   0x010E ImageDescription  ASCII(实为 UTF-8)  正文，如"波尔图大教堂, 葡萄牙"
    ///   0x013B Artist            ASCII(实为 UTF-8)  摄影师
    ///   0x8298 Copyright         ASCII(实为 UTF-8)  摄影师
    ///   0x9C9B XPTitle           BYTE(UTF-16LE)     正文  ← Windows 资源管理器"标题"读它
    ///   0x9C9C XPComment         BYTE(UTF-16LE)     必应检索链接
    ///   0x9C9D XPAuthor          BYTE(UTF-16LE)     摄影师
    ///   0x9C9F XPSubject         BYTE(UTF-16LE)     短标题（必应 title 字段）
    /// </summary>
    public static class ImageMeta
    {
        private const int TagImageDescription = 0x010E;
        private const int TagArtist = 0x013B;
        private const int TagCopyright = 0x8298;
        private const int TagXPTitle = 0x9C9B;
        private const int TagXPComment = 0x9C9C;
        private const int TagXPAuthor = 0x9C9D;
        private const int TagXPSubject = 0x9C9F;

        // ======================= 读 =======================

        /// <summary>读文件内嵌的文字标题：EXIF(0x010E→0x9C9B) 优先，其次 XMP dc:title/description。失败返回空串。</summary>
        public static string ReadTitle(string path)
        {
            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) { return string.Empty; }

                string exifDesc = string.Empty;
                string xmp = null;

                int pos = 2;
                while (pos + 4 <= data.Length)
                {
                    if (data[pos] != 0xFF) { break; }
                    byte marker = data[pos + 1];
                    if (marker == 0xDA || marker == 0xD9) { break; }        // SOS 之后是图像数据
                    if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                    {
                        pos += 2;
                        continue;
                    }
                    int segLen = ReadU16BE(data, pos + 2);
                    if (segLen < 2) { break; }
                    int payload = pos + 4;
                    int payloadLen = segLen - 2;
                    if (payload + payloadLen > data.Length) { break; }

                    if (marker == 0xE1 && payloadLen > 6)
                    {
                        // "Exif\0\0" + TIFF
                        if (data[payload] == 'E' && data[payload + 1] == 'x' && data[payload + 2] == 'i'
                            && data[payload + 3] == 'f' && data[payload + 4] == 0 && data[payload + 5] == 0)
                        {
                            string d = ParseExifDesc(data, payload + 6, payloadLen - 6);
                            if (d.Length > 0) { exifDesc = d; }
                        }
                        // 标准 XMP 包
                        else if (StartsWithAscii(data, payload, "http://ns.adobe.com/xap/1.0/"))
                        {
                            int xmlStart = payload;
                            while (xmlStart < payload + payloadLen && data[xmlStart] != 0) { xmlStart++; }
                            xmlStart++;
                            if (xmlStart < payload + payloadLen)
                            {
                                xmp = Encoding.UTF8.GetString(data, xmlStart, payload + payloadLen - xmlStart);
                            }
                        }
                    }
                    pos += 2 + segLen;
                }

                if (exifDesc.Length > 0) { return exifDesc; }
                if (!string.IsNullOrEmpty(xmp)) { return ParseXmpTitle(xmp); }
                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>解析 TIFF/IFD0 里的描述：0x010E 按 UTF-8（严格→宽松）解，0x9C9B 按 UTF-16LE 解。</summary>
        private static string ParseExifDesc(byte[] data, int tiffStart, int tiffLen)
        {
            try
            {
                if (tiffLen < 8) { return string.Empty; }
                bool little;
                if (data[tiffStart] == 'I' && data[tiffStart + 1] == 'I') { little = true; }
                else if (data[tiffStart] == 'M' && data[tiffStart + 1] == 'M') { little = false; }
                else { return string.Empty; }

                int ifdRel = (int)ReadU32(data, tiffStart + 4, little);
                int ifd = tiffStart + ifdRel;
                if (ifd + 2 > tiffStart + tiffLen) { return string.Empty; }
                int count = ReadU16(data, ifd, little);
                int entry = ifd + 2;
                string desc010E = string.Empty;
                string descXp = string.Empty;

                for (int i = 0; i < count; i++)
                {
                    int e = entry + i * 12;
                    if (e + 12 > tiffStart + tiffLen) { break; }
                    int tag = ReadU16(data, e, little);
                    int type = ReadU16(data, e + 2, little);
                    int n = (int)ReadU32(data, e + 4, little);
                    if (n < 0) { continue; }

                    if (tag != TagImageDescription && tag != TagXPTitle) { continue; }

                    int size = TypeSize(type) * n;
                    if (size <= 0 || size > 8192) { continue; }
                    int valuePos = (size <= 4) ? (e + 8) : (tiffStart + (int)ReadU32(data, e + 8, little));
                    if (valuePos < 0 || valuePos + size > data.Length) { continue; }

                    if (tag == TagImageDescription)
                    {
                        desc010E = DecodeUtf8Lenient(data, valuePos, size);
                    }
                    else
                    {
                        descXp = DecodeUtf16Bytes(data, valuePos, size);
                    }
                }
                return desc010E.Length > 0 ? desc010E : descXp;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>从 XMP 包里抽 dc:title / dc:description 的第一个 rdf:li 文本。</summary>
        private static string ParseXmpTitle(string xml)
        {
            string[] blocks = new[] { "dc:title", "dc:description" };
            foreach (string block in blocks)
            {
                int b = xml.IndexOf("<" + block, StringComparison.OrdinalIgnoreCase);
                if (b < 0) { continue; }
                int li = xml.IndexOf("rdf:li", b, StringComparison.OrdinalIgnoreCase);
                if (li < 0) { continue; }
                int gt = xml.IndexOf('>', li);
                int end = xml.IndexOf("</rdf:li>", gt, StringComparison.OrdinalIgnoreCase);
                if (gt < 0 || end < 0) { continue; }
                string text = xml.Substring(gt + 1, end - gt - 1);
                text = UnescapeXml(text).Trim();
                if (text.Length > 0) { return text; }
            }
            return string.Empty;
        }

        // ======================= 写 =======================

        /// <summary>
        /// 把标题写进 JPEG 的 EXIF（插一段 APP1，不重编码、不损画质）。
        /// 已有 Exif 段会被替换掉，重复调用结果一致。失败返回 false 并给出原因（调用方只记日志，不影响换图）。
        /// </summary>
        public static bool TryWriteTitle(string jpegPath, string desc, string artist, string subject,
            string comment, out string error)
        {
            error = string.Empty;
            try
            {
                if (string.IsNullOrEmpty(desc) && string.IsNullOrEmpty(artist))
                {
                    error = "标题与摄影师都为空，跳过写入";
                    return false;
                }
                byte[] data = File.ReadAllBytes(jpegPath);
                if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
                {
                    error = "不是 JPEG（缺 SOI）";
                    return false;
                }

                byte[] app1 = BuildExifApp1(desc, artist, subject, comment);
                if (app1 == null)
                {
                    error = "EXIF 段构造失败（可能内容过长）";
                    return false;
                }

                // 逐段遍历：记下 APP0（若有，要留在 APP1 之前），并剔除已有的 Exif APP1
                List<byte[]> keep = new List<byte[]>();
                int sosStart = -1;
                int pos = 2;
                while (pos + 4 <= data.Length)
                {
                    if (data[pos] != 0xFF) { break; }
                    byte marker = data[pos + 1];
                    if (marker == 0xDA)
                    {
                        sosStart = pos;
                        break;
                    }
                    if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                    {
                        pos += 2;
                        continue;
                    }
                    int segLen = ReadU16BE(data, pos + 2);
                    if (segLen < 2) { break; }
                    int total = 2 + segLen;
                    if (pos + total > data.Length) { break; }

                    bool isExif = marker == 0xE1 && segLen > 8
                        && data[pos + 4] == 'E' && data[pos + 5] == 'x' && data[pos + 6] == 'i'
                        && data[pos + 7] == 'f' && data[pos + 8] == 0 && data[pos + 9] == 0;
                    if (!isExif)
                    {
                        byte[] seg = new byte[total];
                        Buffer.BlockCopy(data, pos, seg, 0, total);
                        keep.Add(seg);
                    }
                    pos += total;
                }
                if (sosStart < 0) { sosStart = data.Length; }

                using (MemoryStream ms = new MemoryStream(data.Length + app1.Length))
                {
                    ms.WriteByte(0xFF); ms.WriteByte(0xD8);                 // SOI
                    int insertAt = (keep.Count > 0 && keep[0][1] == 0xE0) ? 1 : 0;   // APP0 之后插 APP1
                    for (int i = 0; i < keep.Count; i++)
                    {
                        if (i == insertAt) { ms.Write(app1, 0, app1.Length); }
                        ms.Write(keep[i], 0, keep[i].Length);
                    }
                    if (insertAt >= keep.Count) { ms.Write(app1, 0, app1.Length); }
                    ms.Write(data, sosStart, data.Length - sosStart);
                    byte[] result = ms.ToArray();

                    string tmp = jpegPath + ".metatmp";
                    File.WriteAllBytes(tmp, result);
                    // 保留原时间戳：顺序轮换按修改时间排序，写元数据不该改变"这张图有多新"
                    DateTime created = File.GetCreationTime(jpegPath);
                    DateTime written = File.GetLastWriteTime(jpegPath);
                    // v2.4：用 File.Replace 原子替换（同卷 NTFS 上要么换成新文件、要么保持原文件），
                    // 不再"先删原图再改名"——那一步改名失败就会把用户的图弄丢。
                    bool replaced = false;
                    try
                    {
                        File.Replace(tmp, jpegPath, null);
                        replaced = true;
                    }
                    catch
                    {
                        // 非 NTFS / 跨卷等不支持 Replace 的情况：退化成改名（tmp 已在同目录，失败也不至于丢原图）
                        try
                        {
                            string bak = jpegPath + ".metaold";
                            File.Move(jpegPath, bak);
                            try
                            {
                                File.Move(tmp, jpegPath);
                                replaced = true;
                                try { File.Delete(bak); } catch { }
                            }
                            catch
                            {
                                try { File.Move(bak, jpegPath); } catch { }   // 换不成就还原
                                throw;
                            }
                        }
                        catch
                        {
                            try { File.Delete(tmp); } catch { }
                            throw;
                        }
                    }
                    if (!replaced) { return false; }
                    try { File.SetCreationTime(jpegPath, created); } catch { }
                    try { File.SetLastWriteTime(jpegPath, written); } catch { }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>构造 "FFE1 len Exif\0\0 TIFF…" 整段（含段头）。</summary>
        private static byte[] BuildExifApp1(string desc, string artist, string subject, string comment)
        {
            List<int> tags = new List<int>();
            List<byte[]> values = new List<byte[]>();
            List<int> types = new List<int>();
            List<int> counts = new List<int>();

            if (desc != null && desc.Length > 0)
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(desc + "\0");
                byte[] xp = Utf16LeWithNul(desc);
                Add(tags, types, counts, values, TagImageDescription, 2, utf8.Length, utf8);
                Add(tags, types, counts, values, TagXPTitle, 1, xp.Length, xp);
            }
            if (artist != null && artist.Length > 0)
            {
                byte[] a = Encoding.UTF8.GetBytes(artist + "\0");
                byte[] xpa = Utf16LeWithNul(artist);
                Add(tags, types, counts, values, TagArtist, 2, a.Length, a);
                Add(tags, types, counts, values, TagCopyright, 2, a.Length, a);
                Add(tags, types, counts, values, TagXPAuthor, 1, xpa.Length, xpa);
            }
            if (subject != null && subject.Length > 0)
            {
                byte[] s = Utf16LeWithNul(subject);
                Add(tags, types, counts, values, TagXPSubject, 1, s.Length, s);
            }
            if (comment != null && comment.Length > 0)
            {
                byte[] c = Utf16LeWithNul(comment);
                Add(tags, types, counts, values, TagXPComment, 1, c.Length, c);
            }
            if (tags.Count == 0) { return null; }

            // EXIF 要求 IFD 内条目按 tag 升序
            for (int i = 1; i < tags.Count; i++)
            {
                int j = i;
                while (j > 0 && tags[j - 1] > tags[j])
                {
                    Swap(tags, j - 1, j); Swap(types, j - 1, j); Swap(counts, j - 1, j); Swap(values, j - 1, j);
                    j--;
                }
            }

            int n = tags.Count;
            int ifdSize = 2 + n * 12 + 4;
            int dataStart = 8 + ifdSize;      // TIFF 头 8 字节
            int dataLen = 0;
            for (int i = 0; i < n; i++) { if (values[i].Length > 4) { dataLen += values[i].Length; } }

            int tiffLen = dataStart + dataLen;
            int app1Payload = 6 + tiffLen;    // "Exif\0\0" + TIFF
            int segLen = app1Payload + 2;     // 段长度字段含自身 2 字节
            if (segLen > 0xFFFF) { return null; }

            byte[] seg = new byte[2 + segLen];
            seg[0] = 0xFF; seg[1] = 0xE1;
            seg[2] = (byte)(segLen >> 8); seg[3] = (byte)(segLen & 0xFF);
            seg[4] = (byte)'E'; seg[5] = (byte)'x'; seg[6] = (byte)'i'; seg[7] = (byte)'f';
            seg[8] = 0; seg[9] = 0;

            int t = 10;                             // TIFF 起点（小端）
            seg[t] = (byte)'I'; seg[t + 1] = (byte)'I';
            seg[t + 2] = 0x2A; seg[t + 3] = 0x00;
            WriteU32(seg, t + 4, 8);

            WriteU16(seg, t + 8, n);
            int entryBase = t + 10;
            int valueOffset = dataStart;
            for (int i = 0; i < n; i++)
            {
                int e = entryBase + i * 12;
                WriteU16(seg, e, tags[i]);
                WriteU16(seg, e + 2, types[i]);
                WriteU32(seg, e + 4, (uint)counts[i]);
                byte[] v = values[i];
                if (v.Length <= 4)
                {
                    for (int k = 0; k < v.Length; k++) { seg[e + 8 + k] = v[k]; }
                }
                else
                {
                    WriteU32(seg, e + 8, (uint)valueOffset);
                    Buffer.BlockCopy(v, 0, seg, t + valueOffset, v.Length);
                    valueOffset += v.Length;
                }
            }
            WriteU32(seg, entryBase + n * 12, 0);   // 无 IFD1
            return seg;
        }

        private static void Add(List<int> tags, List<int> types, List<int> counts, List<byte[]> values,
            int tag, int type, int count, byte[] value)
        {
            tags.Add(tag); types.Add(type); counts.Add(count); values.Add(value);
        }

        private static byte[] Utf16LeWithNul(string s)
        {
            byte[] raw = Encoding.Unicode.GetBytes(s);
            byte[] withNul = new byte[raw.Length + 2];
            Buffer.BlockCopy(raw, 0, withNul, 0, raw.Length);
            return withNul;
        }

        // ======================= 小工具 =======================

        private static int TypeSize(int type)
        {
            switch (type)
            {
                case 1: return 1;   // BYTE
                case 2: return 1;   // ASCII
                case 3: return 2;   // SHORT
                case 4: return 4;   // LONG
                case 5: return 8;   // RATIONAL
                case 7: return 1;   // UNDEFINED
                default: return 1;
            }
        }

        private static bool StartsWithAscii(byte[] data, int pos, string literal)
        {
            if (pos + literal.Length > data.Length) { return false; }
            for (int i = 0; i < literal.Length; i++)
            {
                if (data[pos + i] != literal[i]) { return false; }
            }
            return true;
        }

        private static string DecodeUtf8Lenient(byte[] data, int pos, int len)
        {
            int end = pos + len;
            while (end > pos && (data[end - 1] == 0 || data[end - 1] == 0x20)) { end--; }
            if (end <= pos) { return string.Empty; }
            try
            {
                return new UTF8Encoding(false, true).GetString(data, pos, end - pos).Trim();
            }
            catch
            {
                try { return Encoding.Default.GetString(data, pos, end - pos).Trim(); }
                catch { return string.Empty; }
            }
        }

        private static string DecodeUtf16Bytes(byte[] data, int pos, int len)
        {
            int end = pos + len;
            while (end - 1 > pos && data[end - 1] == 0 && data[end - 2] == 0) { end -= 2; }
            if (end <= pos) { return string.Empty; }
            try { return Encoding.Unicode.GetString(data, pos, end - pos).Trim(); }
            catch { return string.Empty; }
        }

        private static string UnescapeXml(string s)
        {
            if (s.IndexOf('&') < 0) { return s; }
            return s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
                    .Replace("&apos;", "'").Replace("&#39;", "'").Replace("&amp;", "&");
        }

        private static int ReadU16BE(byte[] d, int p) { return (d[p] << 8) | d[p + 1]; }

        private static int ReadU16(byte[] d, int p, bool little)
        {
            return little ? ((d[p + 1] << 8) | d[p]) : ((d[p] << 8) | d[p + 1]);
        }

        private static uint ReadU32(byte[] d, int p, bool little)
        {
            if (little) { return (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24)); }
            return (uint)((d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]);
        }

        private static void WriteU16(byte[] d, int p, int v)
        {
            d[p] = (byte)(v & 0xFF);
            d[p + 1] = (byte)((v >> 8) & 0xFF);
        }

        private static void WriteU32(byte[] d, int p, uint v)
        {
            d[p] = (byte)(v & 0xFF);
            d[p + 1] = (byte)((v >> 8) & 0xFF);
            d[p + 2] = (byte)((v >> 16) & 0xFF);
            d[p + 3] = (byte)((v >> 24) & 0xFF);
        }

        private static void Swap<T>(List<T> list, int a, int b)
        {
            T tmp = list[a]; list[a] = list[b]; list[b] = tmp;
        }
    }
}
