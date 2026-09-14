// Czech translation for Prototype - installer
// One .exe, the translation data is embedded in it as resources (csc /resource).
// Build: .github/workflows/release.yml
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

// log levels - shared by the console and the GUI
enum LogLevel { Info, Warning, Error, Ok }

static class Program
{
    const string BACKUP_FOLDER = "_cestina_zaloha";
    static bool dryRun = false;
    static bool guiMode = false;

    // shared logging/progress abstraction - Install/Uninstall write through it,
    // the console and the GUI each wire it up to themselves at startup
    static Action<string, LogLevel> Log = ConsoleLog;
    static Action<int, int> Progress = delegate { };

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(int dwProcessId);
    const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    // ---------------------------------------------------------------- entry point
    [STAThread]
    static int Main(string[] args)
    {
        bool cliMode = args.Contains("--instalovat") || args.Contains("--install")
            || args.Contains("--nanecisto") || args.Contains("--dry-run")
            || args.Contains("--odinstalovat") || args.Contains("--uninstall");
        return cliMode ? RunCli(args) : RunGui(args);
    }

    // console mode - for CI and power users, behavior/exit codes must not change
    static int RunCli(string[] args)
    {
        // must run before the first access to Console - otherwise GetStdHandle gets cached already
        try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        try { Console.Title = "Čeština do Prototype " + AppVersion.Text; } catch { }

        dryRun = args.Contains("--nanecisto") || args.Contains("--dry-run");
        bool uninstall = args.Contains("--odinstalovat") || args.Contains("--uninstall");

        PrintHeader();

        string game = args.FirstOrDefault(a => !a.StartsWith("--") && Directory.Exists(a));
        if (game == null) game = FindGame();
        if (game == null) { Error("Nenašel jsem složku s hrou."); return Finish(2); }

        Console.WriteLine("  Hra: " + game);
        Console.WriteLine();

        if (!dryRun && GameRunning(game))
        {
            Error("Hra právě běží. Vypni ji a spusť instalátor znovu.");
            return Finish(3);
        }

        if (!dryRun && !IsAdmin() && !CanWrite(game))
        {
            Warning("Zápis do složky hry potřebuje práva správce. Spouštím znovu v novém okně...");
            try
            {
                var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName);
                psi.Arguments = "\"" + game.TrimEnd('\\') + "\"" + (uninstall ? " --odinstalovat" : " --instalovat");
                psi.WorkingDirectory = Directory.GetCurrentDirectory();
                psi.UseShellExecute = true; psi.Verb = "runas";
                Process.Start(psi);
                return 0;
            }
            catch { Error("Nepodařilo se získat práva správce."); return Finish(3); }
        }

