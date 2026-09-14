// Cestina do Prototype - doplneni ceskych znaku do fontu Scaleform GFX (DefineFont3)
// Port pythonoveho cz_fonty.py. Hacky se nekresli, odvozuji se z existujicich glyfu:
// carka = obrys navic v "a" oproti "a", hacek = zrcadlena carka + puvodni, krouzek
// ze znaku stupne, hacek u d/t z apostrofu. Podrobnosti viz TECHNIKA.md, kapitola 5.
using System;
using System.Collections.Generic;
using System.Text;

// ---------------------------------------------------------------- vyjimka
class FontChyba : Exception
{
    public FontChyba(string zprava) : base(zprava) { }
}

// ---------------------------------------------------------------- bitovy tok (SHAPE zaznamy jsou bit-packed)
class BitR
{
    byte[] d; int p; int b;
    public BitR(byte[] d, int p) { this.d = d; this.p = p; this.b = 0; }
    public int P { get { return p; } }
    public int Ub(int n)
    {
        int v = 0;
        for (int i = 0; i < n; i++)
        {
            v = (v << 1) | ((d[p] >> (7 - b)) & 1);
            b++;
            if (b == 8) { b = 0; p++; }
        }
        return v;
    }
    public int Sb(int n)
    {
        if (n == 0) return 0;
        int v = Ub(n);
        if ((v >> (n - 1)) != 0) v -= (1 << n);
        return v;
    }
    public void Align() { if (b != 0) { b = 0; p++; } }
}

class BitW
{
    List<byte> outp = new List<byte>();
    int cur = 0, n = 0;
    public void Ub(int v, int pocet)
    {
        for (int i = pocet - 1; i >= 0; i--)
        {
            cur = (cur << 1) | ((v >> i) & 1); n++;
            if (n == 8) { outp.Add((byte)cur); cur = 0; n = 0; }
        }
    }
    public void Sb(int v, int pocet)
    {
        if (pocet > 0) Ub(v & ((1 << pocet) - 1), pocet);
    }
    public void Align() { if (n != 0) { outp.Add((byte)(cur << (8 - n))); cur = 0; n = 0; } }
    public byte[] Bytes() { Align(); return outp.ToArray(); }
}

// ---------------------------------------------------------------- SHAPE zaznam (styl / cara / krivka / konec)
enum ZKind { Konec, Styl, Cara, Krivka }

class SwfZaznam
{
    public ZKind Kind;
    // styl
    public bool MaPosun; public int PosunX, PosunY;   // MoveTo je ABSOLUTNI, ne relativni
    public bool MaFs0; public int Fs0;
    public bool MaFs1; public int Fs1;
    public bool MaLs; public int Ls;
    // cara: Dx,Dy = delta. krivka: Dx,Dy = delta ridiciho bodu, Dx2,Dy2 = delta kotvy od ridiciho bodu
    public int Dx, Dy, Dx2, Dy2;
}

// ---------------------------------------------------------------- glyf a font
class Glyf
{
    public List<SwfZaznam> Zaznamy;
    public int Nfb, Nlb;   // pocet bitu pro fillstyle / linestyle indexy v tomto glyfu
}

class Pismo
{
    public int Fid; public byte Fl, Lang; public byte[] Jmeno;
    public bool Wide, Layout;
    public List<Glyf> Glyfy;
    public List<int> Kody;
    public short Asc, Desc, Lead;
    public List<short> Sirky;
    public List<int[]> Hranice;   // [xmin,xmax,ymin,ymax] - poradi RECT
    public byte[] Kern;           // kerning je klicovan kody znaku, ne indexy -> necháváme beze zmeny
}

// ---------------------------------------------------------------- obrys (geometrie glyfu, absolutni souradnice)
class Seg
{
    public bool Krivka;
    public double Cx, Cy;   // ridici bod (jen krivka)
    public double Ex, Ey;   // koncovy bod (cara), nebo kotva (krivka)
}

class Kontura
{
    public double Sx, Sy;
    public List<Seg> Segy = new List<Seg>();
}

// ---------------------------------------------------------------- ceske znaky: cil <- zaklad
struct ZnakPar
{
    public char Cil, Zaklad;
    public ZnakPar(char cil, char zaklad) { Cil = cil; Zaklad = zaklad; }
}

// ---------------------------------------------------------------- SWF/GFX tag (hlavicka + telo)
class GfxTag
{
    public int Code, Hdr, Body, Len;
}

