// CNFL Rate Widget: a Windows taskbar widget for CNFL's time-of-use residential rate in Costa Rica
// (Tarifa Residencial Horaria, T-REH). The tray icon's color is the rate right now (Low, Mid, High) and
// its number is the time left until it changes; click it for a panel with a live countdown and the day's
// timeline. A heads-up card pops up before the High (and Mid) rate starts.
//
// Compiled at startup by tariff-widget.ps1 through Windows PowerShell's Add-Type, so this is C# 5:
// no string interpolation, no ?. operator. The loader reads this file as UTF-8.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CnflRate
{
    // Filled in from settings.json by tariff-widget.ps1 (see settings.default.json).
    public class Settings
    {
        public double Low = 23.10, Mid = 55.19, High = 134.62;   // colones per kWh
        public bool Alerts = true, Sound = true, Voice = true;
        public int MinutesBefore = 10, HighCardSeconds = 25, MidCardSeconds = 10;
        public bool PromoteIcon = true;   // show the icon on the taskbar, not in the hidden-icons menu
        public bool PowerStats;           // powerLog.enabled: show what the PC cost today and this month
        public string ReportCommand;      // pythonw.exe for power-report.py, or null to hide the link
        public bool ShutdownSwitch;       // claudeShutdownSwitch.enabled
    }

    public class Band
    {
        public readonly string Name, Desc;
        public readonly double Rate;
        public readonly Color Color;
        public Band(string name, string desc, double rate, Color color) { Name = name; Desc = desc; Rate = rate; Color = color; }
    }

    public struct Seg
    {
        public int Start, End;   // minutes of the day, End exclusive
        public Band Band;
        public Seg(int start, int end, Band band) { Start = start; End = end; Band = band; }
    }

    // The T-REH schedule. Each period starts at :01, as in CNFL's tariff sheet:
    //   High (punta):    10:01-12:30 and 17:31-20:00, Monday to Friday only
    //   Mid (valle):     6:01-10:00 and 12:31-17:30, plus the punta hours on Saturday and Sunday
    //   Low (nocturno):  20:01-6:00
    public static class Plan
    {
        public static Band Low, Mid, High;

        public static void Configure(Settings s)
        {
            Low = new Band("Low", "Cheapest rate", s.Low, Color.FromArgb(56, 201, 126));
            Mid = new Band("Mid", "Middle rate", s.Mid, Color.FromArgb(244, 172, 54));
            High = new Band("High", "Most expensive rate", s.High, Color.FromArgb(239, 83, 64));
        }

        public static bool HasHigh(DateTime d) { return d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday; }

        public static List<Seg> Day(DateTime d)
        {
            var s = new List<Seg> { new Seg(0, 361, Low) };
            if (HasHigh(d))
            {
                s.Add(new Seg(361, 601, Mid)); s.Add(new Seg(601, 751, High));
                s.Add(new Seg(751, 1051, Mid)); s.Add(new Seg(1051, 1201, High));
            }
            else s.Add(new Seg(361, 1201, Mid));
            s.Add(new Seg(1201, 1440, Low));
            return s;
        }

        public static Band At(DateTime t)
        {
            int m = t.Hour * 60 + t.Minute;
            foreach (Seg g in Day(t)) if (m >= g.Start && m < g.End) return g.Band;
            return Low;
        }

        // When the rate next changes, and what it changes to.
        public static DateTime NextChange(DateTime t, out Band next)
        {
            Band cur = At(t);
            for (int i = 0; i < 4; i++)
            {
                DateTime d = t.Date.AddDays(i);
                foreach (Seg g in Day(d))
                {
                    DateTime start = d.AddMinutes(g.Start);
                    if (start > t && g.Band != cur) { next = g.Band; return start; }
                }
            }
            next = cur;
            return t.AddDays(1);
        }

        public static DateTime PeriodStart(DateTime t)
        {
            int m = t.Hour * 60 + t.Minute;
            foreach (Seg g in Day(t)) if (m >= g.Start && m < g.End) return t.Date.AddMinutes(g.Start);
            return t;
        }
    }

    static class Native
    {
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    }

    public class App : ApplicationContext
    {
        static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        public const string Mutex = @"Local\CnflRateWidget";
        public const string ShutdownMutex = @"Local\CnflRateWidget.Shutdown";
        public static DateTime? FakeNow;          // previews and screenshots
        public static float Scale = 1f;           // layout scale (screen DPI / 96)
        public static float FontScale = 1f;       // extra font scale, only for screenshots rendered to bitmaps
        public static DateTime Now() { return FakeNow ?? DateTime.Now; }

        public readonly Settings Settings;
        readonly string dir;
        readonly NotifyIcon tray;
        readonly Timer timer = new Timer();
        Flyout flyout;
        HeadsUp card;
        SpeechSynthesizer voice;
        string iconKey = "", tipKey = "", alertedKey;
        DateTime flyoutHiddenAt = DateTime.MinValue, statsLoaded = DateTime.MinValue, watchRead = DateTime.MinValue;
        int promoteTries;
        public bool Silent;   // no chime or voice (previews)
        bool demo;            // screenshots: fixed demo numbers instead of real files

        public static void Run(string dir, Settings s)
        {
            Init(s);
            Application.Run(new App(dir, s, true));
        }

        static void Init(Settings s)
        {
            Plan.Configure(s);
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;
        }

        App(string dir, Settings s, bool withTray)
        {
            this.dir = dir;
            Settings = s;
            if (!withTray) return;
            tray = new NotifyIcon();
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open", null, delegate { ShowFlyout(); });
            if (s.ReportCommand != null) menu.Items.Add("Power report", null, delegate { OpenReport(); });
            if (s.ShutdownSwitch)
            {
                var off = new ToolStripMenuItem("Turn off PC when Claude is done", null, delegate { ToggleShutdown(); });
                menu.Items.Add(off);
                menu.Opening += delegate { watchRead = DateTime.MinValue; LoadWatch(); off.Checked = WatchMode != null; };
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { tray.Visible = false; ExitThread(); });
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (o, e) => { if (e.Button == MouseButtons.Left) Toggle(); };
            UpdateTray();
            tray.Visible = true;
            timer.Interval = 1000;
            timer.Tick += delegate { Tick(); };
            timer.Start();
        }

        void Tick()
        {
            CheckAlerts();
            UpdateTray();
            if (flyout != null && flyout.Visible) flyout.Invalidate();
            if (Settings.PromoteIcon && promoteTries < 12 && DateTime.Now.Second % 5 == 0) { promoteTries++; Promote(); }
        }

        void UpdateTray()
        {
            DateTime now = Now();
            Band band = Plan.At(now), next;
            DateTime change = Plan.NextChange(now, out next);
            int mins = Math.Max(1, (int)Math.Ceiling((change - now).TotalMinutes));
            string label = mins >= 60 ? (mins / 60) + "h" : mins.ToString(CI);
            if (band.Name + label != iconKey)
            {
                iconKey = band.Name + label;
                Icon old = tray.Icon;
                tray.Icon = MakeIcon(band, label, Native.GetSystemMetrics(49));   // SM_CXSMICON at this DPI
                if (old != null) old.Dispose();
            }
            string left = mins >= 60 ? (mins / 60) + "h " + (mins % 60) + "m" : mins + " min";
            string tip = band.Name + " rate  ·  ₡" + band.Rate.ToString("0", CI) + "/kWh\n" + next.Name + " in " + left + ", at " + change.ToString("H:mm", CI);
            if (tip != tipKey) { tipKey = tip; tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip; }
        }

        void Toggle()
        {
            if (flyout != null && flyout.Visible) { flyout.Hide(); return; }
            if ((DateTime.Now - flyoutHiddenAt).TotalMilliseconds < 400) return;   // this click is what closed it
            ShowFlyout();
        }

        void ShowFlyout()
        {
            if (flyout == null || flyout.IsDisposed) flyout = new Flyout(this, false);
            flyout.Place();
            flyout.Show();
            flyout.Activate();
        }

        public void FlyoutHidden() { flyoutHiddenAt = DateTime.Now; }

        public void OpenPanel() { ShowFlyout(); }

        public void OpenReport()
        {
            if (Settings.ReportCommand == null || demo) return;
            try { Process.Start(Settings.ReportCommand, "\"" + Path.Combine(dir, "power-report.py") + "\""); } catch { }
        }

        // ---- Heads-up cards ------------------------------------------------------------------------
        // Shown a few minutes before the High and Mid rates start, and "High rate on now" when the PC
        // starts or wakes up during it. Each period alerts once (logs\alert.state survives restarts).
        void CheckAlerts()
        {
            if (!Settings.Alerts) return;
            DateTime now = Now();
            Band band = Plan.At(now), next;
            DateTime change = Plan.NextChange(now, out next);
            bool soon = next != Plan.Low && (change - now).TotalMinutes <= Settings.MinutesBefore;
            if (!soon && band != Plan.High) return;
            Band b = soon ? next : band;
            DateTime start = soon ? change : Plan.PeriodStart(now);
            string key = b.Name + "-" + start.ToString("yyyy-MM-dd-HHmm", CI);
            if (key == alertedKey) return;
            alertedKey = key;
            string state = Path.Combine(dir, "logs", "alert.state");
            try
            {
                if (File.Exists(state) && File.ReadAllText(state).Trim() == key) return;
                Directory.CreateDirectory(Path.GetDirectoryName(state));
                File.WriteAllText(state, key);
            }
            catch { }
            ShowCard(b, !soon, start);
        }

        // live: the rate is already on (High now); otherwise it starts at `start`.
        public void ShowCard(Band b, bool live, DateTime start)
        {
            if (card != null && !card.IsDisposed) card.Close();
            card = new HeadsUp(this, b, live, start, false);
            card.Show();   // never takes focus, so a game or video keeps running
            if (Silent || !Settings.Sound) return;
            Play(b == Plan.High ? "Windows Notify Calendar.wav" : "Windows Notify System Generic.wav");
            if (b != Plan.High || !Settings.Voice) return;
            Band after;
            DateTime end = Plan.NextChange(start, out after).AddMinutes(-1);
            int mins = Math.Max(1, (int)Math.Ceiling((start - Now()).TotalMinutes));
            string say = live ? "Heads up. The high rate is on now, until " + end.ToString(end.Minute == 0 ? "h tt" : "h:mm tt", CI) + "."
                              : "Heads up. The high rate starts in " + mins + (mins == 1 ? " minute." : " minutes.");
            var t = new Timer();
            t.Interval = 1300;   // after the chime
            t.Tick += delegate
            {
                t.Stop();
                t.Dispose();
                try
                {
                    if (voice == null)
                    {
                        voice = new SpeechSynthesizer();
                        // The words are English: use an English voice when Windows has one (a Spanish Windows defaults to a Spanish voice).
                        try { voice.SelectVoiceByHints(VoiceGender.NotSet, VoiceAge.NotSet, 0, new CultureInfo("en-US")); } catch { }
                    }
                    voice.SpeakAsync(say);
                }
                catch { }
            };
            t.Start();
        }

        static void Play(string wav)
        {
            try { new SoundPlayer(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", wav)).Play(); }
            catch { SystemSounds.Asterisk.Play(); }
        }

        // ---- What the PC cost (optional power log) ----------------------------------------------------
        // From power-log.ps1's CSV, priced the same way as power-report.py. Re-read at most once a minute.
        public bool HasStats;
        public double TodayKwh, TodayCost, MonthKwh, MonthCost;

        public void LoadStats()
        {
            if (demo || (DateTime.Now - statsLoaded).TotalSeconds < 60) return;
            statsLoaded = DateTime.Now;
            string file = Path.Combine(dir, "logs", "power-" + DateTime.Now.ToString("yyyy-MM", CI) + ".csv");
            string today = DateTime.Now.ToString("yyyy-MM-dd", CI);
            double tk = 0, tc = 0, mk = 0, mc = 0;
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs))
                {
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        string[] p = line.Split(',');
                        double pc, mon;
                        DateTime t;
                        if (p.Length < 7 || !double.TryParse(p[5], NumberStyles.Float, CI, out pc) || !double.TryParse(p[6], NumberStyles.Float, CI, out mon)
                            || !DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm", CI, DateTimeStyles.None, out t)) continue;
                        double k = (pc + mon) / 60000.0;   // one row = one minute
                        double c = k * Plan.At(t).Rate;
                        mk += k; mc += c;
                        if (line.StartsWith(today)) { tk += k; tc += c; }
                    }
                }
            }
            catch { }
            HasStats = mk > 0;
            TodayKwh = tk; TodayCost = tc; MonthKwh = mk; MonthCost = mc;
        }

        // ---- Optional: turn the PC off when Claude Code is done --------------------------------------
        // shutdown-when-done.ps1 holds ShutdownMutex while it runs and writes logs\shutdown-watch.status.
        public string WatchMode;   // null when off; "starting", "watching", "nothing" or "shutting-down"
        public int WatchCount, WatchBusy;
        public bool WatchJobs;     // renders, encodes or batch runs still going
        public DateTime ShutdownAt;

        public void LoadWatch()
        {
            if (demo || (DateTime.Now - watchRead).TotalSeconds < 1.5) return;
            watchRead = DateTime.Now;
            System.Threading.Mutex m;
            if (!System.Threading.Mutex.TryOpenExisting(ShutdownMutex, out m)) { WatchMode = null; return; }
            m.Dispose();
            WatchMode = "starting";
            WatchCount = 0;
            WatchBusy = 0;
            WatchJobs = false;
            try
            {
                string[] lines = File.ReadAllLines(Path.Combine(dir, "logs", "shutdown-watch.status"));
                if (lines.Length == 0) return;
                WatchMode = lines[0].Trim();
                if (WatchMode == "shutting-down")
                {
                    DateTime t;
                    if (lines.Length > 1 && DateTime.TryParse(lines[1].Trim(), CI, DateTimeStyles.RoundtripKind, out t)) ShutdownAt = t.ToLocalTime();
                    return;
                }
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] p = lines[i].Split('\t');
                    if (p.Length < 2) continue;
                    if (p[0] == "(jobs)") { WatchJobs = p[1] != "done"; continue; }
                    WatchCount++;
                    if (p[1] == "busy" || p[1] == "bg-job" || p[1] == "settling" || p[1] == "on-hold") WatchBusy++;
                }
            }
            catch { }
        }

        public void ToggleShutdown()
        {
            watchRead = DateTime.MinValue;
            LoadWatch();
            if (WatchMode != null) CancelShutdown(); else ArmShutdown();
            watchRead = DateTime.MinValue;
        }

        void ArmShutdown()
        {
            try { File.Delete(Path.Combine(dir, "logs", "shutdown-watch.status")); } catch { }
            string ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            Hidden(Path.Combine(Environment.SystemDirectory, "conhost.exe"),
                "--headless \"" + ps + "\" -NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(dir, "shutdown-when-done.ps1") + "\" -Auto");
        }

        void CancelShutdown()
        {
            string logs = Path.Combine(dir, "logs");
            try
            {
                Process p = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(logs, "shutdown-watch.pid")).Trim(), CI));
                if (p.ProcessName.Equals("powershell", StringComparison.OrdinalIgnoreCase)) p.Kill();
            }
            catch { }
            Hidden("shutdown.exe", "/a");   // in case the 2-minute countdown already started
            try
            {
                File.Delete(Path.Combine(logs, "shutdown-watch.status"));
                File.AppendAllText(Path.Combine(logs, "shutdown-watch.log"),
                    DateTime.Now.ToString("MM-dd HH:mm:ss", CI) + "  Cancelled from the widget." + Environment.NewLine);
            }
            catch { }
        }

        static void Hidden(string exe, string args)
        {
            var si = new ProcessStartInfo(exe, args);
            si.UseShellExecute = false;
            si.CreateNoWindow = true;
            try { Process.Start(si); } catch { }
        }

        // Windows 11 puts new tray icons in the hidden-icons menu; show this one on the taskbar, once.
        void Promote()
        {
            string marker = Path.Combine(dir, "logs", "tray-promoted");
            if (File.Exists(marker)) { promoteTries = 99; return; }
            try
            {
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
                {
                    if (root == null) return;
                    foreach (string name in root.GetSubKeyNames())
                        using (RegistryKey k = root.OpenSubKey(name, true))
                        {
                            if (k == null) continue;
                            string exe = (k.GetValue("ExecutablePath") as string) ?? "";
                            string tip = (k.GetValue("InitialTooltip") as string) ?? "";
                            if (!exe.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase)) continue;
                            if (!(tip.StartsWith("Low rate") || tip.StartsWith("Mid rate") || tip.StartsWith("High rate"))) continue;
                            k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                            Directory.CreateDirectory(Path.GetDirectoryName(marker));
                            File.WriteAllText(marker, DateTime.Now.ToString("s", CI));
                            promoteTries = 99;
                        }
                }
            }
            catch { }
        }

        // ---- Drawing helpers ------------------------------------------------------------------------
        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Font F(string family, float points) { return new Font(family, points * FontScale); }
        public static Font F(string family, float points, FontStyle style) { return new Font(family, points * FontScale, style); }

        // A rounded square in the rate's color with the time left ("2h", "45") centered on it.
        public static Icon MakeIcon(Band b, string text, int size)
        {
            using (Bitmap bmp = IconBitmap(b, text, size))
            {
                IntPtr h = bmp.GetHicon();
                Icon icon = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return icon;
            }
        }

        public static Bitmap IconBitmap(Band b, string text, int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (GraphicsPath box = Round(new RectangleF(0.5f, 0.5f, size - 1f, size - 1f), size * 0.27f))
                using (var fill = new SolidBrush(b.Color)) g.FillPath(fill, box);
                using (var glyphs = new GraphicsPath())
                using (var ff = new FontFamily("Segoe UI"))
                {
                    glyphs.AddString(text, ff, (int)FontStyle.Bold, 100f, PointF.Empty, StringFormat.GenericTypographic);
                    RectangleF bnd = glyphs.GetBounds();
                    float k = Math.Min(size * 0.86f / bnd.Width, size * 0.60f / bnd.Height);
                    using (var m = new Matrix())
                    {
                        m.Translate(size / 2f, size / 2f);
                        m.Scale(k, k);
                        m.Translate(-(bnd.X + bnd.Width / 2f), -(bnd.Y + bnd.Height / 2f));
                        glyphs.Transform(m);
                    }
                    Color ink = b == Plan.High ? Color.White : Color.FromArgb(24, 24, 27);
                    using (var brush = new SolidBrush(ink)) g.FillPath(brush, glyphs);
                }
            }
            return bmp;
        }

        // ---- Previews, tests and README screenshots ---------------------------------------------------
        // Shows the panel as it would look at a given time, for a few seconds.
        public static void Preview(string dir, Settings s, DateTime fake, int ms)
        {
            Init(s);
            FakeNow = fake;
            var f = new Flyout(new App(dir, s, false), true);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            f.Location = new Point(wa.Right - f.Width - (int)(12 * Scale), wa.Bottom - f.Height - (int)(12 * Scale));
            var t = new Timer();
            t.Interval = ms;
            t.Tick += delegate { Application.ExitThread(); };
            t.Start();
            f.Show();
            Application.Run();
        }

        // Shows a heads-up card as it would appear on a weekday, until it closes.
        public static void CardPreview(string dir, Settings s, string which, bool silent)
        {
            Init(s);
            DateTime day = new DateTime(2026, 10, 14);   // a Wednesday
            var app = new App(dir, s, false);
            app.Silent = silent;
            if (which == "mid") { FakeNow = day.AddMinutes(351); app.ShowCard(Plan.Mid, false, day.AddMinutes(361)); }
            else if (which == "highends") { FakeNow = day.AddMinutes(741); app.ShowCard(Plan.Mid, false, day.AddMinutes(751)); }
            else if (which == "highnow") { FakeNow = day.AddMinutes(1087); app.ShowCard(Plan.High, true, day.AddMinutes(1051)); }
            else { FakeNow = day.AddMinutes(1041); app.ShowCard(Plan.High, false, day.AddMinutes(1051)); }
            app.card.FormClosed += delegate { Application.ExitThread(); };
            Application.Run();
        }

        // Flips the optional "turn off PC when Claude is done" switch, as clicking it does.
        public static void ToggleSwitch(string dir, Settings s) { Plan.Configure(s); new App(dir, s, false).ToggleShutdown(); }

        // Renders the README images: drawn by the same code as the real widget, onto a transparent
        // background, so no real desktop (or anything private on it) ends up in a picture.
        public static void RenderDocs(string dir, Settings s, string outDir)
        {
            Init(s);
            Scale = 2f;
            FontScale = 2f;
            Directory.CreateDirectory(outDir);
            var app = new App(dir, s, false);
            app.demo = true;
            app.Silent = true;
            DateTime wed = new DateTime(2026, 10, 14), sat = new DateTime(2026, 10, 17);

            s.PowerStats = false; s.ShutdownSwitch = false;
            Save(Framed(RenderPanel(app, wed.AddHours(10).AddMinutes(37).AddSeconds(12)), 14), outDir, "panel-high.png");
            Save(Framed(RenderPanel(app, wed.AddHours(15).AddMinutes(44).AddSeconds(30)), 14), outDir, "panel-mid.png");
            Save(Framed(RenderPanel(app, wed.AddHours(22).AddMinutes(10).AddSeconds(5)), 14), outDir, "panel-low.png");
            Save(Framed(RenderPanel(app, sat.AddHours(11).AddMinutes(5)), 14), outDir, "panel-weekend.png");

            s.PowerStats = true; s.ReportCommand = "demo";
            app.HasStats = true; app.TodayKwh = 1.42; app.TodayCost = 48; app.MonthKwh = 38.6; app.MonthCost = 1236;
            Save(Framed(RenderPanel(app, wed.AddHours(15).AddMinutes(44).AddSeconds(30)), 14), outDir, "panel-power.png");

            s.ShutdownSwitch = true;
            app.WatchMode = "watching"; app.WatchCount = 2; app.WatchBusy = 1;
            Save(Framed(RenderPanel(app, wed.AddHours(22).AddMinutes(10).AddSeconds(5)), 14), outDir, "panel-claude-switch.png");
            s.PowerStats = false; s.ReportCommand = null; s.ShutdownSwitch = false; app.WatchMode = null;

            Save(Framed(RenderCard(app, Plan.High, false, wed.AddMinutes(1051), wed.AddMinutes(1041)), 10), outDir, "card-high.png");
            Save(Framed(RenderCard(app, Plan.High, true, wed.AddMinutes(1051), wed.AddMinutes(1087).AddSeconds(50)), 10), outDir, "card-high-now.png");
            Save(Framed(RenderCard(app, Plan.Mid, false, wed.AddMinutes(751), wed.AddMinutes(741)), 10), outDir, "card-high-ends.png");
            Save(Framed(RenderCard(app, Plan.Mid, false, wed.AddMinutes(361), wed.AddMinutes(351)), 10), outDir, "card-mid.png");

            Save(RenderIcons(), outDir, "tray-icons.png");
            Save(RenderHero(app, wed.AddHours(17).AddMinutes(21)), outDir, "hero.png");
            FakeNow = null;
        }

        static void Save(Bitmap b, string outDir, string name)
        {
            using (b) b.Save(Path.Combine(outDir, name), ImageFormat.Png);
        }

        static Bitmap RenderPanel(App app, DateTime at)
        {
            FakeNow = at;
            using (var f = new Flyout(app, true))
            {
                var bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(bmp)) { g.Clear(Flyout.Bg); f.PaintTo(g); }
                return bmp;
            }
        }

        static Bitmap RenderCard(App app, Band b, bool live, DateTime start, DateTime at)
        {
            FakeNow = at;
            using (var c = new HeadsUp(app, b, live, start, true))
            {
                var bmp = new Bitmap(c.ClientSize.Width, c.ClientSize.Height, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(bmp)) { g.Clear(HeadsUp.Bg); c.PaintTo(g); }
                return bmp;
            }
        }

        // Rounded corners, a thin border and a soft shadow, on a transparent background.
        static Bitmap Framed(Bitmap content, float radius)
        {
            using (content)
            {
                int pad = (int)(26 * Scale);
                float rad = radius * Scale;
                var bmp = new Bitmap(content.Width + 2 * pad, content.Height + 2 * pad, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    var r = new RectangleF(pad, pad, content.Width, content.Height);
                    for (int i = 12; i >= 1; i--)
                    {
                        float grow = i * 0.9f * Scale;
                        using (GraphicsPath sp = Round(new RectangleF(r.X - grow, r.Y - grow + 5 * Scale, r.Width + 2 * grow, r.Height + 2 * grow), rad + grow))
                        using (var sb = new SolidBrush(Color.FromArgb(9, 0, 0, 0))) g.FillPath(sb, sp);
                    }
                    using (GraphicsPath p = Round(r, rad))
                    using (var tb = new TextureBrush(content))
                    using (var pen = new Pen(Color.FromArgb(70, 70, 78), Math.Max(1f, Scale * 0.75f)))
                    {
                        tb.TranslateTransform(pad, pad);
                        g.FillPath(tb, p);
                        g.DrawPath(pen, p);
                    }
                }
                return bmp;
            }
        }

        static Bitmap RenderIcons()
        {
            var items = new[] {
                Tuple.Create(Plan.Low, "9h", "Low  ·  9 h left"),
                Tuple.Create(Plan.Mid, "2h", "Mid  ·  2 h left"),
                Tuple.Create(Plan.Mid, "7", "Mid  ·  7 min left"),
                Tuple.Create(Plan.High, "45", "High  ·  45 min left") };
            int icon = (int)(32 * Scale), cell = (int)(150 * Scale), h = (int)(84 * Scale);
            var bmp = new Bitmap(cell * items.Length, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            using (Font f = F("Segoe UI Semibold", 9.5f))
            using (var ink = new SolidBrush(Color.FromArgb(128, 128, 136)))
            using (var center = new StringFormat { Alignment = StringAlignment.Center })
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                for (int i = 0; i < items.Length; i++)
                {
                    using (Bitmap ib = IconBitmap(items[i].Item1, items[i].Item2, icon))
                        g.DrawImage(ib, i * cell + (cell - icon) / 2, (int)(8 * Scale));
                    g.DrawString(items[i].Item3, f, ink, new RectangleF(i * cell, 8 * Scale + icon + 10 * Scale, cell, 24 * Scale), center);
                }
            }
            return bmp;
        }

        // Everything together on a made-up desktop: the card, the open panel and the taskbar icon.
        static Bitmap RenderHero(App app, DateTime at)
        {
            int w = (int)(1100 * Scale), h = (int)(640 * Scale), bar = (int)(48 * Scale);
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(14, 26, 48), Color.FromArgb(46, 28, 66), 35f))
                    g.FillRectangle(bg, 0, 0, w, h);
                Glow(g, w * 0.18f, h * 0.30f, w * 0.45f, Color.FromArgb(70, 80, 160, 255));
                Glow(g, w * 0.80f, h * 0.20f, w * 0.40f, Color.FromArgb(60, 255, 120, 90));
                Glow(g, w * 0.55f, h * 0.85f, w * 0.50f, Color.FromArgb(45, 120, 255, 190));

                // Taskbar with the tray icon and a clock
                using (var tb = new SolidBrush(Color.FromArgb(235, 28, 28, 31))) g.FillRectangle(tb, 0, h - bar, w, bar);
                using (var line = new Pen(Color.FromArgb(60, 255, 255, 255))) g.DrawLine(line, 0, h - bar, w, h - bar);
                Band band = Plan.At(at), next;
                DateTime change = Plan.NextChange(at, out next);
                int mins = Math.Max(1, (int)Math.Ceiling((change - at).TotalMinutes));
                int icon = (int)(16 * Scale);
                int clockX = w - (int)(96 * Scale);
                using (Bitmap ib = IconBitmap(band, mins >= 60 ? (mins / 60) + "h" : mins.ToString(CI), icon))
                    g.DrawImage(ib, clockX - (int)(40 * Scale), h - bar + (bar - icon) / 2);
                using (Font fc = F("Segoe UI", 8.5f))
                using (var white = new SolidBrush(Color.FromArgb(235, 235, 240)))
                using (var right = new StringFormat { Alignment = StringAlignment.Far })
                {
                    g.DrawString(at.ToString("h:mm tt", CI), fc, white, new RectangleF(clockX, h - bar + 7 * Scale, 84 * Scale, 18 * Scale), right);
                    g.DrawString(at.ToString("M/d/yyyy", CI), fc, white, new RectangleF(clockX, h - bar + 24 * Scale, 84 * Scale, 18 * Scale), right);
                }

                using (Bitmap panel = Framed(RenderPanel(app, at), 14))
                    g.DrawImage(panel, w - panel.Width + (int)(14 * Scale), h - bar - panel.Height + (int)(14 * Scale));
                using (Bitmap card = Framed(RenderCard(app, Plan.High, false, change, at), 10))
                    g.DrawImage(card, (w - card.Width) / 2 - (int)(150 * Scale), (int)(4 * Scale));
            }
            FakeNow = null;
            return bmp;
        }

        static void Glow(Graphics g, float cx, float cy, float r, Color c)
        {
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(cx - r, cy - r, 2 * r, 2 * r);
                using (var pg = new PathGradientBrush(path))
                {
                    pg.CenterColor = c;
                    pg.SurroundColors = new[] { Color.FromArgb(0, c) };
                    g.FillPath(pg, path);
                }
            }
        }
    }

    // The panel that opens when you click the tray icon.
    public class Flyout : Form
    {
        static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        const TextFormatFlags TF = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        public static readonly Color Bg = Color.FromArgb(24, 24, 27);
        static readonly Color Fg = Color.FromArgb(242, 242, 245), Muted = Color.FromArgb(160, 160, 168),
            Faint = Color.FromArgb(112, 112, 120), Rule = Color.FromArgb(44, 44, 50);
        const int BaseH = 266, StatsH = 64, SwitchH = 70;
        readonly App app;
        readonly bool preview;
        Rectangle reportLink, shutdownRow;
        bool hoverRow;

        public Flyout(App app, bool preview)
        {
            this.app = app;
            this.preview = preview;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            BackColor = Bg;
            Text = "CNFL rate";
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            int h = BaseH + (app.Settings.PowerStats ? StatsH : 0) + (app.Settings.ShutdownSwitch ? SwitchH : 0);
            ClientSize = new Size(S(360), S(h));
            Deactivate += delegate { if (!this.preview) { Hide(); this.app.FlyoutHidden(); } };
            KeyDown += (o, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
            MouseClick += (o, e) =>
            {
                if (reportLink.Contains(e.Location)) { Hide(); this.app.OpenReport(); }
                else if (shutdownRow.Contains(e.Location)) { this.app.ToggleShutdown(); Invalidate(); }
            };
            MouseMove += (o, e) =>
            {
                bool hover = shutdownRow.Contains(e.Location);
                if (hover != hoverRow) { hoverRow = hover; Invalidate(); }
                Cursor = hover || reportLink.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
            };
            MouseLeave += delegate { if (hoverRow) { hoverRow = false; Invalidate(); } };
        }

        static int S(float v) { return (int)Math.Round(v * App.Scale); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2;            // DWMWCP_ROUND (Windows 11)
            Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);
            int border = 0x00463F3A;  // DWMWA_BORDER_COLOR, 0x00BBGGRR
            Native.DwmSetWindowAttribute(Handle, 34, ref border, 4);
        }

        // Just above the tray, on the screen the mouse is on.
        public void Place()
        {
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(wa.Right - Width - S(12), wa.Bottom - Height - S(12));
        }

        static int DrawT(Graphics g, string t, Font f, Color c, int x, int y)
        {
            TextRenderer.DrawText(g, t, f, new Point(x, y), c, TF);
            return TextRenderer.MeasureText(g, t, f, new Size(int.MaxValue, int.MaxValue), TF).Width;
        }

        static void DrawRight(Graphics g, string t, Font f, Color c, int right, int y)
        {
            int w = TextRenderer.MeasureText(g, t, f, new Size(int.MaxValue, int.MaxValue), TF).Width;
            TextRenderer.DrawText(g, t, f, new Point(right - w, y), c, TF);
        }

        protected override void OnPaint(PaintEventArgs e) { PaintTo(e.Graphics); }

        public void PaintTo(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DateTime now = App.Now();
            Band band = Plan.At(now), next;
            DateTime change = Plan.NextChange(now, out next);
            TimeSpan left = change - now;
            int w = ClientSize.Width, pad = S(22);

            using (Font fCaps = App.F("Segoe UI", 8f, FontStyle.Bold), fSmall = App.F("Segoe UI", 8.5f),
                        fName = App.F("Segoe UI Semibold", 20f), fRate = App.F("Segoe UI Semibold", 11f),
                        fBody = App.F("Segoe UI", 10f), fBodyB = App.F("Segoe UI Semibold", 10f),
                        fClock = App.F("Segoe UI Semibold", 30f), fTick = App.F("Segoe UI", 7.5f))
            {
                // Header
                DrawT(g, "CNFL RATE  ·  T-REH", fCaps, Faint, pad, S(16));
                DrawRight(g, now.ToString("ddd d MMM", CI) + (Plan.HasHigh(now) ? "" : "  ·  no High today"), fSmall, Faint, w - pad, S(16));

                // The rate right now
                using (var dot = new SolidBrush(band.Color)) g.FillEllipse(dot, pad, S(51), S(12), S(12));
                DrawT(g, band.Name, fName, Fg, pad + S(20), S(36));
                DrawRight(g, "₡" + band.Rate.ToString("0.00", CI) + " / kWh", fRate, band.Color, w - pad, S(47));
                string desc = band == Plan.Low ? "Cheapest: the best time for heavy use"
                    : band.Desc + ", " + (band.Rate / Plan.Low.Rate).ToString("0.0", CI) + "× the Low rate";
                DrawT(g, desc, fBody, Muted, pad, S(78));

                // Countdown to the next change
                string clock = left.TotalHours >= 1
                    ? string.Format(CI, "{0}:{1:00}:{2:00}", (int)left.TotalHours, left.Minutes, left.Seconds)
                    : string.Format(CI, "{0}:{1:00}", left.Minutes, left.Seconds);
                DrawT(g, clock, fClock, Fg, pad - S(2), S(104));
                int x = pad, ly = S(160);
                x += DrawT(g, "until", fBody, Muted, x, ly) + S(4);
                x += DrawT(g, next.Name, fBodyB, next.Color, x, ly) + S(4);
                DrawT(g, "starts at " + change.ToString("H:mm", CI) + (change.Date != now.Date ? ", " + change.ToString("dddd", CI) : ""), fBody, Muted, x, ly);

                // Today, with the part already gone dimmed
                DrawTimeline(g, now, pad, S(198), w - 2 * pad, S(10), fTick);

                // Rates
                int lx = pad;
                foreach (Band lb in new[] { Plan.Low, Plan.Mid, Plan.High })
                {
                    using (var br = new SolidBrush(lb.Color)) g.FillEllipse(br, lx, S(243), S(8), S(8));
                    lx += S(13);
                    lx += DrawT(g, lb.Name + " ₡" + lb.Rate.ToString("0.00", CI), fSmall, Muted, lx, S(238)) + S(16);
                }

                int y = BaseH;
                if (app.Settings.PowerStats)
                {
                    // What the PC cost: today and this month so far (same numbers as the Power report)
                    using (var pen = new Pen(Rule)) g.DrawLine(pen, pad, S(y), w - pad, S(y));
                    app.LoadStats();
                    int col2 = pad + S(132);
                    DrawT(g, "PC TODAY", fCaps, Faint, pad, S(y + 13));
                    DrawT(g, "THIS MONTH SO FAR", fCaps, Faint, col2, S(y + 13));
                    if (app.HasStats)
                    {
                        int vx = pad + DrawT(g, "₡" + app.TodayCost.ToString("#,0", CI), fRate, Fg, pad, S(y + 30)) + S(6);
                        DrawT(g, app.TodayKwh.ToString("0.00", CI) + " kWh", fSmall, Faint, vx, S(y + 33));
                        vx = col2 + DrawT(g, "₡" + app.MonthCost.ToString("#,0", CI), fRate, Fg, col2, S(y + 30)) + S(6);
                        DrawT(g, app.MonthKwh.ToString("0.0", CI) + " kWh", fSmall, Faint, vx, S(y + 33));
                    }
                    else DrawT(g, "No power data yet", fSmall, Muted, pad, S(y + 32));
                    reportLink = Rectangle.Empty;
                    if (app.Settings.ReportCommand != null)
                    {
                        string link = "Report ›";
                        Size ls = TextRenderer.MeasureText(g, link, fSmall, new Size(int.MaxValue, int.MaxValue), TF);
                        reportLink = new Rectangle(w - pad - ls.Width, S(y + 33), ls.Width, ls.Height);
                        TextRenderer.DrawText(g, link, fSmall, reportLink.Location, Fg, TF);
                    }
                    y += StatsH;
                }

                shutdownRow = Rectangle.Empty;
                if (app.Settings.ShutdownSwitch)
                {
                    // Optional switch: turn the PC off once the Claude Code sessions working now are done
                    app.LoadWatch();
                    bool on = app.WatchMode != null, going = app.WatchMode == "shutting-down";
                    using (var pen = new Pen(Rule)) g.DrawLine(pen, pad, S(y), w - pad, S(y));
                    shutdownRow = new Rectangle(pad - S(8), S(y + 8), w - 2 * pad + S(16), S(50));
                    if (hoverRow)
                        using (GraphicsPath hp = App.Round(shutdownRow, S(8)))
                        using (var hb = new SolidBrush(Color.FromArgb(34, 34, 39))) g.FillPath(hb, hp);
                    DrawPowerGlyph(g, pad, S(y + 22), S(18), going ? Plan.High.Color : on ? Plan.Low.Color : Muted);
                    DrawT(g, "Turn off PC when Claude is done", fBodyB, Fg, pad + S(30), S(y + 15));
                    string sub;
                    if (!on) sub = "Waits for the Claude sessions working now";
                    else if (going)
                    {
                        TimeSpan r = app.ShutdownAt - DateTime.Now;
                        if (r < TimeSpan.Zero) r = TimeSpan.Zero;
                        sub = "Shutting down in " + string.Format(CI, "{0}:{1:00}", (int)r.TotalMinutes, r.Seconds) + "  ·  flip to cancel";
                    }
                    else if (app.WatchMode == "nothing") sub = "No Claude session is working  ·  shutting down";
                    else if (app.WatchMode == "watching" && app.WatchCount == 0)
                        sub = app.WatchJobs ? "Waiting for background jobs (renders, builds)" : "Wrapping up";
                    else if (app.WatchMode == "watching")
                        sub = "Watching " + app.WatchCount + (app.WatchCount == 1 ? " session" : " sessions") + "  ·  "
                            + (app.WatchBusy > 0 ? app.WatchBusy + " still working" : app.WatchJobs ? "jobs still running" : "wrapping up")
                            + (app.WatchBusy > 0 && app.WatchJobs ? " + jobs" : "");
                    else sub = "Starting…";
                    TextRenderer.DrawText(g, sub, fSmall, new Rectangle(pad + S(30), S(y + 36), w - 2 * pad - S(30) - S(48), S(18)),
                        going ? Plan.High.Color : Muted, TF | TextFormatFlags.EndEllipsis);   // never runs under the switch
                    DrawSwitch(g, new Rectangle(w - pad - S(40), S(y + 23), S(40), S(22)), on, going);
                }
            }
        }

        static void DrawPowerGlyph(Graphics g, int x, int y, int size, Color c)
        {
            using (var pen = new Pen(c, Math.Max(1.6f, size / 9f)))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawArc(pen, x + size * 0.1f, y + size * 0.15f, size * 0.8f, size * 0.8f, -60, 300);   // gap at the top
                g.DrawLine(pen, x + size / 2f, y, x + size / 2f, y + size * 0.5f);
            }
        }

        static void DrawSwitch(Graphics g, Rectangle r, bool on, bool alert)
        {
            Color track = on ? (alert ? Plan.High.Color : Plan.Low.Color) : Color.FromArgb(70, 70, 78);
            using (GraphicsPath p = App.Round(r, r.Height / 2f))
            using (var b = new SolidBrush(track)) g.FillPath(b, p);
            int d = r.Height - S(6);
            using (var knob = new SolidBrush(Color.White)) g.FillEllipse(knob, on ? r.Right - d - S(3) : r.Left + S(3), r.Top + S(3), d, d);
        }

        void DrawTimeline(Graphics g, DateTime now, int x0, int y, int width, int h, Font fTick)
        {
            List<Seg> segs = Plan.Day(now);
            float nowX = x0 + width * (float)now.TimeOfDay.TotalMinutes / 1440f;
            using (GraphicsPath clip = App.Round(new RectangleF(x0, y, width, h), h / 2f))
            {
                g.SetClip(clip);
                foreach (Seg sg in segs)
                    using (var br = new SolidBrush(sg.Band.Color))
                        g.FillRectangle(br, x0 + width * sg.Start / 1440f, y, width * (sg.End - sg.Start) / 1440f + 1, h);
                using (var dim = new SolidBrush(Color.FromArgb(140, Bg))) g.FillRectangle(dim, x0, y, nowX - x0, h);
                g.ResetClip();
            }
            using (var outline = new Pen(Bg, S(5))) g.DrawLine(outline, nowX, y - S(5), nowX, y + h + S(5));
            using (var marker = new Pen(Color.White, Math.Max(2, S(2))))
            {
                marker.StartCap = marker.EndCap = LineCap.Round;
                g.DrawLine(marker, nowX, y - S(4), nowX, y + h + S(4));
            }
            int lastRight = int.MinValue;
            foreach (Seg sg in segs)
            {
                if (sg.Start == 0) continue;
                int m = sg.Start - 1;   // label each change by the minute before it: 6:00, 10:00, 12:30 ...
                string t = (m / 60) + ":" + (m % 60).ToString("00", CI);
                int tw = TextRenderer.MeasureText(g, t, fTick, new Size(int.MaxValue, int.MaxValue), TF).Width;
                int lx = (int)(x0 + width * sg.Start / 1440f) - tw / 2;
                if (lx < lastRight + S(4)) continue;
                DrawT(g, t, fTick, Faint, lx, y + h + S(7));
                lastRight = lx + tw;
            }
        }
    }

    // The heads-up card: topmost but never activated, so a game or video in front keeps focus.
    // High: top-center of the screen in use. Mid: top-right. Click it to open the panel.
    public class HeadsUp : Form
    {
        static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        const TextFormatFlags TF = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        public static readonly Color Bg = Color.FromArgb(24, 24, 27);
        static readonly Color Fg = Color.FromArgb(242, 242, 245), Muted = Color.FromArgb(160, 160, 168), Track = Color.FromArgb(44, 44, 50);
        readonly Band band;
        readonly bool live, docs;
        readonly DateTime start, end, shownAt;
        readonly double seconds;
        readonly Timer timer = new Timer();

        public HeadsUp(App app, Band band, bool live, DateTime start, bool docs)
        {
            this.band = band;
            this.live = live;
            this.start = start;
            this.docs = docs;
            Band after;
            end = Plan.NextChange(start, out after);
            bool high = band == Plan.High;
            seconds = high ? app.Settings.HighCardSeconds : app.Settings.MidCardSeconds;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Bg;
            Text = "CNFL rate heads-up";
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            ClientSize = high ? new Size(S(470), S(128)) : new Size(S(410), S(110));
            shownAt = DateTime.Now;
            if (docs) return;
            Rectangle wa = Screen.FromHandle(Native.GetForegroundWindow()).WorkingArea;
            Location = high ? new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + S(24))
                            : new Point(wa.Right - Width - S(24), wa.Top + S(24));
            timer.Interval = 250;
            timer.Tick += delegate { if ((DateTime.Now - shownAt).TotalSeconds >= seconds) Close(); else Invalidate(); };
            timer.Start();
            MouseClick += delegate { Close(); app.OpenPanel(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008;   // NOACTIVATE | TOOLWINDOW | TOPMOST
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2;   // DWMWCP_ROUND (Windows 11)
            Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);
            int border = band.Color.R | (band.Color.G << 8) | (band.Color.B << 16);   // edge in the rate's color
            Native.DwmSetWindowAttribute(Handle, 34, ref border, 4);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            timer.Dispose();
            base.OnFormClosed(e);
        }

        static int S(float v) { return (int)Math.Round(v * App.Scale); }

        static string Clock(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return t.TotalHours >= 1 ? string.Format(CI, "{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                                     : string.Format(CI, "{0}:{1:00}", t.Minutes, t.Seconds);
        }

        protected override void OnPaint(PaintEventArgs e) { PaintTo(e.Graphics); }

        public void PaintTo(Graphics g)
        {
            DateTime now = App.Now();
            bool high = band == Plan.High;
            bool highEnds = !high && start.Hour * 60 + start.Minute == 751;   // 12:31: Mid after the morning High
            int w = ClientSize.Width, h = ClientSize.Height, pad = S(24);
            string span = start.ToString("H:mm", CI) + "–" + end.AddMinutes(-1).ToString("H:mm", CI);
            string times = (band.Rate / Plan.Low.Rate).ToString(high ? "0" : "0.0", CI) + "×";
            string caps = high ? "HIGH RATE" : highEnds ? "HIGH RATE ENDS SOON" : "MID RATE";
            string big = live ? "On now · " + Clock(end - now) + " left"
                              : (highEnds ? "High ends in " : "Starts in ") + Clock(start - now);
            string small = high ? span + "  ·  " + times + " the Low rate. Pause heavy use."
                                : (highEnds ? "Mid rate " : "") + span + "  ·  ₡" + band.Rate.ToString("0.00", CI) + " / kWh, " + times + " the Low rate";

            using (var edge = new SolidBrush(band.Color)) g.FillRectangle(edge, 0, 0, S(5), h);
            using (Font fCaps = App.F("Segoe UI", 8.5f, FontStyle.Bold), fBig = App.F("Segoe UI Semibold", high ? 22f : 17f),
                        fSmall = App.F("Segoe UI", high ? 10f : 9f), fRate = App.F("Segoe UI Semibold", 9.5f))
            {
                TextRenderer.DrawText(g, caps, fCaps, new Point(pad, S(16)), band.Color, TF);
                if (high)
                {
                    string r = "₡" + band.Rate.ToString("0.00", CI) + " / kWh";
                    int rw = TextRenderer.MeasureText(g, r, fRate, new Size(int.MaxValue, int.MaxValue), TF).Width;
                    TextRenderer.DrawText(g, r, fRate, new Point(w - pad - rw, S(15)), Muted, TF);
                }
                TextRenderer.DrawText(g, big, fBig, new Point(pad - S(1), S(high ? 36 : 34)), Fg, TF);
                TextRenderer.DrawText(g, small, fSmall, new Point(pad, S(high ? 84 : 72)), Muted, TF);
            }
            // How much longer the card stays up.
            float remaining = docs ? 0.7f : Math.Max(0f, 1f - (float)((DateTime.Now - shownAt).TotalSeconds / seconds));
            using (var track = new SolidBrush(Track)) g.FillRectangle(track, S(5), h - S(3), w, S(3));
            using (var bar = new SolidBrush(band.Color)) g.FillRectangle(bar, S(5), h - S(3), (w - S(5)) * remaining, S(3));
        }
    }
}
