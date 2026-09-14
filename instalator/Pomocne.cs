// Cestina do Prototype - prace s formaty hry
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// ---------------------------------------------------------------- TSV
static class Tsv
{
    public static IEnumerable<Tuple<string, string, string>> Cti(byte[] obsah)
    {
        using (var sr = new StreamReader(new MemoryStream(obsah), new UTF8Encoding(false)))
        {
            string radek;
            while ((radek = sr.ReadLine()) != null)
            {
                if (radek.Length == 0) continue;
                int a = radek.IndexOf('\t'); if (a < 0) continue;
                int b = radek.IndexOf('\t', a + 1); if (b < 0) continue;
                yield return Tuple.Create(
                    Odescapuj(radek.Substring(0, a)),
                    Odescapuj(radek.Substring(a + 1, b - a - 1)),
                    Odescapuj(radek.Substring(b + 1)));
            }
        }
    }

    static string Odescapuj(string s)
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

    // sedi soubor presne na verzi, pro kterou je rozdil spocitany?
    public static bool JeZdroj(byte[] src, byte[] d)
    {
        return src.Length == (int)U32(d, 8) && Crc32(src, src.Length) == U32(d, 12);
    }

    public static byte[] Pouzij(byte[] src, byte[] d)
    {
        for (int i = 0; i < 8; i++) if (d[i] != MAGIC[i]) throw new Exception("poškozený soubor s rozdílem");
        uint sl = U32(d, 8), sc = U32(d, 12), dl = U32(d, 16), dc = U32(d, 20), nops = U32(d, 24);
        uint crcSrc = Crc32(src, src.Length);
        if (src.Length == (int)dl && crcSrc == dc) return null;   // uz je zaplatovano
        if (src.Length != (int)sl || crcSrc != sc)
            throw new Exception("soubor hry neodpovídá očekávané verzi");
        var outp = new byte[dl];
        int p = 28, w = 0;
        for (uint k = 0; k < nops; k++)
        {
            byte op = d[p++];
            if (op == 0)
            {
                int off = (int)U32(d, p); p += 4;
                int n = (int)U32(d, p); p += 4;
                Buffer.BlockCopy(src, off, outp, w, n); w += n;
            }
            else
            {
                int n = (int)U32(d, p); p += 4;
                Buffer.BlockCopy(d, p, outp, w, n); p += n; w += n;
            }
        }
        if (w != (int)dl || Crc32(outp, w) != dc) throw new Exception("výsledek nesedí");
        return outp;
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
        public List<string> Klice = new List<string>();
        public List<byte[]> Hodnoty = new List<byte[]>();
        public string Jazyk;
    }