// ================================================================== hlavni trida
static class Fonty
{
    static readonly ZnakPar[] CARON = new ZnakPar[] {
        new ZnakPar('č','c'), new ZnakPar('ě','e'), new ZnakPar('ň','n'), new ZnakPar('ř','r'),
        new ZnakPar('š','s'), new ZnakPar('ž','z'),
        new ZnakPar('Č','C'), new ZnakPar('Ď','D'), new ZnakPar('Ě','E'), new ZnakPar('Ň','N'),
        new ZnakPar('Ř','R'), new ZnakPar('Š','S'), new ZnakPar('Ť','T'), new ZnakPar('Ž','Z'),
    };
    static readonly ZnakPar[] APOST = new ZnakPar[] { new ZnakPar('ď','d'), new ZnakPar('ť','t') };
    static readonly ZnakPar[] RING  = new ZnakPar[] { new ZnakPar('ů','u'), new ZnakPar('Ů','U') };
    static readonly ZnakPar[] ACUTE = new ZnakPar[] { new ZnakPar('ý','y'), new ZnakPar('Ý','Y') };

    // -------------------------------------------------------- verejne API
    public static byte[] Prepis(byte[] gfx)
    {
        try { return PrepisImpl(gfx); }
        catch (FontChyba) { throw; }
        catch (Exception) { throw new Exception("soubor se nepodařilo zpracovat jako font GFX (poškozený nebo nepodporovaný formát)"); }
    }

    static byte[] PrepisImpl(byte[] gfx)
    {
        if (gfx == null || gfx.Length < 8 || gfx[0] != (byte)'G' || gfx[1] != (byte)'F' || gfx[2] != (byte)'X')
            throw new FontChyba("soubor není podporovaný font GFX");

        List<GfxTag> tagy = CtiTagy(gfx);
        bool maFont = false;
        foreach (GfxTag t in tagy) if (t.Code == 75) { maFont = true; break; }
        if (!maFont) throw new FontChyba("v souboru nejsou žádné fonty");

        // autotest: kazdy font (DefineFont3) rozeber a bez zmeny slep znovu - musi vyjit
        // bajtove stejne jako puvodni telo tagu. Jinak formatu nerozumime dost dobre na
        // to, abychom do nej bezpecne zapisovali.
        // Pozor: cely kontejner (ostatni SWF tagy) autotestem NEprochazi - original hry
        // ma nektere tagy zapsane nekanonicky (dlouha forma hlavicky i pro kratkou delku).
        // Stejne jako cz_fonty.py pri skladani vzdy pouzijeme kanonickou formu, takze
        // vysledny soubor neni bajtove shodny s originalem mimo upravene fonty - a nema
        // byt, presne tak to dela i originalni skript.
        foreach (GfxTag t in tagy)
        {
            if (t.Code != 75) continue;
            Pismo f0 = CtiPismo(gfx, t.Body, t.Len);
            byte[] znovu = PisPismo(f0);
            byte[] puvodni = Sub(gfx, t.Body, t.Len);
            if (!Stejne(znovu, puvodni)) throw new FontChyba("autotest formátu fontu neprošel");
        }

        byte[] vysledek = Slep(gfx, tagy, true);
        if (Stejne(vysledek, gfx)) return null;   // uz obsahuje ceske znaky
        return vysledek;
    }

    // -------------------------------------------------------- rozbor SWF/GFX tagu
    // hlavicka: "GFX" u8 verze u32 velikost, RECT, u16 fps, u16 pocet snimku, pak tagy
    static List<GfxTag> CtiTagy(byte[] d)
    {
        int nb = d[8] >> 3;                          // horni 5 bitu prvniho bajtu RECT = pocet bitu na slozku
        int rectBajtu = (5 + 4 * nb + 7) / 8;
        int p = 8 + rectBajtu + 4;                    // + fps(u16) + pocet snimku(u16)
        var outp = new List<GfxTag>();
        while (p < d.Length - 1)
        {
            int hp = p;
            int th = U16(d, p); p += 2;
            int code = th >> 6; int ln = th & 0x3f;
            if (ln == 0x3f) { ln = (int)U32(d, p); p += 4; }
            var t = new GfxTag(); t.Code = code; t.Hdr = hp; t.Body = p; t.Len = ln;
            outp.Add(t);
            p += ln;
            if (code == 0) break;                      // End tag
        }
        return outp;
    }

    // slozi cely soubor zpet; zmenit=true doplni ceske znaky do kazdeho DefineFont3 (kod 75)
    static byte[] Slep(byte[] data, List<GfxTag> tagy, bool zmenit)
    {
        var outp = new List<byte>();
        outp.AddRange(Sub(data, 0, tagy[0].Hdr));
        foreach (GfxTag t in tagy)
        {
            byte[] body;
            if (t.Code == 75)
            {
                Pismo f = CtiPismo(data, t.Body, t.Len);
                if (zmenit) PridejCestinu(f);
                body = PisPismo(f);
            }
            else
            {
                body = Sub(data, t.Body, t.Len);
            }
            int ln = body.Length;
            if (ln >= 0x3f) { W16(outp, (ushort)((t.Code << 6) | 0x3f)); W32(outp, (uint)ln); }
            else W16(outp, (ushort)((t.Code << 6) | ln));
            outp.AddRange(body);
        }
        byte[] vysl = outp.ToArray();
        W32At(vysl, 4, (uint)vysl.Length);
        return vysl;
    }

