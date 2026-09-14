// Czech translation for Prototype - handling of the game's file formats
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// ---------------------------------------------------------------- TSV
static class Tsv
{
    public static IEnumerable<Tuple<string, string, string>> Read(byte[] content)
    {
        using (var sr = new StreamReader(new MemoryStream(content), new UTF8Encoding(false)))
        {
            string line;
            while ((line = sr.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                int a = line.IndexOf('\t'); if (a < 0) continue;
                int b = line.IndexOf('\t', a + 1); if (b < 0) continue;
                yield return Tuple.Create(
                    Unescape(line.Substring(0, a)),
                    Unescape(line.Substring(a + 1, b - a - 1)),
                    Unescape(line.Substring(b + 1)));
            }
        }
    }

    static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char n = s[++i];
                if (n == 'n') sb.Append('\n');
                else if (n == 't') sb.Append('\t');
                else if (n == 'r') sb.Append('\r');
                else sb.Append(n);
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }
}

// ---------------------------------------------------------------- delta
static class Delta
{
    static readonly byte[] MAGIC = Encoding.ASCII.GetBytes("CZDELTA1");

    // does the file match exactly the version the delta was computed against?
    public static bool MatchesSource(byte[] src, byte[] delta)
    {
        return src.Length == (int)U32(delta, 8) && Crc32(src, src.Length) == U32(delta, 12);
    }

    public static byte[] Apply(byte[] src, byte[] delta)
    {
        for (int i = 0; i < 8; i++) if (delta[i] != MAGIC[i]) throw new Exception("poškozený soubor s rozdílem");
        uint srcLen = U32(delta, 8), srcCrc = U32(delta, 12), dstLen = U32(delta, 16), dstCrc = U32(delta, 20), opCount = U32(delta, 24);
        uint crcSrc = Crc32(src, src.Length);
        if (src.Length == (int)dstLen && crcSrc == dstCrc) return null;   // already patched
        if (src.Length != (int)srcLen || crcSrc != srcCrc)
            throw new Exception("soubor hry neodpovídá očekávané verzi");
        var result = new byte[dstLen];
        int p = 28, w = 0;
        for (uint k = 0; k < opCount; k++)
        {
            byte op = delta[p++];
            if (op == 0)
            {
                int off = (int)U32(delta, p); p += 4;
                int n = (int)U32(delta, p); p += 4;
                Buffer.BlockCopy(src, off, result, w, n); w += n;
            }
            else
            {
                int n = (int)U32(delta, p); p += 4;
                Buffer.BlockCopy(delta, p, result, w, n); p += n; w += n;
            }
        }
        if (w != (int)dstLen || Crc32(result, w) != dstCrc) throw new Exception("výsledek nesedí");
        return result;
    }

    static uint U32(byte[] b, int o)
    {
        return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    }

    static uint[] tab;
    public static uint Crc32(byte[] data, int len)
    {
        if (tab == null)
        {
            tab = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int j = 0; j < 8; j++) c = ((c & 1) != 0) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                tab[i] = c;
            }
        }
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < len; i++) crc = tab[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}

// ---------------------------------------------------------------- P3D
static class P3d
{
    const uint MAGIC = 0xff443350;
    const uint TEXTBIBLE = 0x00018202;

