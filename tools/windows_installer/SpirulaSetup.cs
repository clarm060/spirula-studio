// Per-user installer, launcher and updater for the Windows release zip.
// One executable: run from anywhere it installs; run from the install
// directory it is the launcher the shortcuts point at. C# 5 so the csc.exe
// that ships with Windows (.NET Framework 4.x) builds it. See README.md.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Spirula Studio Setup")]
[assembly: System.Reflection.AssemblyProduct("Spirula Studio")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace SpirulaSetup {

class Release {
    public string Tag;
    public string Notes;
    public string ZipUrl;
    public long ZipSize;
}

static class Paths {
    public const string AppName = "Spirula Studio";
    public const string UpdateRepo = "harry7557558/spirula-studio";
    public const string AssetSuffix = "-windows-vulkan-x86_64.zip";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SpirulaStudio";
    public const string LauncherName = "SpirulaLauncher.exe";
    public const string AppExe = "spirula.exe";

    public static string InstallDir {
        get {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "Programs", AppName);
        }
    }
    public static string Launcher { get { return Path.Combine(InstallDir, LauncherName); } }
    public static string App { get { return Path.Combine(InstallDir, AppExe); } }
    public static string StartMenuDir {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName); }
    }
    public static string DesktopLink {
        get {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                                AppName + ".lnk");
        }
    }
    public static bool RunningInstalled {
        get {
            return string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(Launcher),
                                 StringComparison.OrdinalIgnoreCase);
        }
    }
}

// key=value lines beside the launcher; small enough that a format is overkill.
class Settings {
    readonly Dictionary<string, string> _v = new Dictionary<string, string>();
    static string FilePath { get { return Path.Combine(Paths.InstallDir, "launcher.cfg"); } }

    public static Settings Load() {
        var s = new Settings();
        try {
            foreach (var line in File.ReadAllLines(FilePath)) {
                int eq = line.IndexOf('=');
                if (eq > 0) s._v[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
        return s;
    }
    public void Save() {
        var sb = new StringBuilder();
        foreach (var kv in _v) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
        File.WriteAllText(FilePath, sb.ToString());
    }
    public string Get(string k, string def) { string v; return _v.TryGetValue(k, out v) ? v : def; }
    public void Set(string k, string v) { _v[k] = v; }

    public string InstalledTag { get { return Get("installed", ""); } set { Set("installed", value); } }
    public string SkippedTag { get { return Get("skip", ""); } set { Set("skip", value); } }
    public bool AutoCheck { get { return Get("auto_check", "1") == "1"; } set { Set("auto_check", value ? "1" : "0"); } }
    public DateTime LastCheck {
        get {
            long t;
            return long.TryParse(Get("last_check", "0"), out t) ? new DateTime(t, DateTimeKind.Utc) : DateTime.MinValue;
        }
        set { Set("last_check", value.Ticks.ToString()); }
    }
}

static class GitHub {
    static GitHub() {
        try {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 12288);  // TLS 1.2 | 1.3
        } catch (NotSupportedException) {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        }
    }

    static HttpWebRequest Request(string url, int timeoutMs) {
        var r = (HttpWebRequest)WebRequest.Create(url);
        r.UserAgent = "SpirulaStudio-Launcher";  // the GitHub API rejects requests without one
        r.Timeout = timeoutMs;
        r.ReadWriteTimeout = Math.Max(timeoutMs, 30000);
        return r;
    }

    public static Release Latest(int timeoutMs) {
        var r = Request("https://api.github.com/repos/" + Paths.UpdateRepo + "/releases/latest", timeoutMs);
        r.Accept = "application/vnd.github+json";
        string json;
        using (var resp = r.GetResponse())
        using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) json = sr.ReadToEnd();

        var o = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(json);
        var rel = new Release { Tag = (string)o["tag_name"], Notes = o["body"] as string ?? "" };
        foreach (var a in (System.Collections.ArrayList)o["assets"]) {
            var asset = (Dictionary<string, object>)a;
            var name = (string)asset["name"];
            if (name.EndsWith(Paths.AssetSuffix, StringComparison.OrdinalIgnoreCase)) {
                rel.ZipUrl = (string)asset["browser_download_url"];
                rel.ZipSize = Convert.ToInt64(asset["size"]);
            }
        }
        if (rel.ZipUrl == null) throw new Exception("Release " + rel.Tag + " has no Windows build attached yet.");
        return rel;
    }

    public static void Download(string url, string dest, Action<long, long> progress) {
        var r = Request(url, 30000);
        using (var resp = r.GetResponse())
        using (var src = resp.GetResponseStream())
        using (var dst = File.Create(dest)) {
            long total = resp.ContentLength, done = 0;
            var buf = new byte[1 << 16];
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0) {
                dst.Write(buf, 0, n);
                done += n;
                progress(done, total);
            }
        }
    }