    // -------------------------------------------------------- DefineFont3 (kod 75)
    static Pismo CtiPismo(byte[] d, int body, int len)
    {
        int q = body;
        int fid = U16(d, q); q += 2;
        byte fl = d[q]; q += 1;
        byte lang = d[q]; q += 1;
        int nl = d[q]; q += 1;
        byte[] jmeno = Sub(d, q, nl); q += nl;
        int ng = U16(d, q); q += 2;
        bool wide = (fl & 0x08) != 0;
        bool layout = (fl & 0x80) != 0;
        int sz = wide ? 4 : 2;
        int tbl = q;
        int[] offs = new int[ng];
        for (int i = 0; i < ng; i++) offs[i] = wide ? (int)U32(d, q + i * sz) : U16(d, q + i * sz);
        int codeOff = wide ? (int)U32(d, q + ng * sz) : U16(d, q + ng * sz);

        var glyfy = new List<Glyf>(ng);
        for (int i = 0; i < ng; i++)
        {
            int gp = tbl + offs[i];
            int nfb, nlb, konecP;
            List<SwfZaznam> recs = CtiGlyf(d, gp, out nfb, out nlb, out konecP);
            var g = new Glyf(); g.Zaznamy = recs; g.Nfb = nfb; g.Nlb = nlb;
            glyfy.Add(g);
        }

        int p = tbl + codeOff;
        var kody = new List<int>(ng);
        for (int i = 0; i < ng; i++) { kody.Add(U16(d, p)); p += 2; }

        short asc = 0, desc = 0, lead = 0;
        var sirky = new List<short>();
        var hranice = new List<int[]>();
        byte[] kern = new byte[0];
        if (layout)
        {
            asc = S16(d, p); p += 2; desc = S16(d, p); p += 2; lead = S16(d, p); p += 2;
            for (int i = 0; i < ng; i++) { sirky.Add(S16(d, p)); p += 2; }
            for (int i = 0; i < ng; i++)
            {
                int np;
                int[] v = CtiRect(d, p, out np);
                hranice.Add(v); p = np;
            }
            kern = Sub(d, p, body + len - p);
        }

        var f = new Pismo();
        f.Fid = fid; f.Fl = fl; f.Lang = lang; f.Jmeno = jmeno; f.Wide = wide; f.Layout = layout;
        f.Glyfy = glyfy; f.Kody = kody; f.Asc = asc; f.Desc = desc; f.Lead = lead;
        f.Sirky = sirky; f.Hranice = hranice; f.Kern = kern;
        return f;
    }

    static byte[] PisPismo(Pismo f)
    {
        int n = f.Glyfy.Count;
        byte[][] shapes = new byte[n][];
        for (int i = 0; i < n; i++) shapes[i] = PisGlyf(f.Glyfy[i].Zaznamy, f.Glyfy[i].Nfb, f.Glyfy[i].Nlb);

        bool wide = f.Wide;
        int sz = wide ? 4 : 2;
        int tblLen = (n + 1) * sz;
        int[] offs = new int[n]; int acc = tblLen;
        for (int i = 0; i < n; i++) { offs[i] = acc; acc += shapes[i].Length; }
        int codeOff = acc;
        if (!wide && codeOff > 0xffff)
        {
            wide = true; sz = 4; tblLen = (n + 1) * sz; acc = tblLen;
            for (int i = 0; i < n; i++) { offs[i] = acc; acc += shapes[i].Length; }
            codeOff = acc;
        }
        byte fl = f.Fl;
        if (wide) fl = (byte)(fl | 0x08); else fl = (byte)(fl & ~0x08);

        var outp = new List<byte>();
        W16(outp, (ushort)f.Fid); outp.Add(fl); outp.Add(f.Lang);
        outp.Add((byte)f.Jmeno.Length); outp.AddRange(f.Jmeno);
        W16(outp, (ushort)n);
        for (int i = 0; i < n; i++) { if (wide) W32(outp, (uint)offs[i]); else W16(outp, (ushort)offs[i]); }
        if (wide) W32(outp, (uint)codeOff); else W16(outp, (ushort)codeOff);
        for (int i = 0; i < n; i++) outp.AddRange(shapes[i]);
        for (int i = 0; i < n; i++) W16(outp, (ushort)f.Kody[i]);
        if (f.Layout)
        {
            WS16(outp, f.Asc); WS16(outp, f.Desc); WS16(outp, f.Lead);
            for (int i = 0; i < f.Sirky.Count; i++) WS16(outp, f.Sirky[i]);
            for (int i = 0; i < f.Hranice.Count; i++) outp.AddRange(PisRect(f.Hranice[i]));
            outp.AddRange(f.Kern);
        }
        return outp.ToArray();
    }

