// Czech translation for Prototype - adds Czech characters to Scaleform GFX fonts (DefineFont3)
// Port of the Python cz_fonty.py. Diacritics are not drawn from scratch, they are derived
// from existing glyphs: acute = extra outline in "a" vs "a", caron = mirrored acute + original,
// ring from the degree sign, caron on d/t from an apostrophe. Details in TECHNIKA.md, chapter 5.
using System;
using System.Collections.Generic;
using System.Text;

// ---------------------------------------------------------------- exception
class FontFormatException : Exception
{
    public FontFormatException(string message) : base(message) { }
}

// ---------------------------------------------------------------- bit stream (SHAPE records are bit-packed)
class BitReader
{
    byte[] d; int p; int b;
    public BitReader(byte[] d, int p) { this.d = d; this.p = p; this.b = 0; }
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

class BitWriter
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

// ---------------------------------------------------------------- SHAPE record (style / line / curve / end)
enum RecordKind { End, Style, Line, Curve }

class SwfRecord
{
    public RecordKind Kind;
    // style
    public bool HasMove; public int MoveX, MoveY;   // MoveTo is ABSOLUTE, not relative
    public bool HasFillStyle0; public int FillStyle0;
    public bool HasFillStyle1; public int FillStyle1;
    public bool HasLineStyle; public int LineStyle;
    // line: Dx,Dy = delta. curve: Dx,Dy = control point delta, Dx2,Dy2 = anchor delta from control point
    public int Dx, Dy, Dx2, Dy2;
}

// ---------------------------------------------------------------- glyph and font
class Glyph
{
    public List<SwfRecord> Records;
    public int Nfb, Nlb;   // number of bits for fillstyle / linestyle indices in this glyph
}

class GfxFont
{
    public int Fid; public byte Fl, Lang; public byte[] Name;
    public bool Wide, Layout;
    public List<Glyph> Glyphs;
    public List<int> Codes;
    public short Asc, Desc, Lead;
    public List<short> Widths;
    public List<int[]> Bounds;   // [xmin,xmax,ymin,ymax] - RECT order
    public byte[] Kern;          // kerning is keyed by character code, not index -> left unchanged
}

// ---------------------------------------------------------------- outline (glyph geometry, absolute coordinates)
class Seg
{
    public bool IsCurve;
    public double Cx, Cy;   // control point (curve only)
    public double Ex, Ey;   // end point (line), or anchor (curve)
}

class Contour
{
    public double Sx, Sy;
    public List<Seg> Segments = new List<Seg>();
}

// ---------------------------------------------------------------- Czech characters: target <- base
struct CharPair
{
    public char Target, Base;
    public CharPair(char target, char baseChar) { Target = target; Base = baseChar; }
}

// ---------------------------------------------------------------- SWF/GFX tag (header + body)
class GfxTag
{
    public int Code, Hdr, Body, Len;
}

// ================================================================== main class
static class FontPatcher
{
    static readonly CharPair[] CARON = new CharPair[] {
        new CharPair('č','c'), new CharPair('ě','e'), new CharPair('ň','n'), new CharPair('ř','r'),
        new CharPair('š','s'), new CharPair('ž','z'),
        new CharPair('Č','C'), new CharPair('Ď','D'), new CharPair('Ě','E'), new CharPair('Ň','N'),
        new CharPair('Ř','R'), new CharPair('Š','S'), new CharPair('Ť','T'), new CharPair('Ž','Z'),
    };
    static readonly CharPair[] APOST = new CharPair[] { new CharPair('ď','d'), new CharPair('ť','t') };
    static readonly CharPair[] RING  = new CharPair[] { new CharPair('ů','u'), new CharPair('Ů','U') };
    static readonly CharPair[] ACUTE = new CharPair[] { new CharPair('ý','y'), new CharPair('Ý','Y') };