    // Tags are vYYYY.M.D; compare numerically so 2026.10.1 > 2026.9.30.
    public static int Compare(string a, string b) {
        var pa = Parts(a);
        var pb = Parts(b);
        for (int i = 0; i < Math.Max(pa.Count, pb.Count); i++) {
            long x = i < pa.Count ? pa[i] : 0, y = i < pb.Count ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }
    static List<long> Parts(string tag) {
        var l = new List<long>();
        foreach (var p in (tag ?? "").TrimStart('v', 'V').Split('.', '-', ' ')) {
            long n;
            if (long.TryParse(p, out n)) l.Add(n);
        }
        return l;
    }
}

static class Installer {
    // Installs or updates spirula.exe from the release zip. A running
    // spirula.exe cannot be overwritten but can be renamed, so the old one is
    // moved aside and swept up on a later launch.
    public static void InstallRelease(Release rel, Action<string, int> report) {
        Directory.CreateDirectory(Paths.InstallDir);
        string tmp = Path.Combine(Path.GetTempPath(), "spirula-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try {
            string zip = Path.Combine(tmp, "release.zip");
            report("Downloading " + rel.Tag + "...", 0);
            GitHub.Download(rel.ZipUrl, zip, (done, total) => {
                if (total <= 0) total = rel.ZipSize;
                report(string.Format("Downloading {0}... {1:0.0} / {2:0.0} MB", rel.Tag, done / 1048576.0, total / 1048576.0),
                       total > 0 ? (int)(done * 90 / total) : 0);
            });

            report("Extracting...", 92);
            string staged = Path.Combine(tmp, "x");
            ZipFile.ExtractToDirectory(zip, staged);
            string[] found = Directory.GetFiles(staged, Paths.AppExe, SearchOption.AllDirectories);
            if (found.Length == 0) throw new Exception("The release zip does not contain " + Paths.AppExe + ".");
            string root = Path.GetDirectoryName(found[0]);

            report("Installing...", 96);
            foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) {
                string dest = Path.Combine(Paths.InstallDir, f.Substring(root.Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                if (File.Exists(dest)) {
                    try {
                        File.Delete(dest);
                    } catch (Exception) {
                        string aside = dest + "." + DateTime.Now.Ticks + ".old";
                        File.Move(dest, aside);
                    }
                }
                File.Move(f, dest);
            }
            var s = Settings.Load();
            s.InstalledTag = rel.Tag;
            s.SkippedTag = "";
            s.Save();
            WriteUninstallEntry(rel.Tag);
            report("Installed " + rel.Tag + ".", 100);
        } finally {
            try { Directory.Delete(tmp, true); } catch (Exception) { }
        }
    }

    public static void FullInstall(bool desktop, bool openWith, bool autoCheck, Action<string, int> report) {
        report("Looking up the latest release...", 0);
        Release rel = GitHub.Latest(15000);
        CopyLauncher();
        InstallRelease(rel, report);
        CreateShortcuts(desktop);
        if (openWith) RegisterShell();
        var s = Settings.Load();
        s.AutoCheck = autoCheck;
        s.LastCheck = DateTime.UtcNow;
        s.Save();
    }

    public static void SweepOldFiles() {
        try {
            foreach (var f in Directory.GetFiles(Paths.InstallDir, "*.old"))
                try { File.Delete(f); } catch (Exception) { }
        } catch (Exception) { }
    }

    public static void CopyLauncher() {
        string self = Path.GetFullPath(Application.ExecutablePath);
        if (string.Equals(self, Path.GetFullPath(Paths.Launcher), StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(Paths.InstallDir);
        if (File.Exists(Paths.Launcher)) {
            try { File.Delete(Paths.Launcher); } catch (Exception) { File.Move(Paths.Launcher, Paths.Launcher + "." + DateTime.Now.Ticks + ".old"); }
        }
        File.Copy(self, Paths.Launcher);
    }

    public static void CreateShortcuts(bool desktop) {
        Directory.CreateDirectory(Paths.StartMenuDir);
        Shortcut(Path.Combine(Paths.StartMenuDir, Paths.AppName + ".lnk"), "",
                 "Train 3D Gaussian Splats from photos and video");
        Shortcut(Path.Combine(Paths.StartMenuDir, "Check for Updates.lnk"), "--update",
                 "Check GitHub for a newer Spirula Studio release");
        if (desktop) Shortcut(Paths.DesktopLink, "", "Spirula Studio");
        else if (File.Exists(Paths.DesktopLink)) File.Delete(Paths.DesktopLink);
    }

    static void Shortcut(string lnk, string args, string description) {
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        dynamic shell = Activator.CreateInstance(t);
        try {
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = Paths.Launcher;
            sc.Arguments = args;
            sc.WorkingDirectory = Paths.InstallDir;
            sc.IconLocation = Paths.Launcher + ",0";
            sc.Description = description;
            sc.Save();
            Marshal.FinalReleaseComObject(sc);
        } finally {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    static readonly string[] OpenWithTypes = {
        ".ply", ".e57", ".las", ".laz", ".obj", ".glb", ".gltf", ".mp4", ".mov", ".insv"
    };

    // "Open with" entries only: the installer never takes a file type's default.
    public static void RegisterShell() {
        string open = "\"" + Paths.Launcher + "\" \"%1\"";
        using (var app = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\" + Paths.LauncherName)) {
            app.SetValue("FriendlyAppName", Paths.AppName);
            using (var cmd = app.CreateSubKey(@"shell\open\command")) cmd.SetValue("", open);
            using (var types = app.CreateSubKey("SupportedTypes"))
                foreach (var ext in OpenWithTypes) types.SetValue(ext, "");
        }
        foreach (var ext in OpenWithTypes)
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ext + @"\OpenWithList\" + Paths.LauncherName)) { }
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\SpirulaStudio")) {
            k.SetValue("", "Open in Spirula Studio");
            k.SetValue("Icon", Paths.Launcher + ",0");
            using (var cmd = k.CreateSubKey("command")) cmd.SetValue("", open);
        }
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);  // SHCNE_ASSOCCHANGED
    }

    static void UnregisterShell() {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\" + Paths.LauncherName, false);
        foreach (var ext in OpenWithTypes)
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ext + @"\OpenWithList\" + Paths.LauncherName, false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\shell\SpirulaStudio", false);
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

    static void WriteUninstallEntry(string tag) {
        using (var k = Registry.CurrentUser.CreateSubKey(Paths.UninstallKey)) {
            k.SetValue("DisplayName", Paths.AppName);
            k.SetValue("DisplayVersion", tag.TrimStart('v'));
            k.SetValue("Publisher", "Spirula Studio contributors");
            k.SetValue("DisplayIcon", Paths.Launcher + ",0");
            k.SetValue("InstallLocation", Paths.InstallDir);
            k.SetValue("UninstallString", "\"" + Paths.Launcher + "\" --uninstall");
            k.SetValue("URLInfoAbout", "https://github.com/" + Paths.UpdateRepo);
            k.SetValue("URLUpdateInfo", "https://github.com/" + Paths.UpdateRepo + "/releases");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            long bytes = 0;
            foreach (var f in Directory.GetFiles(Paths.InstallDir, "*", SearchOption.AllDirectories)) bytes += new FileInfo(f).Length;
            k.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
        }
    }

    public static void Uninstall() {
        UnregisterShell();
        try { Directory.Delete(Paths.StartMenuDir, true); } catch (Exception) { }
        try { File.Delete(Paths.DesktopLink); } catch (Exception) { }
        Registry.CurrentUser.DeleteSubKeyTree(Paths.UninstallKey, false);
        // The running launcher cannot delete itself; a detached cmd removes
        // the directory once this process has exited.
        Process.Start(new ProcessStartInfo("cmd.exe",
            "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + Paths.InstallDir + "\"") {
            CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath()
        });
    }

    public static bool AppRunning() {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Paths.AppExe))) {
            try {
                if (string.Equals(p.MainModule.FileName, Paths.App, StringComparison.OrdinalIgnoreCase)) return true;
            } catch (Exception) { }
        }
        return false;
    }