    static int[] CtiRect(byte[] d, int p, out int novaP)
    {
        var r = new BitR(d, p);
        int nb = r.Ub(5);
        int[] v = new int[4];
        for (int i = 0; i < 4; i++) v[i] = r.Sb(nb);
        r.Align();
        novaP = r.P;
        return v;
    }

    static byte[] PisRect(int[] v)
    {
        int nb = NbitsS(v);
        var w = new BitW();
        w.Ub(nb, 5);
        for (int i = 0; i < 4; i++) w.Sb(v[i], nb);
        return w.Bytes();
    }

    // -------------------------------------------------------- SHAPE zaznamy glyfu (bit-packed)
    static List<SwfZaznam> CtiGlyf(byte[] d, int pos, out int nfb, out int nlb, out int konecP)
    {
        var r = new BitR(d, pos);
        nfb = r.Ub(4); nlb = r.Ub(4);
        var recs = new List<SwfZaznam>();
        while (true)
        {
            if (r.Ub(1) == 0)
            {
                int flags = r.Ub(5);
                if (flags == 0) { var e = new SwfZaznam(); e.Kind = ZKind.Konec; recs.Add(e); break; }
                var rec = new SwfZaznam(); rec.Kind = ZKind.Styl;
                if ((flags & 0x01) != 0)
                {
                    int nb = r.Ub(5);
                    int mx = r.Sb(nb); int my = r.Sb(nb);
                    rec.MaPosun = true; rec.PosunX = mx; rec.PosunY = my;
                }
                if ((flags & 0x02) != 0) { rec.MaFs0 = true; rec.Fs0 = r.Ub(nfb); }
                if ((flags & 0x04) != 0) { rec.MaFs1 = true; rec.Fs1 = r.Ub(nfb); }
                if ((flags & 0x08) != 0) { rec.MaLs = true; rec.Ls = r.Ub(nlb); }
                if ((flags & 0x10) != 0) throw new Exception("NewStyles v glyfu neni podporovano");
                recs.Add(rec);
            }
            else
            {
                if (r.Ub(1) == 1)
                {
                    int nb = r.Ub(4) + 2;
                    if (r.Ub(1) == 1)
                    {
                        int dx = r.Sb(nb); int dy = r.Sb(nb);
                        recs.Add(NovaCara(dx, dy));
                    }
                    else
                    {
                        if (r.Ub(1) == 1) { int dy = r.Sb(nb); recs.Add(NovaCara(0, dy)); }
                        else { int dx = r.Sb(nb); recs.Add(NovaCara(dx, 0)); }
                    }
                }
                else
                {
                    int nb = r.Ub(4) + 2;
                    int dx = r.Sb(nb); int dy = r.Sb(nb); int dx2 = r.Sb(nb); int dy2 = r.Sb(nb);
                    recs.Add(NovaKrivka(dx, dy, dx2, dy2));
                }
            }
        }
        r.Align();
        konecP = r.P;
        return recs;
    }

    static SwfZaznam NovaCara(int dx, int dy)
    {
        var z = new SwfZaznam(); z.Kind = ZKind.Cara; z.Dx = dx; z.Dy = dy; return z;
    }
    static SwfZaznam NovaKrivka(int dx, int dy, int dx2, int dy2)
    {
        var z = new SwfZaznam(); z.Kind = ZKind.Krivka; z.Dx = dx; z.Dy = dy; z.Dx2 = dx2; z.Dy2 = dy2; return z;
    }