    static uint U32(byte[] b, int o)
    {
        return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    }
    static void W32(List<byte> o, uint v)
    {
        o.Add((byte)v); o.Add((byte)(v >> 8)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 24));
    }
    static void W32(byte[] b, int o, uint v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
    }

    class Bible
    {
        public uint Id, Unk, Unk2, ChildId;
        public byte[] RawName, RawKeys, RawSname;
        public List<string> Keys = new List<string>();
        public List<byte[]> Values = new List<byte[]>();
        public string Language;
    }

    static Bible ReadBible(byte[] d, int off)
    {
        var b = new Bible();
        b.Id = U32(d, off);
        int p = off + 12;
        int p0 = p;
        int nl = d[p]; p += 1 + nl;
        b.RawName = Sub(d, p0, p - p0);
        b.Language = Encoding.ASCII.GetString(d, p0 + 1, nl).TrimEnd('\0');
        b.Unk = U32(d, p); p += 4;
        int n = (int)U32(d, p); p += 4;
        int pk = p;
        for (int i = 0; i < n; i++)
        {
            int l = d[p];
            string k = Encoding.ASCII.GetString(d, p + 1, l).TrimEnd('\0');
            b.Keys.Add(k);
            p += 1 + l;
        }
        b.RawKeys = Sub(d, pk, p - pk);
        var A = new int[n];
        for (int i = 0; i < n; i++) { A[i] = (int)U32(d, p); p += 4; }
        p += 4 * n;                       // table B is recomputed
        b.ChildId = U32(d, p);
        int q = p + 12;
        int q0 = q;
        int sl = d[q]; q += 1 + sl;
        b.RawSname = Sub(d, q0, q - q0);
        b.Unk2 = U32(d, q); q += 4;
        int blobLen = (int)U32(d, q); q += 4;
        for (int i = 0; i < n; i++)
        {
            int s = A[i];
            int e = (i + 1 < n) ? A[i + 1] : blobLen;
            b.Values.Add(Sub(d, q + s, e - s));
        }
        return b;
    }

    static byte[] WriteBible(Bible b)
    {
        int n = b.Keys.Count;
        var blob = new List<byte>();
        var A = new int[n]; var B = new int[n];
        int o = 0;
        for (int i = 0; i < n; i++) { A[i] = o; blob.AddRange(b.Values[i]); o += b.Values[i].Length; }
        for (int i = 0; i < n; i++) B[i] = (i + 1 < n) ? A[i + 1] + 1 : blob.Count + 1;

        var childBody = new List<byte>();
        childBody.AddRange(b.RawSname); W32(childBody, b.Unk2); W32(childBody, (uint)blob.Count);
        int childTs = 12 + childBody.Count + blob.Count;
        var child = new List<byte>();
        W32(child, b.ChildId); W32(child, (uint)childTs); W32(child, (uint)childTs);
        child.AddRange(childBody); child.AddRange(blob);

        var body = new List<byte>();
        body.AddRange(b.RawName); W32(body, b.Unk); W32(body, (uint)n); body.AddRange(b.RawKeys);
        for (int i = 0; i < n; i++) W32(body, (uint)A[i]);
        for (int i = 0; i < n; i++) W32(body, (uint)B[i]);

        int ds = 12 + body.Count;
        var result = new List<byte>();
        W32(result, b.Id); W32(result, (uint)ds); W32(result, (uint)(ds + child.Count));
        result.AddRange(body); result.AddRange(child);
        return result.ToArray();
    }

    public static byte[] RewriteTextBible(byte[] src, Dictionary<string, string> map)
    {
        if (src.Length < 12 || U32(src, 0) != MAGIC) throw new Exception("není to soubor P3D");

        // self-test: take apart and reassemble unchanged, must come out byte-identical
        byte[] check = Assemble(src, null);
        if (!BytesEqual(check, src)) throw new Exception("autotest formátu neprošel");

        return Assemble(src, map);
    }

    static byte[] Assemble(byte[] src, Dictionary<string, string> map)
    {
        var body = new List<byte>();
        int off = 12;
        while (off < src.Length)
        {
            uint cid = U32(src, off);
            int cts = (int)U32(src, off + 8);
            if (cts < 12 || off + cts > src.Length) throw new Exception("poškozená struktura chunků");
            if (cid == TEXTBIBLE)
            {
                var b = ReadBible(src, off);
                if (map != null && b.Language == "english")
                {
                    for (int i = 0; i < b.Keys.Count; i++)
                    {
                        string newValue;
                        if (map.TryGetValue(b.Keys[i], out newValue))
                            b.Values[i] = new UTF8Encoding(false).GetBytes(newValue);
                    }
                }
                body.AddRange(WriteBible(b));
            }
            else body.AddRange(Sub(src, off, cts));
            off += cts;
        }
        var result = new List<byte>();
        W32(result, MAGIC); W32(result, 12); W32(result, (uint)(12 + body.Count));
        result.AddRange(body);
        return result.ToArray();
    }

    // ------------------------------------------------------------ audio subtitles
    static readonly byte[] MARKER = Encoding.ASCII.GetBytes("AudioDialogueSubtitle");
    static readonly byte[] END_MARKER = Encoding.ASCII.GetBytes("AudioFile\0");

    public static byte[] RewriteSubtitle(byte[] d, string newText)
    {
        int i = Find(d, MARKER, 0);
        if (i < 0) throw new Exception("chybí titulkový blok");
        int p = i + MARKER.Length + 1;
        p += 4;
        int nl = (int)U32(d, p); p += 4; p += nl + 1;
        int end = Find(d, END_MARKER, i);
        if (end < 0) end = d.Length;

        int q = -1, tl = 0;
        while (p < end - 12)
        {
            int j = Array.IndexOf(d, (byte)0x01, p);
            if (j < 0 || j >= end) break;
            if (j + 5 >= d.Length) break;
            int ll = (int)U32(d, j + 1);
            if (ll < 2 || ll > 12 || j + 5 + ll >= d.Length) { p = j + 1; continue; }
            bool isLetters = true;
            for (int k = 0; k < ll; k++)
            {
                byte c = d[j + 5 + k];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))) { isLetters = false; break; }
            }
            if (!isLetters || d[j + 5 + ll] != 0) { p = j + 1; continue; }
            string lang = Encoding.ASCII.GetString(d, j + 5, ll);
            int qq = j + 5 + ll + 1;
            int t = (int)U32(d, qq);
            if (t > 5000) { p = j + 1; continue; }
            if (lang == "english") { q = qq; tl = t; break; }
            p = qq + 4 + t + 1;
        }
        if (q < 0) throw new Exception("nenašel jsem anglický titulek");

        byte[] newBytes = new UTF8Encoding(false).GetBytes(newText);
        int oldLen = 4 + tl + 1, newLen = 4 + newBytes.Length + 1;
        int diff = newLen - oldLen;

        var result = new byte[d.Length + diff];
        Buffer.BlockCopy(d, 0, result, 0, q);
        W32(result, q, (uint)newBytes.Length);
        Buffer.BlockCopy(newBytes, 0, result, q + 4, newBytes.Length);
        result[q + 4 + newBytes.Length] = 0;
        Buffer.BlockCopy(d, q + oldLen, result, q + newLen, d.Length - q - oldLen);

        if (diff != 0)
        {
            W32(result, 8, (uint)(U32(result, 8) + diff));
            int off = 12;
            while (off < result.Length - 12)
            {
                uint ds = U32(result, off + 4);
                int cts = (int)U32(result, off + 8);
                if (cts < 12) break;
                if (off + 12 <= q && q < off + cts)
                {
                    W32(result, off + 4, (uint)(ds + diff));
                    W32(result, off + 8, (uint)(cts + diff));
                    break;
                }
                off += cts;
            }
        }
        return result;
    }

    // ------------------------------------------------------------ small helpers
    static byte[] Sub(byte[] d, int off, int len)
    {
        var r = new byte[len]; Buffer.BlockCopy(d, off, r, 0, len); return r;
    }
    static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
    static int Find(byte[] hay, byte[] needle, int from)
    {
        int max = hay.Length - needle.Length;
        for (int i = from; i <= max; i++)
        {
            int k = 0;
            while (k < needle.Length && hay[i + k] == needle[k]) k++;
            if (k == needle.Length) return i;
        }
        return -1;
    }
}