    // spirula.exe is a console-subsystem binary whose GUI frees its own
    // console; starting it with no window keeps a console from flashing up.
    public static void LaunchApp(string[] args) {
        var sb = new StringBuilder();
        foreach (var a in args) {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(Quote(a));
        }
        Process.Start(new ProcessStartInfo(Paths.App, sb.ToString()) {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Paths.InstallDir
        });
    }

    // CommandLineToArgvW rules: backslashes double only before a quote, so
    // D:\ (a drive root from the folder menu) survives.
    static string Quote(string a) {
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in a) {
            if (c == '\\') { slashes++; continue; }
            sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', slashes * 2);
        return sb.Append('"').ToString();
    }
}

class ProgressForm : Form {
    readonly Label _status = new Label { AutoSize = false, Dock = DockStyle.Top, Height = 40, TextAlign = ContentAlignment.MiddleLeft };
    readonly ProgressBar _bar = new ProgressBar { Dock = DockStyle.Top, Height = 22, Maximum = 100 };
    public Exception Error;
    bool _done;

    public ProgressForm(string title, Action<Action<string, int>> work) {
        Text = title;
        Icon = Program.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(440, 90);
        Padding = new Padding(14);
        Controls.Add(_bar);
        Controls.Add(_status);
        Shown += delegate {
            new Thread(() => {
                try {
                    work((msg, pct) => BeginInvoke((Action)delegate {
                        _status.Text = msg;
                        _bar.Value = Math.Max(0, Math.Min(100, pct));
                    }));
                } catch (Exception e) {
                    Error = e;
                }
                BeginInvoke((Action)delegate { _done = true; Close(); });
            }) { IsBackground = true }.Start();
        };
        FormClosing += (s, e) => { if (!_done) e.Cancel = true; };
    }
}

class SetupForm : Form {
    readonly CheckBox _desktop = new CheckBox { Text = "Create a desktop shortcut", Checked = true, AutoSize = true };
    readonly CheckBox _openWith = new CheckBox { Text = "Add to \"Open with\" and the folder right-click menu", Checked = true, AutoSize = true };
    readonly CheckBox _autoCheck = new CheckBox { Text = "Check for new versions when Spirula Studio starts", Checked = true, AutoSize = true };
    readonly CheckBox _launch = new CheckBox { Text = "Launch Spirula Studio when done", Checked = true, AutoSize = true };
    public bool Installed;