    static byte[] PisGlyf(List<SwfZaznam> recs, int nfb, int nlb)
    {
        var w = new BitW();
        w.Ub(nfb, 4); w.Ub(nlb, 4);
        foreach (SwfZaznam rec in recs)
        {
            if (rec.Kind == ZKind.Konec) { w.Ub(0, 1); w.Ub(0, 5); break; }
            if (rec.Kind == ZKind.Styl)
            {
                int flags = 0;
                if (rec.MaPosun) flags |= 0x01;
                if (rec.MaFs0) flags |= 0x02;
                if (rec.MaFs1) flags |= 0x04;
                if (rec.MaLs) flags |= 0x08;
                w.Ub(0, 1); w.Ub(flags, 5);
                if (rec.MaPosun)
                {
                    int nb = NbitsS(new int[] { rec.PosunX, rec.PosunY });
                    w.Ub(nb, 5); w.Sb(rec.PosunX, nb); w.Sb(rec.PosunY, nb);
                }
                if (rec.MaFs0) w.Ub(rec.Fs0, nfb);
                if (rec.MaFs1) w.Ub(rec.Fs1, nfb);
                if (rec.MaLs) w.Ub(rec.Ls, nlb);
            }
            else if (rec.Kind == ZKind.Cara)
            {
                int dx = rec.Dx, dy = rec.Dy;
                int nb = Math.Max(2, NbitsS(new int[] { dx, dy }));
                w.Ub(1, 1); w.Ub(1, 1); w.Ub(nb - 2, 4);
                if (dx != 0 && dy != 0) { w.Ub(1, 1); w.Sb(dx, nb); w.Sb(dy, nb); }
                else if (dx == 0) { w.Ub(0, 1); w.Ub(1, 1); w.Sb(dy, nb); }
                else { w.Ub(0, 1); w.Ub(0, 1); w.Sb(dx, nb); }
            }
            else // Krivka
            {
                int[] vals = new int[] { rec.Dx, rec.Dy, rec.Dx2, rec.Dy2 };
                int nb = Math.Max(2, NbitsS(vals));
                w.Ub(1, 1); w.Ub(0, 1); w.Ub(nb - 2, 4);
                for (int i = 0; i < 4; i++) w.Sb(vals[i], nb);
            }
        }
        return w.Bytes();
    }

    // minimalni pocet bitu (se znamenkem) na ulozeni vsech hodnot, min. 1
    static int NbitsS(int[] vals)
    {
        int n = 1;
        foreach (int v in vals)
        {
            if (v == 0) continue;
            int k = v > 0 ? BitLength(v) + 1 : BitLength(~v) + 1;
            if (k > n) n = k;
        }
        return n;
    }
    static int BitLength(int x)
    {
        int n = 0;
        while (x != 0) { x >>= 1; n++; }
        return n;
    }

    // -------------------------------------------------------- obrysy: SHAPE zaznamy <-> geometrie
    static List<Kontura> Kontury(List<SwfZaznam> recs)
    {
        var cs = new List<Kontura>();
        double x = 0, y = 0; Kontura cur = null;
        foreach (SwfZaznam r in recs)
        {
            if (r.Kind == ZKind.Styl)
            {
                if (r.MaPosun)
                {
                    if (cur != null) cs.Add(cur);
                    x = r.PosunX; y = r.PosunY;
                    cur = new Kontura(); cur.Sx = x; cur.Sy = y;
                }
            }
            else if (r.Kind == ZKind.Cara)
            {
                double nx = x + r.Dx, ny = y + r.Dy;
                var s = new Seg(); s.Krivka = false; s.Ex = nx; s.Ey = ny;
                cur.Segy.Add(s); x = nx; y = ny;
            }
            else if (r.Kind == ZKind.Krivka)
            {
                double cx = x + r.Dx, cy = y + r.Dy;
                double ax = cx + r.Dx2, ay = cy + r.Dy2;
                var s = new Seg(); s.Krivka = true; s.Cx = cx; s.Cy = cy; s.Ex = ax; s.Ey = ay;
                cur.Segy.Add(s); x = ax; y = ay;
            }
            else // Konec
            {
                if (cur != null) { cs.Add(cur); cur = null; }
            }
        }
        if (cur != null) cs.Add(cur);
        return cs;
    }

    // sestavi SHAPE zaznamy z obrysu; MoveTo je absolutni, fillstyle1 se nastavi jen na prvni kontuře
    static List<SwfZaznam> SestavZaznamy(List<Kontura> cs, int fillstyle)
    {
        var recs = new List<SwfZaznam>();
        bool prvni = true;
        foreach (Kontura c in cs)
        {
            var st = new SwfZaznam(); st.Kind = ZKind.Styl;
            st.MaPosun = true; st.PosunX = (int)Math.Round(c.Sx); st.PosunY = (int)Math.Round(c.Sy);
            if (prvni) { st.MaFs1 = true; st.Fs1 = fillstyle; prvni = false; }
            recs.Add(st);
            double x = c.Sx, y = c.Sy;
            foreach (Seg s in c.Segy)
            {
                if (!s.Krivka)
                {
                    int dx = (int)Math.Round(s.Ex - x), dy = (int)Math.Round(s.Ey - y);
                    recs.Add(NovaCara(dx, dy));
                    x = s.Ex; y = s.Ey;
                }
                else
                {
                    int dx = (int)Math.Round(s.Cx - x), dy = (int)Math.Round(s.Cy - y);
                    int dx2 = (int)Math.Round(s.Ex - s.Cx), dy2 = (int)Math.Round(s.Ey - s.Cy);
                    recs.Add(NovaKrivka(dx, dy, dx2, dy2));
                    x = s.Ex; y = s.Ey;
                }
            }
        }
        var e = new SwfZaznam(); e.Kind = ZKind.Konec; recs.Add(e);
        return recs;
    }

