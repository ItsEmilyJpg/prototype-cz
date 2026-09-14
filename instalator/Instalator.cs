// Cestina do Prototype - instalator
// Jeden .exe, data prekladu jsou v nem pribalena jako zdroje (csc /resource).
// Sestaveni: .github/workflows/release.yml
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// urovne vypisu - spolecne pro konzoli i GUI
enum Uroven { Info, Varovani, Chyba, Ok }

static class Program
{
    const string ZALOHA = "_cestina_zaloha";
    static bool dryRun = false;
    static bool guiRezim = false;

    // spolecna abstrakce vypisu/postupu - Instalovat/Odinstalovat pres ni pisi,
    // konzole i GUI si ji na zacatku prepoji na sebe
    static Action<string, Uroven> Vypis = KonzoleVypis;
    static Action<int, int> Prubeh = delegate { };

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(int dwProcessId);
    const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    // ---------------------------------------------------------------- vstup
    [STAThread]
    static int Main(string[] args)
    {
        bool cliRezim = args.Contains("--instalovat") || args.Contains("--nanecisto") || args.Contains("--odinstalovat");
        return cliRezim ? SpustCli(args) : SpustGui(args);
    }

    // konzolovy rezim - pro CI a power uzivatele, beze zmeny chovani/navratovych kodu
    static int SpustCli(string[] args)
    {
        // musi bezet pred prvnim pristupem na Console - jinak se GetStdHandle jiz zacachuje
        try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        try { Console.Title = "Čeština do Prototype " + Verze.Text; } catch { }

        dryRun = args.Contains("--nanecisto");
        bool odinstalovat = args.Contains("--odinstalovat");

        Hlavicka();

        string hra = args.FirstOrDefault(a => !a.StartsWith("--") && Directory.Exists(a));
        if (hra == null) hra = NajdiHru();
        if (hra == null) { Chyba("Nenašel jsem složku s hrou."); return Konec(2); }

        Console.WriteLine("  Hra: " + hra);
        Console.WriteLine();

        if (!dryRun && HraBezi(hra))
        {
            Chyba("Hra právě běží. Vypni ji a spusť instalátor znovu.");
            return Konec(3);
        }

        if (!dryRun && !JsemSpravce() && !MuzuZapsat(hra))
        {
            Zluta("Zápis do složky hry potřebuje práva správce. Spouštím znovu v novém okně...");
            try
            {
                var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName);
                psi.Arguments = "\"" + hra.TrimEnd('\\') + "\"" + (odinstalovat ? " --odinstalovat" : " --instalovat");
                psi.WorkingDirectory = Directory.GetCurrentDirectory();
                psi.UseShellExecute = true; psi.Verb = "runas";
                Process.Start(psi);
                return 0;
            }
            catch { Chyba("Nepodařilo se získat práva správce."); return Konec(3); }
        }