    static Bible CtiBibli(byte[] d, int off)
    {
        var b = new Bible();
        b.Id = U32(d, off);
        int p = off + 12;
        int p0 = p;
        int nl = d[p]; p += 1 + nl;
        b.RawName = Sub(d, p0, p - p0);
        b.Jazyk = Encoding.ASCII.GetString(d, p0 + 1, nl).TrimEnd('\0');
        b.Unk = U32(d, p); p += 4;
        int n = (int)U32(d, p); p += 4;
        int pk = p;
        for (int i = 0; i < n; i++)
        {
            int l = d[p];
            string k = Encoding.ASCII.GetString(d, p + 1, l).TrimEnd('\0');
            b.Klice.Add(k);
            p += 1 + l;
        }
        b.RawKeys = Sub(d, pk, p - pk);
        var A = new int[n];
        for (int i = 0; i < n; i++) { A[i] = (int)U32(d, p); p += 4; }
        p += 4 * n;                       // tabulka B se dopocita
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
            b.Hodnoty.Add(Sub(d, q + s, e - s));
        }
        return b;
    }

    static byte[] PisBibli(Bible b)
    {
        int n = b.Klice.Count;
        var blob = new List<byte>();
        var A = new int[n]; var B = new int[n];
        int o = 0;
        for (int i = 0; i < n; i++) { A[i] = o; blob.AddRange(b.Hodnoty[i]); o += b.Hodnoty[i].Length; }
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
        var outp = new List<byte>();
        W32(outp, b.Id); W32(outp, (uint)ds); W32(outp, (uint)(ds + child.Count));
        outp.AddRange(body); outp.AddRange(child);
        return outp.ToArray();
    }

    public static byte[] PrepisTextbible(byte[] src, Dictionary<string, string> mapa)
    {
        if (src.Length < 12 || U32(src, 0) != MAGIC) throw new Exception("není to soubor P3D");

        // autotest: rozeber a sloz beze zmeny, musi vyjit bajtove stejne
        byte[] kontrola = Slep(src, null);
        if (!Stejne(kontrola, src)) throw new Exception("autotest formátu neprošel");

        return Slep(src, mapa);
    }

    static byte[] Slep(byte[] src, Dictionary<string, string> mapa)
    {
        var telo = new List<byte>();
        int off = 12;
        while (off < src.Length)
        {
            uint cid = U32(src, off);
            int cts = (int)U32(src, off + 8);
            if (cts < 12 || off + cts > src.Length) throw new Exception("poškozená struktura chunků");
            if (cid == TEXTBIBLE)
            {
                var b = CtiBibli(src, off);
                if (mapa != null && b.Jazyk == "english")
                {
                    for (int i = 0; i < b.Klice.Count; i++)
                    {
                        string novy;
                        if (mapa.TryGetValue(b.Klice[i], out novy))
                            b.Hodnoty[i] = new UTF8Encoding(false).GetBytes(novy);
                    }
                }
                telo.AddRange(PisBibli(b));
            }
            else telo.AddRange(Sub(src, off, cts));
            off += cts;
        }
        var outp = new List<byte>();
        W32(outp, MAGIC); W32(outp, 12); W32(outp, (uint)(12 + telo.Count));
        outp.AddRange(telo);
        return outp.ToArray();
    }

    // ------------------------------------------------------------ titulky u zvuku
    static readonly byte[] ZNACKA = Encoding.ASCII.GetBytes("AudioDialogueSubtitle");
    static readonly byte[] KONEC = Encoding.ASCII.GetBytes("AudioFile\0");

    public static byte[] PrepisTitulek(byte[] d, string novyText)
    {
        int i = Najdi(d, ZNACKA, 0);
        if (i < 0) throw new Exception("chybí titulkový blok");
        int p = i + ZNACKA.Length + 1;
        p += 4;
        int nl = (int)U32(d, p); p += 4; p += nl + 1;
        int konec = Najdi(d, KONEC, i);
        if (konec < 0) konec = d.Length;

        int q = -1, tl = 0;
        while (p < konec - 12)
        {
            int j = Array.IndexOf(d, (byte)0x01, p);
            if (j < 0 || j >= konec) break;
            if (j + 5 >= d.Length) break;
            int ll = (int)U32(d, j + 1);
            if (ll < 2 || ll > 12 || j + 5 + ll >= d.Length) { p = j + 1; continue; }
            bool pismena = true;
            for (int k = 0; k < ll; k++)
            {
                byte c = d[j + 5 + k];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))) { pismena = false; break; }
            }
            if (!pismena || d[j + 5 + ll] != 0) { p = j + 1; continue; }
            string jazyk = Encoding.ASCII.GetString(d, j + 5, ll);
            int qq = j + 5 + ll + 1;
            int t = (int)U32(d, qq);
            if (t > 5000) { p = j + 1; continue; }
            if (jazyk == "english") { q = qq; tl = t; break; }
            p = qq + 4 + t + 1;
        }
        if (q < 0) throw new Exception("nenašel jsem anglický titulek");

        byte[] nove = new UTF8Encoding(false).GetBytes(novyText);
        int stary = 4 + tl + 1, novy = 4 + nove.Length + 1;
        int rozdil = novy - stary;

        var outp = new byte[d.Length + rozdil];
        Buffer.BlockCopy(d, 0, outp, 0, q);
        W32(outp, q, (uint)nove.Length);
        Buffer.BlockCopy(nove, 0, outp, q + 4, nove.Length);
        outp[q + 4 + nove.Length] = 0;
        Buffer.BlockCopy(d, q + stary, outp, q + novy, d.Length - q - stary);

        if (rozdil != 0)
        {
            W32(outp, 8, (uint)(U32(outp, 8) + rozdil));
            int off = 12;
            while (off < outp.Length - 12)
            {
                uint ds = U32(outp, off + 4);
                int cts = (int)U32(outp, off + 8);
                if (cts < 12) break;
                if (off + 12 <= q && q < off + cts)
                {
                    W32(outp, off + 4, (uint)(ds + rozdil));
                    W32(outp, off + 8, (uint)(cts + rozdil));
                    break;
                }
                off += cts;
            }
        }
        return outp;
    }

    // ------------------------------------------------------------ drobnosti
    static byte[] Sub(byte[] d, int off, int len)
    {
        var r = new byte[len]; Buffer.BlockCopy(d, off, r, 0, len); return r;
    }
    static bool Stejne(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
    static int Najdi(byte[] hay, byte[] needle, int from)
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