    // vraci [xmin,ymin,xmax,ymax]
    static double[] Bbox(List<Kontura> cs)
    {
        bool ma = false;
        double minx = 0, miny = 0, maxx = 0, maxy = 0;
        foreach (Kontura c in cs)
        {
            Zvetsi(ref ma, ref minx, ref miny, ref maxx, ref maxy, c.Sx, c.Sy);
            foreach (Seg s in c.Segy)
            {
                if (!s.Krivka) Zvetsi(ref ma, ref minx, ref miny, ref maxx, ref maxy, s.Ex, s.Ey);
                else
                {
                    Zvetsi(ref ma, ref minx, ref miny, ref maxx, ref maxy, s.Cx, s.Cy);
                    Zvetsi(ref ma, ref minx, ref miny, ref maxx, ref maxy, s.Ex, s.Ey);
                }
            }
        }
        if (!ma) return new double[] { 0, 0, 0, 0 };
        return new double[] { minx, miny, maxx, maxy };
    }
    static void Zvetsi(ref bool ma, ref double minx, ref double miny, ref double maxx, ref double maxy, double x, double y)
    {
        if (!ma) { minx = maxx = x; miny = maxy = y; ma = true; return; }
        if (x < minx) minx = x; if (x > maxx) maxx = x;
        if (y < miny) miny = y; if (y > maxy) maxy = y;
    }

    // posun + meritko kolem bodu (ox,oy), vysledek zaokrouhlen na cele jednotky (twips)
    static List<Kontura> Tx(List<Kontura> cs, double dx, double dy, double sx, double sy, double ox, double oy)
    {
        var outp = new List<Kontura>();
        foreach (Kontura c in cs)
        {
            var n = new Kontura();
            n.Sx = Math.Round(ox + (c.Sx - ox) * sx + dx);
            n.Sy = Math.Round(oy + (c.Sy - oy) * sy + dy);
            foreach (Seg s in c.Segy)
            {
                var ns = new Seg(); ns.Krivka = s.Krivka;
                if (s.Krivka)
                {
                    ns.Cx = Math.Round(ox + (s.Cx - ox) * sx + dx);
                    ns.Cy = Math.Round(oy + (s.Cy - oy) * sy + dy);
                }
                ns.Ex = Math.Round(ox + (s.Ex - ox) * sx + dx);
                ns.Ey = Math.Round(oy + (s.Ey - oy) * sy + dy);
                n.Segy.Add(ns);
            }
            outp.Add(n);
        }
        return outp;
    }

    // zrcadleni podle svisle osy x=axis; obraci smer obrysu, aby zustala zachovana vypln
    static List<Kontura> Zrcadli(List<Kontura> cs, double axis)
    {
        var outp = new List<Kontura>();
        foreach (Kontura c in cs)
        {
            int m = c.Segy.Count;
            double[] ptsX = new double[m + 1]; double[] ptsY = new double[m + 1];
            ptsX[0] = 2 * axis - c.Sx; ptsY[0] = c.Sy;
            var mirr = new Seg[m];
            for (int i = 0; i < m; i++)
            {
                Seg s = c.Segy[i];
                var ns = new Seg(); ns.Krivka = s.Krivka;
                if (s.Krivka) { ns.Cx = 2 * axis - s.Cx; ns.Cy = s.Cy; }
                ns.Ex = 2 * axis - s.Ex; ns.Ey = s.Ey;
                mirr[i] = ns;
                ptsX[i + 1] = ns.Ex; ptsY[i + 1] = ns.Ey;
            }
            var revSegy = new List<Seg>();
            for (int i = m - 1; i >= 0; i--)
            {
                Seg s = mirr[i];
                var rs = new Seg(); rs.Krivka = s.Krivka;
                if (s.Krivka) { rs.Cx = s.Cx; rs.Cy = s.Cy; }
                rs.Ex = ptsX[i]; rs.Ey = ptsY[i];
                revSegy.Add(rs);
            }
            var n = new Kontura();
            n.Sx = ptsX[m]; n.Sy = ptsY[m];
            n.Segy = revSegy;
            outp.Add(n);
        }
        return outp;
    }