    public SetupForm() {
        bool existing = File.Exists(Paths.App);
        Text = Paths.AppName + " Setup";
        Icon = Program.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var layout = new FlowLayoutPanel {
            FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(16), WrapContents = false
        };
        var header = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
        header.Controls.Add(new PictureBox {
            Image = new Icon(Program.AppIcon, 48, 48).ToBitmap(), Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.Zoom
        });
        header.Controls.Add(new Label {
            Text = Paths.AppName, Font = new Font(Font.FontFamily, 16f, FontStyle.Bold), AutoSize = true,
            Margin = new Padding(8, 10, 0, 0)
        });
        layout.Controls.Add(header);
        layout.Controls.Add(new Label {
            AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(0, 0, 0, 10),
            Text = (existing ? "Spirula Studio is already installed. Continuing reinstalls the latest release.\n\n" : "") +
                   "Downloads the latest Windows release from github.com/" + Paths.UpdateRepo +
                   " and installs it for your user account (no administrator rights needed) to:\n" + Paths.InstallDir
        });
        layout.Controls.Add(_desktop);
        layout.Controls.Add(_openWith);
        layout.Controls.Add(_autoCheck);
        layout.Controls.Add(_launch);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 14, 0, 0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var install = new Button { Text = existing ? "Reinstall" : "Install", AutoSize = true };
        install.Click += delegate { DoInstall(); };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(install);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = install;
        CancelButton = cancel;
    }