        try { return uninstall ? Uninstall(game) : Install(game); }
        catch (Exception e) { Error("Neočekávaná chyba: " + e.Message); return Finish(9); }
    }

    // graphical mode - double-click, no mode switches on the command line
    static int RunGui(string[] args)
    {
        guiMode = true;
        try { SetProcessDPIAware(); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string prefilled = args.FirstOrDefault(a => !a.StartsWith("--") && Directory.Exists(a));
        if (prefilled == null) prefilled = FindGame();

        using (var window = new MainWindow(prefilled))
        {
            Application.Run(window);
        }
        return 0;
    }

    // ---------------------------------------------------------------- installation
    static int Install(string game)
    {
        string backupDir = Path.Combine(game, BACKUP_FOLDER);
        if (!dryRun) Directory.CreateDirectory(backupDir);

        Log("[1/5] Kontrola souborů hry...", LogLevel.Info);
        Progress(1, 5);
        var tasks = new List<FileTask>();
        tasks.AddRange(FontTasks(game));
        tasks.AddRange(TextTasks(game, "hud.tsv",     Path.Combine("art", "hud"), "menu a HUD"));
        tasks.AddRange(TextTasks(game, "mezihry.tsv", Path.Combine("art", "nis"), "mezihry"));
        tasks.AddRange(TextTasks(game, "filmy.tsv",   "movies", "filmečky"));
        tasks.AddRange(SubtitleTasks(game, "titulky.tsv"));

        int missing = tasks.Count(u => u.Missing);
        var work = tasks.Where(u => !u.Missing).ToList();
        if (work.Count == 0) { Log("Nenašel jsem ve hře žádný soubor k překladu.", LogLevel.Error); return Finish(4); }
        Log(string.Format("      {0} souborů k úpravě{1}", work.Count,
            missing > 0 ? ", " + missing + " ve hře není" : ""), LogLevel.Info);

        Log("", LogLevel.Info);
        Log("[2/5] Autotest — ověřuji, že souborům rozumím...", LogLevel.Info);
        Progress(2, 5);
        int done = 0, failed = 0;
        var prepared = new List<PreparedFile>();
        var categories = new List<string>();
        var changed = new Dictionary<string, int>();
        var unchanged = new Dictionary<string, int>();
        foreach (var task in work)
        {
            if (!changed.ContainsKey(task.Category)) { categories.Add(task.Category); changed[task.Category] = 0; unchanged[task.Category] = 0; }
            try
            {
                byte[] original = File.ReadAllBytes(task.FilePath);
                byte[] updated = task.Apply(original);
                if (updated == null || BytesEqual(updated, original)) unchanged[task.Category]++;
                else
                {
                    prepared.Add(new PreparedFile { FilePath = task.FilePath, Data = updated, Name = task.Name });
                    changed[task.Category]++;
                }
            }
            catch (Exception e)
            {
                failed++;
                if (failed <= 5) Log("      " + task.Name + ": " + e.Message, LogLevel.Error);
            }
            done++;
            if (done % 50 == 0 || done == work.Count) Progress(done, work.Count);
            if (done % 1500 == 0) Log(string.Format("      ... {0}/{1}", done, work.Count), LogLevel.Info);
        }
        if (failed > 0)
        {
            Log("Autotest neprošel u " + failed + " souborů. NIC JSEM NEZAPSAL.", LogLevel.Error);
            Log("Nejspíš máš jinou verzi hry, než pro kterou je čeština určená.", LogLevel.Error);
            return Finish(5);
        }
        foreach (string cat in categories)
            Log(string.Format("      {0,-12} {1,6} k přeložení, {2,6} už přeloženo", cat, changed[cat], unchanged[cat]), LogLevel.Info);
        Log(string.Format("      V pořádku, {0} souborů připraveno.", prepared.Count), LogLevel.Info);

        Log("", LogLevel.Info);
        if (dryRun)
        {
            Log("[nanečisto] Všechno prošlo, instalace by proběhla v pořádku. Nic jsem nezapsal.", LogLevel.Ok);
            return Finish(0);
        }
        if (prepared.Count == 0)
        {
            Log("Čeština už je nainstalovaná, není co měnit.", LogLevel.Ok);
            return Finish(0);
        }

        Log("[3/5] Záloha originálů do " + BACKUP_FOLDER + "...", LogLevel.Info);
        Progress(3, 5);
        int backedUp = 0;
        foreach (var file in prepared)
        {
            string dest = Path.Combine(backupDir, RelativePath(game, file.FilePath));
            if (File.Exists(dest)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            File.Copy(file.FilePath, dest);
            backedUp++;
        }
        Log(string.Format("      Zálohováno {0} souborů.", backedUp), LogLevel.Info);

        Log("", LogLevel.Info);
        Log("[4/5] Zápis...", LogLevel.Info);
        Progress(4, 5);
        int written = 0;
        foreach (var file in prepared)
        {
            File.WriteAllBytes(file.FilePath, file.Data);
            written++;
            if (written % 50 == 0 || written == prepared.Count) Progress(written, prepared.Count);
            if (written % 3000 == 0) Log(string.Format("      ... {0}/{1}", written, prepared.Count), LogLevel.Info);
        }
        Log(string.Format("      Zapsáno {0} souborů.", written), LogLevel.Info);

        Log("", LogLevel.Info);
        Log("[5/5] Kontrola po zápisu...", LogLevel.Info);
        Progress(5, 5);
        int mismatches = 0;
        foreach (var file in prepared)
            if (!BytesEqual(File.ReadAllBytes(file.FilePath), file.Data)) mismatches++;
        if (mismatches > 0) { Log("Kontrola našla " + mismatches + " neshod.", LogLevel.Error); return Finish(6); }
        Log("      Sedí.", LogLevel.Info);

        Log("", LogLevel.Info);
        Log("HOTOVO. Čeština je nainstalovaná.", LogLevel.Ok);
        Log("", LogLevel.Info);
        Log("  Spusť hru. Jazyk hry musí být nastavený na angličtinu —", LogLevel.Info);
        Log("  čeština nahrazuje anglickou větev.", LogLevel.Info);
        Log("", LogLevel.Info);
        Log("  Originály jsou v " + backupDir, LogLevel.Info);
        Log("  Vrátit zpátky: spusť instalátor znovu a vyber Odinstalovat.", LogLevel.Info);
        return Finish(0);
    }

    static int Uninstall(string game)
    {
        string backupDir = Path.Combine(game, BACKUP_FOLDER);
        if (!Directory.Exists(backupDir)) { Log("Složka se zálohou neexistuje: " + backupDir, LogLevel.Error); return Finish(4); }
        var files = Directory.GetFiles(backupDir, "*", SearchOption.AllDirectories);
        int n = 0;
        foreach (string file in files)
        {
            string rel = file.Substring(backupDir.Length).TrimStart('\\', '/');
            string dest = Path.Combine(game, rel);
            // older backups from cz_fonty.py sit flat, they belong under art\hud
            if (rel.IndexOf('\\') < 0 && !File.Exists(dest))
            {
                string legacyHud = Path.Combine(game, "art", "hud", rel);
                if (File.Exists(legacyHud)) dest = legacyHud;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            File.Copy(file, dest, true); n++;
            if (n % 25 == 0 || n == files.Length) Progress(n, files.Length);
        }
        Log("Vráceno " + n + " původních souborů.", LogLevel.Ok);
        return Finish(0);
    }

    // ---------------------------------------------------------------- tasks
    class FileTask { public string FilePath, Name, Category; public bool Missing; public Func<byte[], byte[]> Apply; }
    class PreparedFile { public string FilePath, Name; public byte[] Data; }

    static readonly string[] FONT_FILES = { "advfont.gfx", "basicfont.gfx", "fontexport.gfx", "fonts_latin.gfx", "gfxfontlib.gfx" };

    static IEnumerable<FileTask> FontTasks(string game)
    {
        foreach (string name in FONT_FILES)
        {
            string target = Path.Combine(game, "art", "hud", name);
            // verification deltas are not part of the public source tree, without them it just skips comparison
            byte[] delta = EmbeddedData.Exists(name + ".delta") ? EmbeddedData.Read(name + ".delta") : null;
            yield return new FileTask
            {
                FilePath = target, Name = name, Category = "fonty", Missing = !File.Exists(target),
                Apply = src => PatchFont(src, delta)
            };
        }
    }

    // Fonts are patched based on their structure, so this also works on other game versions.
    // When it's exactly the Steam version we have a verified delta for, both results must match.
    static byte[] PatchFont(byte[] src, byte[] delta)
    {
        byte[] updated = FontPatcher.Patch(src);
        if (delta != null && Delta.MatchesSource(src, delta))
        {
            byte[] verified = Delta.Apply(src, delta);
            if (updated == null || !BytesEqual(updated, verified)) throw new Exception("úprava fontu nesouhlasí s ověřenou verzí");
        }
        return updated;
    }

    static IEnumerable<FileTask> TextTasks(string game, string resourceName, string subfolder, string category)
    {
        var byFile = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Tsv.Read(EmbeddedData.Read(resourceName)))
        {
            Dictionary<string, string> d;
            if (!byFile.TryGetValue(r.Item1, out d)) { d = new Dictionary<string, string>(); byFile[r.Item1] = d; }
            d[r.Item2] = r.Item3;
        }
        foreach (var kv in byFile)
        {
            string target = Path.Combine(game, subfolder, kv.Key.Replace('/', '\\'));
            var map = kv.Value;
            yield return new FileTask
            {
                FilePath = target, Name = kv.Key, Category = category, Missing = !File.Exists(target),
                Apply = src => P3d.RewriteTextBible(src, map)
            };
        }
    }

    static IEnumerable<FileTask> SubtitleTasks(string game, string resourceName)
    {
        string root = Path.Combine(game, "audio", "english", "AudioFile");
        foreach (var r in Tsv.Read(EmbeddedData.Read(resourceName)))
        {
            string target = Path.Combine(root, r.Item1.Replace('/', '\\'));
            string text = r.Item3;
            yield return new FileTask
            {
                FilePath = target, Name = r.Item1, Category = "titulky", Missing = !File.Exists(target),
                Apply = src => P3d.RewriteSubtitle(src, text)
            };
        }
    }

    // ---------------------------------------------------------------- game detection
    static readonly string[] DefaultPaths = {
        @"C:\Program Files (x86)\Steam\steamapps\common\Prototype",
        @"C:\Program Files\Steam\steamapps\common\Prototype",
    };

    static bool LooksLikeGame(string p)
    {
        return Directory.Exists(Path.Combine(p, "art")) || Directory.Exists(Path.Combine(p, "audio"));
    }

    // stricter check for the GUI (live validation of the entered path)
    static bool LooksLikeGameExact(string p)
    {
        try
        {
            if (File.Exists(Path.Combine(p, "prototypef.exe"))) return true;
            return Directory.Exists(Path.Combine(p, "art")) && Directory.Exists(Path.Combine(p, "audio"));
        }
        catch { return false; }
    }

    static string FindGame()
    {
        foreach (string p in DefaultPaths) if (LooksLikeGame(p)) return p;
        // Steam libraries
        var steamDirs = new List<string> { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" };
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                string steamPath = key == null ? null : key.GetValue("SteamPath") as string;
                if (steamPath != null) steamDirs.Insert(0, steamPath.Replace('/', '\\'));
            }
        }
        catch { }
        foreach (string steam in steamDirs)
        {
            string vdfPath = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdfPath)) continue;
            foreach (string line in File.ReadAllLines(vdfPath))
            {
                int a = line.IndexOf("\"path\"");
                if (a < 0) continue;
                int b = line.IndexOf('"', a + 6); if (b < 0) continue;
                int c = line.IndexOf('"', b + 1); if (c < 0) continue;
                string path = line.Substring(b + 1, c - b - 1).Replace("\\\\", "\\");
                string candidate = Path.Combine(path, "steamapps", "common", "Prototype");
                if (LooksLikeGame(candidate)) return candidate;
            }
        }
        foreach (var d in DriveInfo.GetDrives())
        {
            if (!d.IsReady) continue;
            string candidate = Path.Combine(d.Name, "SteamLibrary", "steamapps", "common", "Prototype");
            try { if (LooksLikeGame(candidate)) return candidate; } catch { }
        }
        return null;
    }

    static string RelativePath(string root, string full)
    {
        string k = root.TrimEnd('\\') + "\\";
        return full.StartsWith(k, StringComparison.OrdinalIgnoreCase)
            ? full.Substring(k.Length) : Path.GetFileName(full);
    }

    static bool CanWrite(string game)
    {
        try
        {
            foreach (string d in new[] { game, Path.Combine(game, "art", "hud"), Path.Combine(game, "audio", "english", "AudioFile") })
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

    static bool GameRunning(string game)
    {
        string prefix = Path.GetFullPath(game).TrimEnd('\\') + "\\";
        foreach (var p in Process.GetProcessesByName("prototypef"))
        {
            try
            {
                if (p.MainModule.FileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { return true; }   // can't determine the path, better assume it's the one
        }
        return false;
    }

    static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    static bool IsAdmin()
    {
        try
        {
            var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- console output
    static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine();
        Console.WriteLine("  ČEŠTINA DO PROTOTYPE " + AppVersion.Text);
        Console.ResetColor();
        Console.WriteLine("  menu, mise, cutscény i titulky mluvených replik");
        Console.WriteLine("  ────────────────────────────────────────────────");
        Console.WriteLine();
    }
    static void Error(string s) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine(s); Console.ResetColor(); }
    static void Warning(string s) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(s); Console.ResetColor(); }
    static void Success(string s) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine(s); Console.ResetColor(); }

    // default Log implementation for console mode
    static void ConsoleLog(string s, LogLevel u)
    {
        if (u == LogLevel.Error) Error(s);
        else if (u == LogLevel.Warning) Warning(s);
        else if (u == LogLevel.Ok) Success(s);
        else Console.WriteLine(s);
    }

    // "Press Enter" only in an interactive console, never in CI/redirected input and never in the GUI
    static int Finish(int code)
    {
        if (!guiMode)
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
        return code;
    }

    // ---------------------------------------------------------------- GUI
    class MainWindow : Form
    {
        readonly TextBox txtFolder;
        readonly Button btnBrowse, btnInstall, btnDryRun, btnUninstall;
        readonly Label lblStatus, lblHint;
        readonly ProgressBar progress;
        readonly RichTextBox txtLog;
        volatile bool running;

        public MainWindow(string initialPath)
        {
            Text = "Čeština do Prototype " + AppVersion.Text;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(560, 480);
            Size = new Size(640, 560);
            AllowDrop = true;

            var lblTitle = new Label
            {
                Text = "ČEŠTINA DO PROTOTYPE " + AppVersion.Text,
                Font = new Font(Font.FontFamily, Font.Size + 3, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(12, 10)
            };
            var lblSubtitle = new Label
            {
                Text = "menu, mise, cutscény i titulky mluvených replik",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Location = new Point(14, 36)
            };

            var lblFolder = new Label { Text = "Složka hry:", AutoSize = true, Location = new Point(14, 66) };
            txtFolder = new TextBox
            {
                Text = initialPath ?? "",
                Location = new Point(14, 84),
                Width = 420,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                AllowDrop = true
            };
            btnBrowse = new Button { Text = "Procházet…", Location = new Point(440, 82), Width = 100, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            lblStatus = new Label { AutoSize = true, Location = new Point(14, 110) };

            btnInstall = new Button { Text = "Nainstalovat", Location = new Point(14, 140), Width = 150 };
            btnDryRun = new Button { Text = "Vyzkoušet nanečisto", Location = new Point(172, 140), Width = 170 };
            btnUninstall = new Button { Text = "Odinstalovat", Location = new Point(350, 140), Width = 150 };

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
                lblTitle, lblSubtitle, lblFolder, txtFolder, btnBrowse, lblStatus,
                btnInstall, btnDryRun, btnUninstall, progress, txtLog, lblHint
            });

            Resize += delegate { LayoutControls(); };
            LayoutControls();

            txtFolder.TextChanged += delegate { Revalidate(); };
            btnBrowse.Click += BtnBrowse_Click;
            btnInstall.Click += delegate { Start(false, false); };
            btnDryRun.Click += delegate { Start(true, false); };
            btnUninstall.Click += delegate { Start(false, true); };

            DragEnter += Window_DragEnter;
            DragDrop += Window_DragDrop;
            txtFolder.DragEnter += Window_DragEnter;
            txtFolder.DragDrop += Window_DragDrop;
            FormClosing += Window_FormClosing;

            Revalidate();
            if (string.IsNullOrEmpty(initialPath))
                GuiLog("Nenašel jsem složku s hrou automaticky. Vyber ji tlačítkem Procházet, nebo ji přetáhni do okna.", LogLevel.Warning);
        }

        void LayoutControls()
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            btnBrowse.Location = new Point(w - 14 - btnBrowse.Width, 82);
            txtFolder.Width = btnBrowse.Left - 24 - txtFolder.Left;
            progress.Width = w - 28;
            lblHint.Location = new Point(14, h - 24);
            txtLog.Location = new Point(14, 204);
            txtLog.Size = new Size(w - 28, Math.Max(60, lblHint.Top - 10 - txtLog.Top));
        }

        void BtnBrowse_Click(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                string start = txtFolder.Text.Trim().Trim('"');
                if (Directory.Exists(start)) dlg.SelectedPath = start;
                dlg.Description = "Vyber složku hry Prototype";
                if (dlg.ShowDialog(this) == DialogResult.OK) txtFolder.Text = dlg.SelectedPath;
            }
        }

        void Window_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
                e.Effect = (paths != null && paths.Length > 0 && Directory.Exists(paths[0])) ? DragDropEffects.Copy : DragDropEffects.None;
            }
        }

        void Window_DragDrop(object sender, DragEventArgs e)
        {
            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (paths != null && paths.Length > 0 && Directory.Exists(paths[0])) txtFolder.Text = paths[0];
        }

        void Window_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (running)
            {
                DialogResult r = MessageBox.Show(this, "Instalace ještě běží. Opravdu zavřít?",
                    "Čeština do Prototype", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) e.Cancel = true;
            }
        }

        void Revalidate()
        {
            string p = txtFolder.Text.Trim().Trim('"');
            bool entered = p.Length > 0;
            bool ok = entered && Directory.Exists(p) && LooksLikeGameExact(p);
            if (!entered)
            {
                lblStatus.Text = "Zadej složku hry, nebo ji sem přetáhni.";
                lblStatus.ForeColor = SystemColors.GrayText;
            }
            else if (ok)
            {
                lblStatus.Text = "Hra nalezena";
                lblStatus.ForeColor = Color.Green;
            }
            else
            {
                lblStatus.Text = "Tohle nevypadá jako složka hry Prototype";
                lblStatus.ForeColor = Color.Firebrick;
            }
            bool hasBackup = ok && Directory.Exists(Path.Combine(p, BACKUP_FOLDER));
            btnInstall.Enabled = ok && !running;
            btnDryRun.Enabled = ok && !running;
            btnUninstall.Enabled = ok && hasBackup && !running;
            txtFolder.Enabled = !running;
            btnBrowse.Enabled = !running;
        }

        void Start(bool dry, bool uninstall)
        {
            string game = txtFolder.Text.Trim().Trim('"');
            if (!Directory.Exists(game) || !LooksLikeGameExact(game))
            {
                MessageBox.Show(this, "Tohle nevypadá jako složka hry Prototype.", "Čeština do Prototype",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!dry)
            {
                if (GameRunning(game))
                {
                    MessageBox.Show(this, "Hra právě běží. Vypni ji a zkus to znovu.", "Čeština do Prototype",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                if (!IsAdmin() && !CanWrite(game))
                {
                    DialogResult r = MessageBox.Show(this,
                        "Zápis do složky hry potřebuje práva správce.\nSpustit instalátor znovu jako správce?",
                        "Čeština do Prototype", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (r == DialogResult.Yes)
                    {
                        try
                        {
                            var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName);
                            psi.Arguments = "--gui \"" + game.TrimEnd('\\') + "\"";
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

            running = true;
            Revalidate();
            progress.Value = 0;
            txtLog.Clear();
            dryRun = dry;
            Log = GuiLog;
            Progress = GuiProgress;

            var thread = new Thread(delegate ()
            {
                int code = 9;
                try { code = uninstall ? Uninstall(game) : Install(game); }
                catch (Exception e) { GuiLog("Neočekávaná chyba: " + e.Message, LogLevel.Error); }
                int result = code;
                try
                {
                    Invoke(new Action(delegate
                    {
                        running = false;
                        Revalidate();
                        Finished(result, dry, uninstall);
                    }));
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        void Finished(int code, bool dry, bool uninstall)
        {
            if (code == 0)
            {
                if (dry)
                    MessageBox.Show(this, "Zkouška nanečisto proběhla v pořádku, nic se nezapisovalo.",
                        "Hotovo", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else if (uninstall)
                    MessageBox.Show(this, "Původní soubory byly vráceny.",
                        "Hotovo", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show(this, "Hotovo, čeština je nainstalovaná. Jazyk hry musí být nastavený na angličtinu.",
                        "Hotovo", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(this, "Něco se nepovedlo (kód " + code + "). Podrobnosti jsou v logu výše.",
                    "Chyba", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void GuiLog(string text, LogLevel u)
        {
            if (InvokeRequired) { try { Invoke(new Action<string, LogLevel>(GuiLog), text, u); } catch { } return; }
            Color color;
            if (u == LogLevel.Error) color = Color.Firebrick;
            else if (u == LogLevel.Warning) color = Color.DarkGoldenrod;
            else if (u == LogLevel.Ok) color = Color.SeaGreen;
            else color = txtLog.ForeColor;
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.SelectionLength = 0;
            txtLog.SelectionColor = color;
            txtLog.AppendText(text + Environment.NewLine);
            txtLog.ScrollToCaret();
        }

        void GuiProgress(int current, int total)
        {
            if (InvokeRequired) { try { Invoke(new Action<int, int>(GuiProgress), current, total); } catch { } return; }
            if (total <= 0) return;
            progress.Maximum = total;
            progress.Value = Math.Max(0, Math.Min(current, total));
        }
    }
}

// ---------------------------------------------------------------- embedded data
static class EmbeddedData
{
    static readonly Assembly CurrentAssembly = typeof(EmbeddedData).Assembly;

    public static bool Exists(string name)
    {
        return CurrentAssembly.GetManifestResourceNames().Contains(name);
    }

    public static byte[] Read(string name)
    {
        using (var s = CurrentAssembly.GetManifestResourceStream(name))
        {
            if (s == null) throw new Exception("v instalátoru chybí data: " + name);
            var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