    // obrysy z acc_cs, ktere lezi cele nad telem base_cs (tolerance kvuli mirnemu posunu pod carkou)
    static List<Kontura> OddelAkcent(List<Kontura> baseCs, List<Kontura> accCs, double tol)
    {
        double top = Bbox(baseCs)[1];
        var extra = new List<Kontura>();
        foreach (Kontura c in accCs)
        {
            var jedna = new List<Kontura>(); jedna.Add(c);
            double[] bb = Bbox(jedna);
            if (bb[3] <= top + tol) extra.Add(c);
        }
        return extra;
    }

    // hacek = zrcadlena carka vlevo + puvodni carka vpravo, sesazene do klina
    static List<Kontura> Hacek(List<Kontura> acuteCs)
    {
        double[] bb = Bbox(acuteCs);
        double x0 = bb[0], x1 = bb[2];
        double w = x1 - x0;
        List<Kontura> right = acuteCs;
        List<Kontura> left = Zrcadli(acuteCs, (x0 + x1) / 2.0);
        left = Tx(left, -w * 0.52, 0, 1.0, 1.0, 0, 0);
        right = Tx(right, w * 0.52, 0, 1.0, 1.0, 0, 0);
        var outp = new List<Kontura>(left); outp.AddRange(right);
        return outp;
    }

    // umisti akcent nad stred zakladniho pismene s danou mezerou (gap) a meritkem
    static List<Kontura> UmistiNad(List<Kontura> baseCs, List<Kontura> accCs, double gap, double scale)
    {
        double[] bb = Bbox(baseCs);
        double bx0 = bb[0], by0 = bb[1], bx1 = bb[2];
        double[] bba = Bbox(accCs);
        List<Kontura> acc = Tx(accCs, 0, 0, scale, scale, (bba[0] + bba[2]) / 2.0, (bba[1] + bba[3]) / 2.0);
        double[] bba2 = Bbox(acc);
        double dx = (bx0 + bx1) / 2.0 - (bba2[0] + bba2[2]) / 2.0;
        double dy = (by0 - gap) - bba2[3];
        return Tx(acc, dx, dy, 1.0, 1.0, 0, 0);
    }

    // -------------------------------------------------------- doplneni ceskych znaku do jednoho fontu
    // vraci pocet pridanych glyfu; font f meni primo (pridava glyfy/kody/sirky/hranice a seradi je)
    static int PridejCestinu(Pismo f)
    {
        var cm = new Dictionary<int, int>();
        for (int i = 0; i < f.Kody.Count; i++) cm[f.Kody[i]] = i;   // kod znaku -> index glyfu

        if (!(Ma(cm, 'a') && Ma(cm, 'á'))) return 0;

        List<Kontura> aCs = ObrysyZnaku(f, cm, 'a');
        List<Kontura> aaCs = ObrysyZnaku(f, cm, 'á');
        List<Kontura> acute = OddelAkcent(aCs, aaCs, 200);
        if (acute.Count == 0) return 0;
        List<Kontura> caron = Hacek(acute);

        List<Kontura> ring = null;
        if (Ma(cm, '°'))
        {
            List<Kontura> r = ObrysyZnaku(f, cm, '°');
            double[] bb = Bbox(r);
            ring = Tx(r, 0, 0, 0.62, 0.62, (bb[0] + bb[2]) / 2.0, (bb[1] + bb[3]) / 2.0);
        }
        List<Kontura> apo;
        if (Ma(cm, '’'))
        {
            apo = ObrysyZnaku(f, cm, '’');
        }
        else
        {
            double[] bb = Bbox(acute);
            apo = Tx(acute, 0, 0, 0.55, 1.15, (bb[0] + bb[2]) / 2.0, (bb[1] + bb[3]) / 2.0);
        }

        int puvodniPocet = f.Kody.Count;

        foreach (ZnakPar par in CARON)
        {
            if (Ma(cm, par.Cil) || !Ma(cm, par.Zaklad)) continue;
            List<Kontura> bcs = ObrysyZnaku(f, cm, par.Zaklad);
            short badv = SirkaZnaku(f, cm, par.Zaklad);
            double gap = char.IsLower(par.Cil) ? 900 : 1100;
            var cs = new List<Kontura>(bcs); cs.AddRange(UmistiNad(bcs, caron, gap, 1.0));
            EmitZnak(f, par.Cil, cs, badv);
        }
        if (ring != null)
        {
            foreach (ZnakPar par in RING)
            {
                if (Ma(cm, par.Cil) || !Ma(cm, par.Zaklad)) continue;
                List<Kontura> bcs = ObrysyZnaku(f, cm, par.Zaklad);
                short badv = SirkaZnaku(f, cm, par.Zaklad);
                var cs = new List<Kontura>(bcs); cs.AddRange(UmistiNad(bcs, ring, 800, 1.0));
                EmitZnak(f, par.Cil, cs, badv);
            }
        }
        foreach (ZnakPar par in ACUTE)
        {
            if (Ma(cm, par.Cil) || !Ma(cm, par.Zaklad)) continue;
            List<Kontura> bcs = ObrysyZnaku(f, cm, par.Zaklad);
            short badv = SirkaZnaku(f, cm, par.Zaklad);
            var cs = new List<Kontura>(bcs); cs.AddRange(UmistiNad(bcs, acute, 700, 1.0));
            EmitZnak(f, par.Cil, cs, badv);
        }
        if (apo != null)
        {
            foreach (ZnakPar par in APOST)
            {
                if (Ma(cm, par.Cil) || !Ma(cm, par.Zaklad)) continue;
                List<Kontura> bcs = ObrysyZnaku(f, cm, par.Zaklad);
                short badv = SirkaZnaku(f, cm, par.Zaklad);
                double[] bbb = Bbox(bcs);
                double[] bba = Bbox(apo);
                List<Kontura> small = Tx(apo, 0, 0, 0.80, 0.80, (bba[0] + bba[2]) / 2.0, (bba[1] + bba[3]) / 2.0);
                double[] bbs = Bbox(small);
                List<Kontura> placed = Tx(small, bbb[2] + 250 - bbs[0], (bbb[1] + 400) - bbs[1], 1.0, 1.0, 0, 0);
                var cs = new List<Kontura>(bcs); cs.AddRange(placed);
                short novaAdv = (short)(badv + (int)Math.Round(bbs[2] - bbs[0]) + 500);
                EmitZnak(f, par.Cil, cs, novaAdv);
            }
        }

        // seradit vsechny paralelni tabulky podle kodu (prehravac ceka vzestupne)
        SeraditPodleKodu(f);
        return f.Kody.Count - puvodniPocet;
    }