    void DoInstall() {
        if (Installer.AppRunning()) {
            MessageBox.Show(this, "Close Spirula Studio before installing.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        bool desktop = _desktop.Checked, openWith = _openWith.Checked, autoCheck = _autoCheck.Checked;
        var p = new ProgressForm("Installing " + Paths.AppName,
                                 report => Installer.FullInstall(desktop, openWith, autoCheck, report));
        Hide();
        p.ShowDialog();
        if (p.Error != null) {
            MessageBox.Show("Installation failed:\n\n" + p.Error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Show();
            return;
        }
        Installed = true;
        if (_launch.Checked) Installer.LaunchApp(new string[0]);
        else MessageBox.Show(Paths.AppName + " is installed. Find it in the Start menu.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        Close();
    }
}

class UpdateForm : Form {
    public enum Choice { Later, Update, Skip }
    public Choice Result = Choice.Later;

    public UpdateForm(string current, Release rel) {
        Text = Paths.AppName + " Update";
        Icon = Program.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        ClientSize = new Size(560, 420);
        MinimumSize = new Size(420, 300);
        Padding = new Padding(12);

        var head = new Label {
            Dock = DockStyle.Top, Height = 44, Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold),
            Text = "Spirula Studio " + rel.Tag + " is available." +
                   (string.IsNullOrEmpty(current) ? "" : "  You have " + current + ".")
        };
        var notes = new TextBox {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Text = rel.Notes.Replace("\r\n", "\n").Replace("\n", "\r\n"), BackColor = SystemColors.Window
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 10, 0, 0) };
        var later = new Button { Text = "Remind me later", AutoSize = true };
        var skip = new Button { Text = "Skip this version", AutoSize = true };
        var update = new Button { Text = "Update now", AutoSize = true };
        later.Click += delegate { Result = Choice.Later; Close(); };
        skip.Click += delegate { Result = Choice.Skip; Close(); };
        update.Click += delegate { Result = Choice.Update; Close(); };
        buttons.Controls.Add(later);
        buttons.Controls.Add(skip);
        buttons.Controls.Add(update);
        Controls.Add(notes);
        Controls.Add(buttons);
        Controls.Add(head);
        AcceptButton = update;
        CancelButton = later;
        Shown += delegate { update.Focus(); notes.SelectionLength = 0; };
    }
}

static class Program {
    static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    public static Icon AppIcon;

    [STAThread]
    static int Main(string[] args) {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var st = typeof(Program).Assembly.GetManifestResourceStream("app.ico")) AppIcon = new Icon(st);

        string mode = args.Length > 0 && args[0].StartsWith("--") ? args[0] : "";
        if (mode != "" && Array.IndexOf(args, "--quiet") > 0) return Quiet(mode);
        if (mode == "--uninstall") return Uninstall();
        if (mode == "--install" || !Paths.RunningInstalled) {
            var f = new SetupForm();
            Application.Run(f);
            return f.Installed ? 0 : 1;
        }
        if (mode == "--update") {
            CheckForUpdate(true);
            return 0;
        }
        return Launch(args);
    }

    // --install/--update/--uninstall --quiet: no UI, for scripts and package
    // managers. Failures go to %TEMP%\SpirulaSetup.log and the exit code.
    static int Quiet(string mode) {
        Action<string, int> none = (m, p) => { };
        try {
            if (mode == "--install") {
                if (Installer.AppRunning()) throw new Exception("Spirula Studio is running.");
                Installer.FullInstall(true, true, true, none);
            } else if (mode == "--update") {
                var s = Settings.Load();
                Release rel = GitHub.Latest(15000);
                s.LastCheck = DateTime.UtcNow;
                s.Save();
                if (GitHub.Compare(rel.Tag, s.InstalledTag) <= 0) return 0;
                if (Installer.AppRunning()) throw new Exception("Spirula Studio is running.");
                Installer.InstallRelease(rel, none);
            } else if (mode == "--uninstall") {
                if (Installer.AppRunning()) throw new Exception("Spirula Studio is running.");
                Installer.Uninstall();
            } else {
                throw new Exception("Unknown option " + mode);
            }
            return 0;
        } catch (Exception e) {
            try {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "SpirulaSetup.log"),
                                   DateTime.Now.ToString("s") + " " + mode + ": " + e + "\r\n");
            } catch (Exception) { }
            return 1;
        }
    }