    // -------------------------------------------------------- public API
    public static byte[] Patch(byte[] gfx)
    {
        try { return PatchImpl(gfx); }
        catch (FontFormatException) { throw; }
        catch (Exception) { throw new Exception("soubor se nepodařilo zpracovat jako font GFX (poškozený nebo nepodporovaný formát)"); }
    }

    static byte[] PatchImpl(byte[] gfx)
    {
        if (gfx == null || gfx.Length < 8 || gfx[0] != (byte)'G' || gfx[1] != (byte)'F' || gfx[2] != (byte)'X')
            throw new FontFormatException("soubor není podporovaný font GFX");

        List<GfxTag> tags = ReadTags(gfx);
        bool hasFont = false;
        foreach (GfxTag t in tags) if (t.Code == 75) { hasFont = true; break; }
        if (!hasFont) throw new FontFormatException("v souboru nejsou žádné fonty");

        // self-test: take apart every font (DefineFont3) and reassemble unchanged - must come
        // out byte-identical to the original tag body. Otherwise we don't understand the format
        // well enough to safely write into it.
        // Note: the whole container (the other SWF tags) does NOT go through the self-test - the
        // original game has some tags written non-canonically (long header form even for a short
        // length). Just like cz_fonty.py, we always use the canonical form when reassembling, so
        // the resulting file is not byte-identical to the original outside of the patched fonts -
        // and it isn't meant to be, that's exactly what the original script does too.
        foreach (GfxTag t in tags)
        {
            if (t.Code != 75) continue;
            GfxFont f0 = ReadFont(gfx, t.Body, t.Len);
            byte[] again = WriteFont(f0);
            byte[] original = Sub(gfx, t.Body, t.Len);
            if (!BytesEqual(again, original)) throw new FontFormatException("autotest formátu fontu neprošel");
        }

        byte[] result = Assemble(gfx, tags, true);
        if (BytesEqual(result, gfx)) return null;   // already has Czech characters
        return result;
    }