    static bool Ma(Dictionary<int, int> cm, char ch) { return cm.ContainsKey((int)ch); }
    static List<Kontura> ObrysyZnaku(Pismo f, Dictionary<int, int> cm, char ch) { return Kontury(f.Glyfy[cm[(int)ch]].Zaznamy); }
    static short SirkaZnaku(Pismo f, Dictionary<int, int> cm, char ch) { return f.Sirky[cm[(int)ch]]; }

    static void EmitZnak(Pismo f, char ch, List<Kontura> cs, short adv)
    {
        double[] bb = Bbox(cs);
        var novy = new Glyf();
        novy.Zaznamy = SestavZaznamy(cs, 1);
        novy.Nfb = f.Glyfy[0].Nfb; novy.Nlb = f.Glyfy[0].Nlb;   // stejne jako prvni glyf fontu
        f.Glyfy.Add(novy);
        f.Kody.Add((int)ch);
        f.Sirky.Add(adv);
        f.Hranice.Add(new int[] {
            (int)Math.Round(bb[0]), (int)Math.Round(bb[2]), (int)Math.Round(bb[1]), (int)Math.Round(bb[3]) });
    }

    static void SeraditPodleKodu(Pismo f)
    {
        int n = f.Kody.Count;
        int[] klice = new int[n]; int[] idx = new int[n];
        for (int i = 0; i < n; i++) { klice[i] = f.Kody[i]; idx[i] = i; }
        Array.Sort(klice, idx);
        var novaG = new List<Glyf>(n); var novaK = new List<int>(n);
        var novaS = new List<short>(n); var novaH = new List<int[]>(n);
        for (int i = 0; i < n; i++)
        {
            int j = idx[i];
            novaG.Add(f.Glyfy[j]); novaK.Add(f.Kody[j]); novaS.Add(f.Sirky[j]); novaH.Add(f.Hranice[j]);
        }
        f.Glyfy = novaG; f.Kody = novaK; f.Sirky = novaS; f.Hranice = novaH;
    }

    // -------------------------------------------------------- drobnosti (cteni/zapis bajtu)
    static int U16(byte[] d, int o) { return d[o] | (d[o + 1] << 8); }
    static short S16(byte[] d, int o) { return (short)(d[o] | (d[o + 1] << 8)); }
    static uint U32(byte[] d, int o) { return (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24)); }
    static void W16(List<byte> o, ushort v) { o.Add((byte)v); o.Add((byte)(v >> 8)); }
    static void W32(List<byte> o, uint v) { o.Add((byte)v); o.Add((byte)(v >> 8)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 24)); }
    static void WS16(List<byte> o, short v) { W16(o, unchecked((ushort)v)); }
    static void W32At(byte[] b, int o, uint v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
    }
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
}