    static int Launch(string[] args) {
        Installer.SweepOldFiles();
        if (!File.Exists(Paths.App)) {
            var f = new SetupForm();
            Application.Run(f);
            return f.Installed ? 0 : 1;
        }
        var s = Settings.Load();
        if (s.AutoCheck && DateTime.UtcNow - s.LastCheck > CheckInterval && !Installer.AppRunning())
            CheckForUpdate(false);
        Installer.LaunchApp(args);
        return 0;
    }

    // interactive: the user asked, so report "up to date" and errors, and
    // ignore a skipped version. Otherwise stay silent unless there is news.
    static void CheckForUpdate(bool interactive) {
        var s = Settings.Load();
        Release rel;
        try {
            rel = GitHub.Latest(interactive ? 15000 : 4000);
        } catch (Exception e) {
            if (interactive)
                MessageBox.Show("Could not check for updates:\n\n" + e.Message, Paths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        s.LastCheck = DateTime.UtcNow;
        s.Save();

        bool newer = string.IsNullOrEmpty(s.InstalledTag) || GitHub.Compare(rel.Tag, s.InstalledTag) > 0;
        if (!newer || (!interactive && rel.Tag == s.SkippedTag)) {
            if (interactive)
                MessageBox.Show("Spirula Studio " + s.InstalledTag + " is the latest version.", Paths.AppName,
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var f = new UpdateForm(s.InstalledTag, rel);
        f.ShowDialog();
        if (f.Result == UpdateForm.Choice.Skip) {
            s.SkippedTag = rel.Tag;
            s.Save();
            return;
        }
        if (f.Result != UpdateForm.Choice.Update) return;
        if (Installer.AppRunning()) {
            MessageBox.Show("Close Spirula Studio, then choose Check for Updates from the Start menu.", Paths.AppName,
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var p = new ProgressForm("Updating " + Paths.AppName, report => Installer.InstallRelease(rel, report));
        p.ShowDialog();
        if (p.Error != null)
            MessageBox.Show("Update failed, the current version is unchanged:\n\n" + p.Error.Message, Paths.AppName,
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
        else if (interactive)
            Installer.LaunchApp(new string[0]);
    }

    static int Uninstall() {
        if (MessageBox.Show("Remove Spirula Studio from this computer?\n\nSettings and caches in AppData are kept.",
                            Paths.AppName + " Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return 1;
        if (Installer.AppRunning()) {
            MessageBox.Show("Close Spirula Studio first.", Paths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }
        Installer.Uninstall();
        return 0;
    }
}

}