    // -------------------------------------------------------- parsing SWF/GFX tags
    // header: "GFX" u8 version u32 size, RECT, u16 fps, u16 frame count, then tags
    static List<GfxTag> ReadTags(byte[] d)
    {
        int nb = d[8] >> 3;                          // top 5 bits of the first RECT byte = bits per component
        int rectBytes = (5 + 4 * nb + 7) / 8;
        int p = 8 + rectBytes + 4;                    // + fps(u16) + frame count(u16)
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

    // reassembles the whole file; patch=true adds Czech characters to every DefineFont3 (code 75)
    static byte[] Assemble(byte[] data, List<GfxTag> tags, bool patch)
    {
        var outp = new List<byte>();
        outp.AddRange(Sub(data, 0, tags[0].Hdr));
        foreach (GfxTag t in tags)
        {
            byte[] body;
            if (t.Code == 75)
            {
                GfxFont f = ReadFont(data, t.Body, t.Len);
                if (patch) AddCzechChars(f);
                body = WriteFont(f);
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
        byte[] result = outp.ToArray();
        W32At(result, 4, (uint)result.Length);
        return result;
    }

    // -------------------------------------------------------- DefineFont3 (code 75)
    static GfxFont ReadFont(byte[] d, int body, int len)
    {
        int q = body;
        int fid = U16(d, q); q += 2;
        byte fl = d[q]; q += 1;
        byte lang = d[q]; q += 1;
        int nl = d[q]; q += 1;
        byte[] name = Sub(d, q, nl); q += nl;
        int ng = U16(d, q); q += 2;
        bool wide = (fl & 0x08) != 0;
        bool layout = (fl & 0x80) != 0;
        int sz = wide ? 4 : 2;
        int tbl = q;
        int[] offs = new int[ng];
        for (int i = 0; i < ng; i++) offs[i] = wide ? (int)U32(d, q + i * sz) : U16(d, q + i * sz);
        int codeOff = wide ? (int)U32(d, q + ng * sz) : U16(d, q + ng * sz);

        var glyphs = new List<Glyph>(ng);
        for (int i = 0; i < ng; i++)
        {
            int gp = tbl + offs[i];
            int nfb, nlb, endP;
            List<SwfRecord> recs = ReadGlyph(d, gp, out nfb, out nlb, out endP);
            var g = new Glyph(); g.Records = recs; g.Nfb = nfb; g.Nlb = nlb;
            glyphs.Add(g);
        }

        int p = tbl + codeOff;
        var codes = new List<int>(ng);
        for (int i = 0; i < ng; i++) { codes.Add(U16(d, p)); p += 2; }

        short asc = 0, desc = 0, lead = 0;
        var widths = new List<short>();
        var bounds = new List<int[]>();
        byte[] kern = new byte[0];
        if (layout)
        {
            asc = S16(d, p); p += 2; desc = S16(d, p); p += 2; lead = S16(d, p); p += 2;
            for (int i = 0; i < ng; i++) { widths.Add(S16(d, p)); p += 2; }
            for (int i = 0; i < ng; i++)
            {
                int np;
                int[] v = ReadRect(d, p, out np);
                bounds.Add(v); p = np;
            }
            kern = Sub(d, p, body + len - p);
        }

        var f = new GfxFont();
        f.Fid = fid; f.Fl = fl; f.Lang = lang; f.Name = name; f.Wide = wide; f.Layout = layout;
        f.Glyphs = glyphs; f.Codes = codes; f.Asc = asc; f.Desc = desc; f.Lead = lead;
        f.Widths = widths; f.Bounds = bounds; f.Kern = kern;
        return f;
    }

    static byte[] WriteFont(GfxFont f)
    {
        int n = f.Glyphs.Count;
        byte[][] shapes = new byte[n][];
        for (int i = 0; i < n; i++) shapes[i] = WriteGlyph(f.Glyphs[i].Records, f.Glyphs[i].Nfb, f.Glyphs[i].Nlb);

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
        outp.Add((byte)f.Name.Length); outp.AddRange(f.Name);
        W16(outp, (ushort)n);
        for (int i = 0; i < n; i++) { if (wide) W32(outp, (uint)offs[i]); else W16(outp, (ushort)offs[i]); }
        if (wide) W32(outp, (uint)codeOff); else W16(outp, (ushort)codeOff);
        for (int i = 0; i < n; i++) outp.AddRange(shapes[i]);
        for (int i = 0; i < n; i++) W16(outp, (ushort)f.Codes[i]);
        if (f.Layout)
        {
            WS16(outp, f.Asc); WS16(outp, f.Desc); WS16(outp, f.Lead);
            for (int i = 0; i < f.Widths.Count; i++) WS16(outp, f.Widths[i]);
            for (int i = 0; i < f.Bounds.Count; i++) outp.AddRange(WriteRect(f.Bounds[i]));
            outp.AddRange(f.Kern);
        }
        return outp.ToArray();
    }

    static int[] ReadRect(byte[] d, int p, out int newPos)
    {
        var r = new BitReader(d, p);
        int nb = r.Ub(5);
        int[] v = new int[4];
        for (int i = 0; i < 4; i++) v[i] = r.Sb(nb);
        r.Align();
        newPos = r.P;
        return v;
    }

    static byte[] WriteRect(int[] v)
    {
        int nb = NbitsS(v);
        var w = new BitWriter();
        w.Ub(nb, 5);
        for (int i = 0; i < 4; i++) w.Sb(v[i], nb);
        return w.Bytes();
    }

    // -------------------------------------------------------- glyph SHAPE records (bit-packed)
    static List<SwfRecord> ReadGlyph(byte[] d, int pos, out int nfb, out int nlb, out int endP)
    {
        var r = new BitReader(d, pos);
        nfb = r.Ub(4); nlb = r.Ub(4);
        var recs = new List<SwfRecord>();
        while (true)
        {
            if (r.Ub(1) == 0)
            {
                int flags = r.Ub(5);
                if (flags == 0) { var e = new SwfRecord(); e.Kind = RecordKind.End; recs.Add(e); break; }
                var rec = new SwfRecord(); rec.Kind = RecordKind.Style;
                if ((flags & 0x01) != 0)
                {
                    int nb = r.Ub(5);
                    int mx = r.Sb(nb); int my = r.Sb(nb);
                    rec.HasMove = true; rec.MoveX = mx; rec.MoveY = my;
                }
                if ((flags & 0x02) != 0) { rec.HasFillStyle0 = true; rec.FillStyle0 = r.Ub(nfb); }
                if ((flags & 0x04) != 0) { rec.HasFillStyle1 = true; rec.FillStyle1 = r.Ub(nfb); }
                if ((flags & 0x08) != 0) { rec.HasLineStyle = true; rec.LineStyle = r.Ub(nlb); }
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
                        recs.Add(NewLineRecord(dx, dy));
                    }
                    else
                    {
                        if (r.Ub(1) == 1) { int dy = r.Sb(nb); recs.Add(NewLineRecord(0, dy)); }
                        else { int dx = r.Sb(nb); recs.Add(NewLineRecord(dx, 0)); }
                    }
                }
                else
                {
                    int nb = r.Ub(4) + 2;
                    int dx = r.Sb(nb); int dy = r.Sb(nb); int dx2 = r.Sb(nb); int dy2 = r.Sb(nb);
                    recs.Add(NewCurveRecord(dx, dy, dx2, dy2));
                }
            }
        }
        r.Align();
        endP = r.P;
        return recs;
    }

    static SwfRecord NewLineRecord(int dx, int dy)
    {
        var z = new SwfRecord(); z.Kind = RecordKind.Line; z.Dx = dx; z.Dy = dy; return z;
    }
    static SwfRecord NewCurveRecord(int dx, int dy, int dx2, int dy2)
    {
        var z = new SwfRecord(); z.Kind = RecordKind.Curve; z.Dx = dx; z.Dy = dy; z.Dx2 = dx2; z.Dy2 = dy2; return z;
    }

    static byte[] WriteGlyph(List<SwfRecord> recs, int nfb, int nlb)
    {
        var w = new BitWriter();
        w.Ub(nfb, 4); w.Ub(nlb, 4);
        foreach (SwfRecord rec in recs)
        {
            if (rec.Kind == RecordKind.End) { w.Ub(0, 1); w.Ub(0, 5); break; }
            if (rec.Kind == RecordKind.Style)
            {
                int flags = 0;
                if (rec.HasMove) flags |= 0x01;
                if (rec.HasFillStyle0) flags |= 0x02;
                if (rec.HasFillStyle1) flags |= 0x04;
                if (rec.HasLineStyle) flags |= 0x08;
                w.Ub(0, 1); w.Ub(flags, 5);
                if (rec.HasMove)
                {
                    int nb = NbitsS(new int[] { rec.MoveX, rec.MoveY });
                    w.Ub(nb, 5); w.Sb(rec.MoveX, nb); w.Sb(rec.MoveY, nb);
                }
                if (rec.HasFillStyle0) w.Ub(rec.FillStyle0, nfb);
                if (rec.HasFillStyle1) w.Ub(rec.FillStyle1, nfb);
                if (rec.HasLineStyle) w.Ub(rec.LineStyle, nlb);
            }
            else if (rec.Kind == RecordKind.Line)
            {
                int dx = rec.Dx, dy = rec.Dy;
                int nb = Math.Max(2, NbitsS(new int[] { dx, dy }));
                w.Ub(1, 1); w.Ub(1, 1); w.Ub(nb - 2, 4);
                if (dx != 0 && dy != 0) { w.Ub(1, 1); w.Sb(dx, nb); w.Sb(dy, nb); }
                else if (dx == 0) { w.Ub(0, 1); w.Ub(1, 1); w.Sb(dy, nb); }
                else { w.Ub(0, 1); w.Ub(0, 1); w.Sb(dx, nb); }
            }
            else // Curve
            {
                int[] vals = new int[] { rec.Dx, rec.Dy, rec.Dx2, rec.Dy2 };
                int nb = Math.Max(2, NbitsS(vals));
                w.Ub(1, 1); w.Ub(0, 1); w.Ub(nb - 2, 4);
                for (int i = 0; i < 4; i++) w.Sb(vals[i], nb);
            }
        }
        return w.Bytes();
    }

    // minimum number of bits (signed) to store all values, at least 1
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

    // -------------------------------------------------------- outlines: SHAPE records <-> geometry
    static List<Contour> GetContours(List<SwfRecord> recs)
    {
        var cs = new List<Contour>();
        double x = 0, y = 0; Contour cur = null;
        foreach (SwfRecord r in recs)
        {
            if (r.Kind == RecordKind.Style)
            {
                if (r.HasMove)
                {
                    if (cur != null) cs.Add(cur);
                    x = r.MoveX; y = r.MoveY;
                    cur = new Contour(); cur.Sx = x; cur.Sy = y;
                }
            }
            else if (r.Kind == RecordKind.Line)
            {
                double nx = x + r.Dx, ny = y + r.Dy;
                var s = new Seg(); s.IsCurve = false; s.Ex = nx; s.Ey = ny;
                cur.Segments.Add(s); x = nx; y = ny;
            }
            else if (r.Kind == RecordKind.Curve)
            {
                double cx = x + r.Dx, cy = y + r.Dy;
                double ax = cx + r.Dx2, ay = cy + r.Dy2;
                var s = new Seg(); s.IsCurve = true; s.Cx = cx; s.Cy = cy; s.Ex = ax; s.Ey = ay;
                cur.Segments.Add(s); x = ax; y = ay;
            }
            else // End
            {
                if (cur != null) { cs.Add(cur); cur = null; }
            }
        }
        if (cur != null) cs.Add(cur);
        return cs;
    }

    // builds SHAPE records from an outline; MoveTo is absolute, fillstyle1 is set only on the first contour
    static List<SwfRecord> BuildRecords(List<Contour> cs, int fillstyle)
    {
        var recs = new List<SwfRecord>();
        bool first = true;
        foreach (Contour c in cs)
        {
            var st = new SwfRecord(); st.Kind = RecordKind.Style;
            st.HasMove = true; st.MoveX = (int)Math.Round(c.Sx); st.MoveY = (int)Math.Round(c.Sy);
            if (first) { st.HasFillStyle1 = true; st.FillStyle1 = fillstyle; first = false; }
            recs.Add(st);
            double x = c.Sx, y = c.Sy;
            foreach (Seg s in c.Segments)
            {
                if (!s.IsCurve)
                {
                    int dx = (int)Math.Round(s.Ex - x), dy = (int)Math.Round(s.Ey - y);
                    recs.Add(NewLineRecord(dx, dy));
                    x = s.Ex; y = s.Ey;
                }
                else
                {
                    int dx = (int)Math.Round(s.Cx - x), dy = (int)Math.Round(s.Cy - y);
                    int dx2 = (int)Math.Round(s.Ex - s.Cx), dy2 = (int)Math.Round(s.Ey - s.Cy);
                    recs.Add(NewCurveRecord(dx, dy, dx2, dy2));
                    x = s.Ex; y = s.Ey;
                }
            }
        }
        var e = new SwfRecord(); e.Kind = RecordKind.End; recs.Add(e);
        return recs;
    }

    // returns [xmin,ymin,xmax,ymax]
    static double[] Bbox(List<Contour> cs)
    {
        bool has = false;
        double minx = 0, miny = 0, maxx = 0, maxy = 0;
        foreach (Contour c in cs)
        {
            Expand(ref has, ref minx, ref miny, ref maxx, ref maxy, c.Sx, c.Sy);
            foreach (Seg s in c.Segments)
            {
                if (!s.IsCurve) Expand(ref has, ref minx, ref miny, ref maxx, ref maxy, s.Ex, s.Ey);
                else
                {
                    Expand(ref has, ref minx, ref miny, ref maxx, ref maxy, s.Cx, s.Cy);
                    Expand(ref has, ref minx, ref miny, ref maxx, ref maxy, s.Ex, s.Ey);
                }
            }
        }
        if (!has) return new double[] { 0, 0, 0, 0 };
        return new double[] { minx, miny, maxx, maxy };
    }
    static void Expand(ref bool has, ref double minx, ref double miny, ref double maxx, ref double maxy, double x, double y)
    {
        if (!has) { minx = maxx = x; miny = maxy = y; has = true; return; }
        if (x < minx) minx = x; if (x > maxx) maxx = x;
        if (y < miny) miny = y; if (y > maxy) maxy = y;
    }

    // translate + scale around point (ox,oy), result rounded to whole units (twips)
    static List<Contour> Transform(List<Contour> cs, double dx, double dy, double sx, double sy, double ox, double oy)
    {
        var outp = new List<Contour>();
        foreach (Contour c in cs)
        {
            var n = new Contour();
            n.Sx = Math.Round(ox + (c.Sx - ox) * sx + dx);
            n.Sy = Math.Round(oy + (c.Sy - oy) * sy + dy);
            foreach (Seg s in c.Segments)
            {
                var ns = new Seg(); ns.IsCurve = s.IsCurve;
                if (s.IsCurve)
                {
                    ns.Cx = Math.Round(ox + (s.Cx - ox) * sx + dx);
                    ns.Cy = Math.Round(oy + (s.Cy - oy) * sy + dy);
                }
                ns.Ex = Math.Round(ox + (s.Ex - ox) * sx + dx);
                ns.Ey = Math.Round(oy + (s.Ey - oy) * sy + dy);
                n.Segments.Add(ns);
            }
            outp.Add(n);
        }
        return outp;
    }

    // mirror around the vertical axis x=axis; reverses the outline direction to keep the fill correct
    static List<Contour> Mirror(List<Contour> cs, double axis)
    {
        var outp = new List<Contour>();
        foreach (Contour c in cs)
        {
            int m = c.Segments.Count;
            double[] ptsX = new double[m + 1]; double[] ptsY = new double[m + 1];
            ptsX[0] = 2 * axis - c.Sx; ptsY[0] = c.Sy;
            var mirr = new Seg[m];
            for (int i = 0; i < m; i++)
            {
                Seg s = c.Segments[i];
                var ns = new Seg(); ns.IsCurve = s.IsCurve;
                if (s.IsCurve) { ns.Cx = 2 * axis - s.Cx; ns.Cy = s.Cy; }
                ns.Ex = 2 * axis - s.Ex; ns.Ey = s.Ey;
                mirr[i] = ns;
                ptsX[i + 1] = ns.Ex; ptsY[i + 1] = ns.Ey;
            }
            var revSegy = new List<Seg>();
            for (int i = m - 1; i >= 0; i--)
            {
                Seg s = mirr[i];
                var rs = new Seg(); rs.IsCurve = s.IsCurve;
                if (s.IsCurve) { rs.Cx = s.Cx; rs.Cy = s.Cy; }
                rs.Ex = ptsX[i]; rs.Ey = ptsY[i];
                revSegy.Add(rs);
            }
            var n = new Contour();
            n.Sx = ptsX[m]; n.Sy = ptsY[m];
            n.Segments = revSegy;
            outp.Add(n);
        }
        return outp;
    }

    // outlines from accCs that lie entirely above the body of baseCs (tolerance for a slight shift under the acute)
    static List<Contour> SeparateAccent(List<Contour> baseCs, List<Contour> accCs, double tol)
    {
        double top = Bbox(baseCs)[1];
        var extra = new List<Contour>();
        foreach (Contour c in accCs)
        {
            var one = new List<Contour>(); one.Add(c);
            double[] bb = Bbox(one);
            if (bb[3] <= top + tol) extra.Add(c);
        }
        return extra;
    }

    // caron = mirrored acute on the left + original acute on the right, arranged into a wedge
    static List<Contour> BuildCaron(List<Contour> acuteCs)
    {
        double[] bb = Bbox(acuteCs);
        double x0 = bb[0], x1 = bb[2];
        double w = x1 - x0;
        List<Contour> right = acuteCs;
        List<Contour> left = Mirror(acuteCs, (x0 + x1) / 2.0);
        left = Transform(left, -w * 0.52, 0, 1.0, 1.0, 0, 0);
        right = Transform(right, w * 0.52, 0, 1.0, 1.0, 0, 0);
        var outp = new List<Contour>(left); outp.AddRange(right);
        return outp;
    }

    // places the accent above the center of the base letter with the given gap and scale
    static List<Contour> PlaceAbove(List<Contour> baseCs, List<Contour> accCs, double gap, double scale)
    {
        double[] bb = Bbox(baseCs);
        double bx0 = bb[0], by0 = bb[1], bx1 = bb[2];
        double[] bba = Bbox(accCs);
        List<Contour> acc = Transform(accCs, 0, 0, scale, scale, (bba[0] + bba[2]) / 2.0, (bba[1] + bba[3]) / 2.0);
        double[] bba2 = Bbox(acc);
        double dx = (bx0 + bx1) / 2.0 - (bba2[0] + bba2[2]) / 2.0;
        double dy = (by0 - gap) - bba2[3];
        return Transform(acc, dx, dy, 1.0, 1.0, 0, 0);
    }

    // -------------------------------------------------------- adding Czech characters to a single font
    // returns the number of glyphs added; mutates font f directly (adds glyphs/codes/widths/bounds and sorts them)
    static int AddCzechChars(GfxFont f)
    {
        var cm = new Dictionary<int, int>();
        for (int i = 0; i < f.Codes.Count; i++) cm[f.Codes[i]] = i;   // character code -> glyph index

        if (!(Has(cm, 'a') && Has(cm, 'á'))) return 0;

        List<Contour> aCs = CharContours(f, cm, 'a');
        List<Contour> aaCs = CharContours(f, cm, 'á');
        List<Contour> acute = SeparateAccent(aCs, aaCs, 200);
        if (acute.Count == 0) return 0;
        List<Contour> caron = BuildCaron(acute);

        List<Contour> ring = null;
        if (Has(cm, '°'))
        {
            List<Contour> r = CharContours(f, cm, '°');
            double[] bb = Bbox(r);
            ring = Transform(r, 0, 0, 0.62, 0.62, (bb[0] + bb[2]) / 2.0, (bb[1] + bb[3]) / 2.0);
        }
        List<Contour> apo;
        if (Has(cm, '’'))
        {
            apo = CharContours(f, cm, '’');
        }
        else
        {
            double[] bb = Bbox(acute);
            apo = Transform(acute, 0, 0, 0.55, 1.15, (bb[0] + bb[2]) / 2.0, (bb[1] + bb[3]) / 2.0);
        }

        int originalCount = f.Codes.Count;

        foreach (CharPair par in CARON)
        {
            if (Has(cm, par.Target) || !Has(cm, par.Base)) continue;
            List<Contour> bcs = CharContours(f, cm, par.Base);
            short badv = CharWidth(f, cm, par.Base);
            double gap = char.IsLower(par.Target) ? 900 : 1100;
            var cs = new List<Contour>(bcs); cs.AddRange(PlaceAbove(bcs, caron, gap, 1.0));
            EmitChar(f, par.Target, cs, badv);
        }
        if (ring != null)
        {
            foreach (CharPair par in RING)
            {
                if (Has(cm, par.Target) || !Has(cm, par.Base)) continue;
                List<Contour> bcs = CharContours(f, cm, par.Base);
                short badv = CharWidth(f, cm, par.Base);
                var cs = new List<Contour>(bcs); cs.AddRange(PlaceAbove(bcs, ring, 800, 1.0));
                EmitChar(f, par.Target, cs, badv);
            }
        }
        foreach (CharPair par in ACUTE)
        {
            if (Has(cm, par.Target) || !Has(cm, par.Base)) continue;
            List<Contour> bcs = CharContours(f, cm, par.Base);
            short badv = CharWidth(f, cm, par.Base);
            var cs = new List<Contour>(bcs); cs.AddRange(PlaceAbove(bcs, acute, 700, 1.0));
            EmitChar(f, par.Target, cs, badv);
        }
        if (apo != null)
        {
            foreach (CharPair par in APOST)
            {
                if (Has(cm, par.Target) || !Has(cm, par.Base)) continue;
                List<Contour> bcs = CharContours(f, cm, par.Base);
                short badv = CharWidth(f, cm, par.Base);
                double[] bbb = Bbox(bcs);
                double[] bba = Bbox(apo);
                List<Contour> small = Transform(apo, 0, 0, 0.80, 0.80, (bba[0] + bba[2]) / 2.0, (bba[1] + bba[3]) / 2.0);
                double[] bbs = Bbox(small);
                List<Contour> placed = Transform(small, bbb[2] + 250 - bbs[0], (bbb[1] + 400) - bbs[1], 1.0, 1.0, 0, 0);
                var cs = new List<Contour>(bcs); cs.AddRange(placed);
                short newAdv = (short)(badv + (int)Math.Round(bbs[2] - bbs[0]) + 500);
                EmitChar(f, par.Target, cs, newAdv);
            }
        }

        // sort every parallel table by code (the player expects ascending order)
        SortByCode(f);
        return f.Codes.Count - originalCount;
    }

    static bool Has(Dictionary<int, int> cm, char ch) { return cm.ContainsKey((int)ch); }
    static List<Contour> CharContours(GfxFont f, Dictionary<int, int> cm, char ch) { return GetContours(f.Glyphs[cm[(int)ch]].Records); }
    static short CharWidth(GfxFont f, Dictionary<int, int> cm, char ch) { return f.Widths[cm[(int)ch]]; }

    static void EmitChar(GfxFont f, char ch, List<Contour> cs, short adv)
    {
        double[] bb = Bbox(cs);
        var novy = new Glyph();
        novy.Records = BuildRecords(cs, 1);
        novy.Nfb = f.Glyphs[0].Nfb; novy.Nlb = f.Glyphs[0].Nlb;   // same as the font's first glyph
        f.Glyphs.Add(novy);
        f.Codes.Add((int)ch);
        f.Widths.Add(adv);
        f.Bounds.Add(new int[] {
            (int)Math.Round(bb[0]), (int)Math.Round(bb[2]), (int)Math.Round(bb[1]), (int)Math.Round(bb[3]) });
    }

    static void SortByCode(GfxFont f)
    {
        int n = f.Codes.Count;
        int[] keys = new int[n]; int[] idx = new int[n];
        for (int i = 0; i < n; i++) { keys[i] = f.Codes[i]; idx[i] = i; }
        Array.Sort(keys, idx);
        var newG = new List<Glyph>(n); var newC = new List<int>(n);
        var newW = new List<short>(n); var newB = new List<int[]>(n);
        for (int i = 0; i < n; i++)
        {
            int j = idx[i];
            newG.Add(f.Glyphs[j]); newC.Add(f.Codes[j]); newW.Add(f.Widths[j]); newB.Add(f.Bounds[j]);
        }
        f.Glyphs = newG; f.Codes = newC; f.Widths = newW; f.Bounds = newB;
    }

    // -------------------------------------------------------- small helpers (byte reading/writing)
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
    static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