        try { return odinstalovat ? Odinstalovat(hra) : Instalovat(hra); }
        catch (Exception e) { Chyba("Neočekávaná chyba: " + e.Message); return Konec(9); }
    }

    // graficky rezim - dvojklik, bez prepinacu rezimu na prikazove radce
    static int SpustGui(string[] args)
    {
        guiRezim = true;
        try { SetProcessDPIAware(); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string predvyplnena = args.FirstOrDefault(a => !a.StartsWith("--") && Directory.Exists(a));
        if (predvyplnena == null) predvyplnena = NajdiHru();

        using (var okno = new HlavniOkno(predvyplnena))
        {
            Application.Run(okno);
        }
        return 0;
    }

    // ---------------------------------------------------------------- instalace
    static int Instalovat(string hra)
    {
        string zal = Path.Combine(hra, ZALOHA);
        if (!dryRun) Directory.CreateDirectory(zal);

        Vypis("[1/5] Kontrola souborů hry...", Uroven.Info);
        Prubeh(1, 5);
        var ukoly = new List<Ukol>();
        ukoly.AddRange(UkolyFonty(hra));
        ukoly.AddRange(UkolyTexty(hra, "hud.tsv",     Path.Combine("art", "hud"), "menu a HUD"));
        ukoly.AddRange(UkolyTexty(hra, "mezihry.tsv", Path.Combine("art", "nis"), "mezihry"));
        ukoly.AddRange(UkolyTexty(hra, "filmy.tsv",   "movies", "filmečky"));
        ukoly.AddRange(UkolyTitulky(hra, "titulky.tsv"));

        int chybi = ukoly.Count(u => u.Chybi);
        var prace = ukoly.Where(u => !u.Chybi).ToList();
        if (prace.Count == 0) { Vypis("Nenašel jsem ve hře žádný soubor k překladu.", Uroven.Chyba); return Konec(4); }
        Vypis(string.Format("      {0} souborů k úpravě{1}", prace.Count,
            chybi > 0 ? ", " + chybi + " ve hře není" : ""), Uroven.Info);

        Vypis("", Uroven.Info);
        Vypis("[2/5] Autotest — ověřuji, že souborům rozumím...", Uroven.Info);
        Prubeh(2, 5);
        int hotovo = 0, selhalo = 0;
        var pripravene = new List<Hotovy>();
        var kategorie = new List<string>();
        var zmen = new Dictionary<string, int>();
        var stejne = new Dictionary<string, int>();
        foreach (var u in prace)
        {
            if (!zmen.ContainsKey(u.Kategorie)) { kategorie.Add(u.Kategorie); zmen[u.Kategorie] = 0; stejne[u.Kategorie] = 0; }
            try
            {
                byte[] puvodni = File.ReadAllBytes(u.Cesta);
                byte[] novy = u.Uprav(puvodni);
                if (novy == null || Stejne(novy, puvodni)) stejne[u.Kategorie]++;
                else
                {
                    pripravene.Add(new Hotovy { Cesta = u.Cesta, Data = novy, Jmeno = u.Jmeno });
                    zmen[u.Kategorie]++;
                }
            }
            catch (Exception e)
            {
                selhalo++;
                if (selhalo <= 5) Vypis("      " + u.Jmeno + ": " + e.Message, Uroven.Chyba);
            }
            hotovo++;
            if (hotovo % 50 == 0 || hotovo == prace.Count) Prubeh(hotovo, prace.Count);
            if (hotovo % 1500 == 0) Vypis(string.Format("      ... {0}/{1}", hotovo, prace.Count), Uroven.Info);
        }
        if (selhalo > 0)
        {
            Vypis("Autotest neprošel u " + selhalo + " souborů. NIC JSEM NEZAPSAL.", Uroven.Chyba);
            Vypis("Nejspíš máš jinou verzi hry, než pro kterou je čeština určená.", Uroven.Chyba);
            return Konec(5);
        }
        foreach (string k in kategorie)
            Vypis(string.Format("      {0,-12} {1,6} k přeložení, {2,6} už přeloženo", k, zmen[k], stejne[k]), Uroven.Info);
        Vypis(string.Format("      V pořádku, {0} souborů připraveno.", pripravene.Count), Uroven.Info);

        Vypis("", Uroven.Info);
        if (dryRun)
        {
            Vypis("[nanečisto] Všechno prošlo, instalace by proběhla v pořádku. Nic jsem nezapsal.", Uroven.Ok);
            return Konec(0);
        }
        if (pripravene.Count == 0)
        {
            Vypis("Čeština už je nainstalovaná, není co měnit.", Uroven.Ok);
            return Konec(0);
        }

        Vypis("[3/5] Záloha originálů do " + ZALOHA + "...", Uroven.Info);
        Prubeh(3, 5);
        int zalohovano = 0;
        foreach (var h in pripravene)
        {
            string cil = Path.Combine(zal, RelativniCesta(hra, h.Cesta));
            if (File.Exists(cil)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(cil));
            File.Copy(h.Cesta, cil);
            zalohovano++;
        }
        Vypis(string.Format("      Zálohováno {0} souborů.", zalohovano), Uroven.Info);

        Vypis("", Uroven.Info);
        Vypis("[4/5] Zápis...", Uroven.Info);
        Prubeh(4, 5);
        int zapsano = 0;
        foreach (var h in pripravene)
        {
            File.WriteAllBytes(h.Cesta, h.Data);
            zapsano++;
            if (zapsano % 50 == 0 || zapsano == pripravene.Count) Prubeh(zapsano, pripravene.Count);
            if (zapsano % 3000 == 0) Vypis(string.Format("      ... {0}/{1}", zapsano, pripravene.Count), Uroven.Info);
        }
        Vypis(string.Format("      Zapsáno {0} souborů.", zapsano), Uroven.Info);

        Vypis("", Uroven.Info);
        Vypis("[5/5] Kontrola po zápisu...", Uroven.Info);
        Prubeh(5, 5);
        int spatne = 0;
        foreach (var h in pripravene)
            if (!Stejne(File.ReadAllBytes(h.Cesta), h.Data)) spatne++;
        if (spatne > 0) { Vypis("Kontrola našla " + spatne + " neshod.", Uroven.Chyba); return Konec(6); }
        Vypis("      Sedí.", Uroven.Info);

        Vypis("", Uroven.Info);
        Vypis("HOTOVO. Čeština je nainstalovaná.", Uroven.Ok);
        Vypis("", Uroven.Info);
        Vypis("  Spusť hru. Jazyk hry musí být nastavený na angličtinu —", Uroven.Info);
        Vypis("  čeština nahrazuje anglickou větev.", Uroven.Info);
        Vypis("", Uroven.Info);
        Vypis("  Originály jsou v " + zal, Uroven.Info);
        Vypis("  Vrátit zpátky: spusť instalátor znovu a vyber Odinstalovat.", Uroven.Info);
        return Konec(0);
    }

    static int Odinstalovat(string hra)
    {
        string zal = Path.Combine(hra, ZALOHA);
        if (!Directory.Exists(zal)) { Vypis("Složka se zálohou neexistuje: " + zal, Uroven.Chyba); return Konec(4); }
        var soubory = Directory.GetFiles(zal, "*", SearchOption.AllDirectories);
        int n = 0;
        foreach (string f in soubory)
        {
            string rel = f.Substring(zal.Length).TrimStart('\\', '/');
            string cil = Path.Combine(hra, rel);
            // starsi zalohy z cz_fonty.py lezi naplocho, patri do art\hud
            if (rel.IndexOf('\\') < 0 && !File.Exists(cil))
            {
                string hud = Path.Combine(hra, "art", "hud", rel);
                if (File.Exists(hud)) cil = hud;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(cil));
            File.Copy(f, cil, true); n++;
            if (n % 25 == 0 || n == soubory.Length) Prubeh(n, soubory.Length);
        }
        Vypis("Vráceno " + n + " původních souborů.", Uroven.Ok);
        return Konec(0);
    }

    // ---------------------------------------------------------------- ukoly
    class Ukol { public string Cesta, Jmeno, Kategorie; public bool Chybi; public Func<byte[], byte[]> Uprav; }
    class Hotovy { public string Cesta, Jmeno; public byte[] Data; }

    static readonly string[] FONTY = { "advfont.gfx", "basicfont.gfx", "fontexport.gfx", "fonts_latin.gfx", "gfxfontlib.gfx" };

    static IEnumerable<Ukol> UkolyFonty(string hra)
    {
        foreach (string jm in FONTY)
        {
            string cil = Path.Combine(hra, "art", "hud", jm);
            // overovaci rozdily nejsou soucasti verejneho zdrojaku, bez nich se jen neporovnava
            byte[] delta = Data.Je(jm + ".delta") ? Data.Bajty(jm + ".delta") : null;
            yield return new Ukol
            {
                Cesta = cil, Jmeno = jm, Kategorie = "fonty", Chybi = !File.Exists(cil),
                Uprav = src => UpravFont(src, delta)
            };
        }
    }

    // Fonty se upravuji podle struktury, takze funguji i na jinych verzich hry.
    // Kdyz je to presne Steam verze, pro kterou mame overeny rozdil, musi oba vysledky sedet.
    static byte[] UpravFont(byte[] src, byte[] delta)
    {
        byte[] novy = Fonty.Prepis(src);
        if (delta != null && Delta.JeZdroj(src, delta))
        {
            byte[] overeny = Delta.Pouzij(src, delta);
            if (novy == null || !Stejne(novy, overeny)) throw new Exception("úprava fontu nesouhlasí s ověřenou verzí");
        }
        return novy;
    }

    static IEnumerable<Ukol> UkolyTexty(string hra, string dat, string podslozka, string kategorie)
    {
        var podle = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Tsv.Cti(Data.Bajty(dat)))
        {
            Dictionary<string, string> d;
            if (!podle.TryGetValue(r.Item1, out d)) { d = new Dictionary<string, string>(); podle[r.Item1] = d; }
            d[r.Item2] = r.Item3;
        }
        foreach (var kv in podle)
        {
            string cil = Path.Combine(hra, podslozka, kv.Key.Replace('/', '\\'));
            var mapa = kv.Value;
            yield return new Ukol
            {
                Cesta = cil, Jmeno = kv.Key, Kategorie = kategorie, Chybi = !File.Exists(cil),
                Uprav = src => P3d.PrepisTextbible(src, mapa)
            };
        }
    }

    static IEnumerable<Ukol> UkolyTitulky(string hra, string dat)
    {
        string korenu = Path.Combine(hra, "audio", "english", "AudioFile");
        foreach (var r in Tsv.Cti(Data.Bajty(dat)))
        {
            string cil = Path.Combine(korenu, r.Item1.Replace('/', '\\'));
            string text = r.Item3;
            yield return new Ukol
            {
                Cesta = cil, Jmeno = r.Item1, Kategorie = "titulky", Chybi = !File.Exists(cil),
                Uprav = src => P3d.PrepisTitulek(src, text)
            };
        }
    }

    // ---------------------------------------------------------------- hledani hry
    static readonly string[] VychoziCesty = {
        @"C:\Program Files (x86)\Steam\steamapps\common\Prototype",
        @"C:\Program Files\Steam\steamapps\common\Prototype",
    };

    static bool Vypada(string p)
    {
        return Directory.Exists(Path.Combine(p, "art")) || Directory.Exists(Path.Combine(p, "audio"));
    }

    // presnejsi kontrola pro GUI (zivotni validace zadane cesty)
    static bool VypadaJakoHraPresne(string p)
    {
        try
        {
            if (File.Exists(Path.Combine(p, "prototypef.exe"))) return true;
            return Directory.Exists(Path.Combine(p, "art")) && Directory.Exists(Path.Combine(p, "audio"));
        }
        catch { return false; }
    }

    static string NajdiHru()
    {
        foreach (string p in VychoziCesty) if (Vypada(p)) return p;
        // knihovny Steamu
        var steamy = new List<string> { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" };
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                string sp = k == null ? null : k.GetValue("SteamPath") as string;
                if (sp != null) steamy.Insert(0, sp.Replace('/', '\\'));
            }
        }
        catch { }
        foreach (string steam in steamy)
        {
            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (string radek in File.ReadAllLines(vdf))
            {
                int a = radek.IndexOf("\"path\"");
                if (a < 0) continue;
                int b = radek.IndexOf('"', a + 6); if (b < 0) continue;
                int c = radek.IndexOf('"', b + 1); if (c < 0) continue;
                string cesta = radek.Substring(b + 1, c - b - 1).Replace("\\\\", "\\");
                string kand = Path.Combine(cesta, "steamapps", "common", "Prototype");
                if (Vypada(kand)) return kand;
            }
        }
        foreach (var d in DriveInfo.GetDrives())
        {
            if (!d.IsReady) continue;
            string kand = Path.Combine(d.Name, "SteamLibrary", "steamapps", "common", "Prototype");
            try { if (Vypada(kand)) return kand; } catch { }
        }
        return null;
    }

    static string RelativniCesta(string koren, string plna)
    {
        string k = koren.TrimEnd('\\') + "\\";
        return plna.StartsWith(k, StringComparison.OrdinalIgnoreCase)
            ? plna.Substring(k.Length) : Path.GetFileName(plna);
    }

    static bool MuzuZapsat(string hra)
    {
        try
        {
            foreach (string d in new[] { hra, Path.Combine(hra, "art", "hud"), Path.Combine(hra, "audio", "english", "AudioFile") })
            {
                if (!Directory.Exists(d)) continue;
                string t = Path.Combine(d, "_cestina_test.tmp");
                File.WriteAllBytes(t, new byte[1]);
                File.Delete(t);
            }
            return true;
        }
        catch { return false; }
    }

    static bool HraBezi(string hra)
    {
        string k = Path.GetFullPath(hra).TrimEnd('\\') + "\\";
        foreach (var p in Process.GetProcessesByName("prototypef"))
        {
            try
            {
                if (p.MainModule.FileName.StartsWith(k, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { return true; }   // cestu nejde zjistit, radsi predpokladej, ze je to ona
        }
        return false;
    }

    static bool Stejne(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    static bool JsemSpravce()
    {
        try
        {
            var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- vypis
    static void Hlavicka()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine();
        Console.WriteLine("  ČEŠTINA DO PROTOTYPE " + Verze.Text);
        Console.ResetColor();
        Console.WriteLine("  menu, mise, cutscény i titulky mluvených replik");
        Console.WriteLine("  ────────────────────────────────────────────────");
        Console.WriteLine();
    }
    static void Chyba(string s) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine(s); Console.ResetColor(); }
    static void Zluta(string s) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(s); Console.ResetColor(); }
    static void Zelena(string s) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine(s); Console.ResetColor(); }

    // vychozi implementace Vypis pro konzolovy rezim
    static void KonzoleVypis(string s, Uroven u)
    {
        if (u == Uroven.Chyba) Chyba(s);
        else if (u == Uroven.Varovani) Zluta(s);
        else if (u == Uroven.Ok) Zelena(s);
        else Console.WriteLine(s);
    }

    // "Zmáčkni Enter" jen v interaktivni konzoli, nikdy v CI/presmerovanem vstupu a nikdy v GUI
    static int Konec(int kod)
    {
        if (!guiRezim)
        {
            try
            {
                if (!Console.IsOutputRedirected && !Console.IsInputRedirected)
                {
                    Console.WriteLine();
                    Console.WriteLine("Zmáčkni Enter pro zavření.");
                    Console.ReadLine();
                }
            }
            catch { }
        }
        return kod;
    }

    // ---------------------------------------------------------------- GUI
    class HlavniOkno : Form
    {
        readonly TextBox txtSlozka;
        readonly Button btnProchazet, btnInstalovat, btnZkouska, btnOdinstalovat;
        readonly Label lblStav, lblHint;
        readonly ProgressBar progress;
        readonly RichTextBox txtLog;
        volatile bool bezi;

        public HlavniOkno(string pocatecniCesta)
        {
            Text = "Čeština do Prototype " + Verze.Text;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(560, 480);
            Size = new Size(640, 560);
            AllowDrop = true;

            var lblNadpis = new Label
            {
                Text = "ČEŠTINA DO PROTOTYPE " + Verze.Text,
                Font = new Font(Font.FontFamily, Font.Size + 3, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(12, 10)
            };
            var lblPopis = new Label
            {
                Text = "menu, mise, cutscény i titulky mluvených replik",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Location = new Point(14, 36)
            };

            var lblSlozka = new Label { Text = "Složka hry:", AutoSize = true, Location = new Point(14, 66) };
            txtSlozka = new TextBox
            {
                Text = pocatecniCesta ?? "",
                Location = new Point(14, 84),
                Width = 420,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                AllowDrop = true
            };
            btnProchazet = new Button { Text = "Procházet…", Location = new Point(440, 82), Width = 100, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            lblStav = new Label { AutoSize = true, Location = new Point(14, 110) };

            btnInstalovat = new Button { Text = "Nainstalovat", Location = new Point(14, 140), Width = 150 };
            btnZkouska = new Button { Text = "Vyzkoušet nanečisto", Location = new Point(172, 140), Width = 170 };
            btnOdinstalovat = new Button { Text = "Odinstalovat", Location = new Point(350, 140), Width = 150 };

            progress = new ProgressBar { Location = new Point(14, 176), Height = 20, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };

            txtLog = new RichTextBox
            {
                Location = new Point(14, 204),
                ReadOnly = true,
                BackColor = Color.White,
                Font = new Font(FontFamily.GenericMonospace, 8.5F),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            lblHint = new Label
            {
                Text = "Hra musí být zavřená a jazyk hry nastavený na angličtinu.",
                AutoSize = true,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };

            Controls.AddRange(new Control[] {
                lblNadpis, lblPopis, lblSlozka, txtSlozka, btnProchazet, lblStav,
                btnInstalovat, btnZkouska, btnOdinstalovat, progress, txtLog, lblHint
            });

            Resize += delegate { Rozvrzeni(); };
            Rozvrzeni();

            txtSlozka.TextChanged += delegate { Validuj(); };
            btnProchazet.Click += BtnProchazet_Click;
            btnInstalovat.Click += delegate { Spustit(false, false); };
            btnZkouska.Click += delegate { Spustit(true, false); };
            btnOdinstalovat.Click += delegate { Spustit(false, true); };

            DragEnter += Okno_DragEnter;
            DragDrop += Okno_DragDrop;
            txtSlozka.DragEnter += Okno_DragEnter;
            txtSlozka.DragDrop += Okno_DragDrop;
            FormClosing += Okno_FormClosing;

            Validuj();
            if (string.IsNullOrEmpty(pocatecniCesta))
                GuiVypis("Nenašel jsem složku s hrou automaticky. Vyber ji tlačítkem Procházet, nebo ji přetáhni do okna.", Uroven.Varovani);
        }

        void Rozvrzeni()
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            btnProchazet.Location = new Point(w - 14 - btnProchazet.Width, 82);
            txtSlozka.Width = btnProchazet.Left - 24 - txtSlozka.Left;
            progress.Width = w - 28;
            lblHint.Location = new Point(14, h - 24);
            txtLog.Location = new Point(14, 204);
            txtLog.Size = new Size(w - 28, Math.Max(60, lblHint.Top - 10 - txtLog.Top));
        }

        void BtnProchazet_Click(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                string zac = txtSlozka.Text.Trim().Trim('"');
                if (Directory.Exists(zac)) dlg.SelectedPath = zac;
                dlg.Description = "Vyber složku hry Prototype";
                if (dlg.ShowDialog(this) == DialogResult.OK) txtSlozka.Text = dlg.SelectedPath;
            }
        }

        void Okno_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var cesty = (string[])e.Data.GetData(DataFormats.FileDrop);
                e.Effect = (cesty != null && cesty.Length > 0 && Directory.Exists(cesty[0])) ? DragDropEffects.Copy : DragDropEffects.None;
            }
        }

        void Okno_DragDrop(object sender, DragEventArgs e)
        {
            var cesty = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (cesty != null && cesty.Length > 0 && Directory.Exists(cesty[0])) txtSlozka.Text = cesty[0];
        }

        void Okno_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (bezi)
            {
                DialogResult r = MessageBox.Show(this, "Instalace ještě běží. Opravdu zavřít?",
                    "Čeština do Prototype", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) e.Cancel = true;
            }
        }

        void Validuj()
        {
            string p = txtSlozka.Text.Trim().Trim('"');
            bool zadano = p.Length > 0;
            bool ok = zadano && Directory.Exists(p) && VypadaJakoHraPresne(p);
            if (!zadano)
            {
                lblStav.Text = "Zadej složku hry, nebo ji sem přetáhni.";
                lblStav.ForeColor = SystemColors.GrayText;
            }
            else if (ok)
            {
                lblStav.Text = "Hra nalezena";
                lblStav.ForeColor = Color.Green;
            }
            else
            {
                lblStav.Text = "Tohle nevypadá jako složka hry Prototype";
                lblStav.ForeColor = Color.Firebrick;
            }
            bool zaloha = ok && Directory.Exists(Path.Combine(p, ZALOHA));
            btnInstalovat.Enabled = ok && !bezi;
            btnZkouska.Enabled = ok && !bezi;
            btnOdinstalovat.Enabled = ok && zaloha && !bezi;
            txtSlozka.Enabled = !bezi;
            btnProchazet.Enabled = !bezi;
        }

        void Spustit(bool dry, bool odinstalovat)
        {
            string hra = txtSlozka.Text.Trim().Trim('"');
            if (!Directory.Exists(hra) || !VypadaJakoHraPresne(hra))
            {
                MessageBox.Show(this, "Tohle nevypadá jako složka hry Prototype.", "Čeština do Prototype",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!dry)
            {
                if (HraBezi(hra))
                {
                    MessageBox.Show(this, "Hra právě běží. Vypni ji a zkus to znovu.", "Čeština do Prototype",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                if (!JsemSpravce() && !MuzuZapsat(hra))
                {
                    DialogResult r = MessageBox.Show(this,
                        "Zápis do složky hry potřebuje práva správce.\nSpustit instalátor znovu jako správce?",
                        "Čeština do Prototype", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (r == DialogResult.Yes)
                    {
                        try
                        {
                            var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName);
                            psi.Arguments = "--gui \"" + hra.TrimEnd('\\') + "\"";
                            psi.UseShellExecute = true; psi.Verb = "runas";
                            Process.Start(psi);
                            Close();
                        }
                        catch
                        {
                            MessageBox.Show(this, "Nepodařilo se získat práva správce.", "Čeština do Prototype",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                    return;
                }
            }

            bezi = true;
            Validuj();
            progress.Value = 0;
            txtLog.Clear();
            dryRun = dry;
            Vypis = GuiVypis;
            Prubeh = GuiPrubeh;

            var vlakno = new Thread(delegate ()
            {
                int kod = 9;
                try { kod = odinstalovat ? Odinstalovat(hra) : Instalovat(hra); }
                catch (Exception e) { GuiVypis("Neočekávaná chyba: " + e.Message, Uroven.Chyba); }
                int vysledek = kod;
                try
                {
                    Invoke(new Action(delegate
                    {
                        bezi = false;
                        Validuj();
                        Dokonceno(vysledek, dry, odinstalovat);
                    }));
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            });
            vlakno.IsBackground = true;
            vlakno.Start();
        }

        void Dokonceno(int kod, bool dry, bool odinstalovat)
        {
            if (kod == 0)
            {
                if (dry)
                    MessageBox.Show(this, "Zkouška nanečisto proběhla v pořádku, nic se nezapisovalo.",
                        "Hotovo", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else if (odinstalovat)
                    MessageBox.Show(this, "Původní soubory byly vráceny.",
                        "Hotovo", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show(this, "Hotovo, čeština je nainstalovaná. Jazyk hry musí být nastavený na angličtinu.",
                        "Hotovo", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(this, "Něco se nepovedlo (kód " + kod + "). Podrobnosti jsou v logu výše.",
                    "Chyba", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void GuiVypis(string text, Uroven u)
        {
            if (InvokeRequired) { try { Invoke(new Action<string, Uroven>(GuiVypis), text, u); } catch { } return; }
            Color barva;
            if (u == Uroven.Chyba) barva = Color.Firebrick;
            else if (u == Uroven.Varovani) barva = Color.DarkGoldenrod;
            else if (u == Uroven.Ok) barva = Color.SeaGreen;
            else barva = txtLog.ForeColor;
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.SelectionLength = 0;
            txtLog.SelectionColor = barva;
            txtLog.AppendText(text + Environment.NewLine);
            txtLog.ScrollToCaret();
        }

        void GuiPrubeh(int aktual, int celkem)
        {
            if (InvokeRequired) { try { Invoke(new Action<int, int>(GuiPrubeh), aktual, celkem); } catch { } return; }
            if (celkem <= 0) return;
            progress.Maximum = celkem;
            progress.Value = Math.Max(0, Math.Min(aktual, celkem));
        }
    }
}

// ---------------------------------------------------------------- pribalena data
static class Data
{
    static readonly Assembly A = typeof(Data).Assembly;

    public static bool Je(string jmeno)
    {
        return A.GetManifestResourceNames().Contains(jmeno);
    }

    public static byte[] Bajty(string jmeno)
    {
        using (var s = A.GetManifestResourceStream(jmeno))
        {
            if (s == null) throw new Exception("v instalátoru chybí data: " + jmeno);
            var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
