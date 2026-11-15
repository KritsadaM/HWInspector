// HW Inspector Setup - self-contained installer / uninstaller.
// The application exe is embedded as a resource ("payload.exe").
// Per-user install (no admin rights): %LOCALAPPDATA%\Programs\HW Inspector
//   Setup.exe              interactive install
//   Setup.exe /S           silent install
//   uninstall.exe /uninstall [/S]
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("HW Inspector Setup")]
[assembly: AssemblyProduct("HW Inspector")]
[assembly: AssemblyCompany("HW Inspector")]
[assembly: AssemblyDescription("Installer for HW Inspector")]
[assembly: AssemblyVersion(HwSetup.Info.Version + ".0")]
[assembly: AssemblyFileVersion(HwSetup.Info.Version + ".0")]

namespace HwSetup
{
    static class Info
    {
        public const string Version = "1.1.0";
        public const string AppName = "HW Inspector";
        public const string ExeName = "HWInspector.exe";
        public const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\HWInspector";
        public static string DefaultDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName); } }
        public static string StartMenuLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); } }
        public static string DesktopLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); } }

        public static string InstalledDir()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(UninstKey))
                    return k == null ? null : k.GetValue("InstallLocation") as string;
            }
            catch { return null; }
        }

        public static string InstalledVersion()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(UninstKey))
                    return k == null ? null : k.GetValue("DisplayVersion") as string;
            }
            catch { return null; }
        }
    }

    static class Installer
    {
        public static void CloseRunning(string dir)
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Info.ExeName)))
            {
                try
                {
                    string path = p.MainModule.FileName;
                    if (dir != null && !path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
                    p.CloseMainWindow();
                    if (!p.WaitForExit(3000)) { p.Kill(); p.WaitForExit(3000); }
                }
                catch { }
            }
        }

        static void Shortcut(string link, string target, string dir)
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            object sh = Activator.CreateInstance(t);
            try
            {
                object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { link });
                var lt = lnk.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { dir });
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Hardware information and 3D mainboard viewer" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                Marshal.ReleaseComObject(lnk);
            }
            finally { Marshal.ReleaseComObject(sh); }
        }

        public static void Install(string dir, bool desktop, bool startMenu, Action<int, string> progress)
        {
            progress(5, "Closing running copies...");
            CloseRunning(null);

            progress(15, "Creating folder...");
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(dir, "layouts"));

            progress(30, "Copying " + Info.ExeName + "...");
            string exe = Path.Combine(dir, Info.ExeName);
            using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.exe"))
            {
                if (src == null) throw new InvalidOperationException("Installer is damaged: application payload missing.");
                using (var dst = File.Create(exe)) src.CopyTo(dst);
            }

            progress(50, "Copying uninstaller...");
            string uninst = Path.Combine(dir, "uninstall.exe");
            string self = Assembly.GetExecutingAssembly().Location;
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(uninst), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, uninst, true);

            progress(65, "Creating shortcuts...");
            if (startMenu) Shortcut(Info.StartMenuLink, exe, dir);
            else if (File.Exists(Info.StartMenuLink)) File.Delete(Info.StartMenuLink);
            if (desktop) Shortcut(Info.DesktopLink, exe, dir);
            else if (File.Exists(Info.DesktopLink)) File.Delete(Info.DesktopLink);

            progress(85, "Registering in Apps & features...");
            long kb = (new FileInfo(exe).Length + new FileInfo(uninst).Length) / 1024;
            using (var k = Registry.CurrentUser.CreateSubKey(Info.UninstKey))
            {
                k.SetValue("DisplayName", Info.AppName);
                k.SetValue("DisplayVersion", Info.Version);
                k.SetValue("Publisher", Info.AppName);
                k.SetValue("DisplayIcon", exe + ",0");
                k.SetValue("InstallLocation", dir);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("UninstallString", "\"" + uninst + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + uninst + "\" /uninstall /S");
                k.SetValue("EstimatedSize", (int)kb, RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            progress(100, "Done");
        }

        public static void Uninstall(string dir, bool keepLayouts)
        {
            CloseRunning(dir);
            foreach (var l in new[] { Info.StartMenuLink, Info.DesktopLink })
                try { if (File.Exists(l)) File.Delete(l); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(Info.UninstKey, false); } catch { }
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            try { File.Delete(Path.Combine(dir, Info.ExeName)); } catch { }
            string layouts = Path.Combine(dir, "layouts");
            if (!keepLayouts) try { Directory.Delete(layouts, true); } catch { }
            bool keep = keepLayouts && Directory.Exists(layouts) && Directory.EnumerateFileSystemEntries(layouts).Any();

            // uninstall.exe is running, so remove it (and the folder) after this process exits
            string cmd = "/c ping 127.0.0.1 -n 3 > nul & del /f /q \"" + Path.Combine(dir, "uninstall.exe") + "\"" +
                         (keep ? "" : " & rmdir /s /q \"" + dir + "\"");
            Process.Start(new ProcessStartInfo("cmd.exe", cmd)
            {
                CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath()
            });
        }
    }

    class SetupForm : Form
    {
        readonly float scale;
        readonly Panel body = new Panel();
        readonly TextBox txtDir = new TextBox();
        readonly CheckBox chkDesktop = new CheckBox(), chkStart = new CheckBox(), chkLaunch = new CheckBox();
        readonly ProgressBar bar = new ProgressBar();
        readonly Label lblStatus = new Label();
        readonly Button btnMain = new Button(), btnCancel = new Button();
        Icon appIcon;
        bool done;

        int Px(int v) { return (int)Math.Round(v * scale); }

        public SetupForm()
        {
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            try { appIcon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }
            Icon = appIcon;
            Text = Info.AppName + " Setup";
            Font = new Font("Segoe UI", 9.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Px(580), Px(420));
            BackColor = Color.White;

            var header = new Panel { Dock = DockStyle.Top, Height = Px(104) };
            header.Paint += PaintHeader;

            var footer = new Panel { Dock = DockStyle.Bottom, Height = Px(64), BackColor = Color.FromArgb(245, 246, 248) };
            footer.Paint += (s, e) => e.Graphics.DrawLine(new Pen(Color.FromArgb(225, 228, 232)), 0, 0, footer.Width, 0);
            btnMain.SetBounds(Px(580 - 24 - 120 - 10 - 100), Px(16), Px(120), Px(32));
            btnCancel.SetBounds(Px(580 - 24 - 100), Px(16), Px(100), Px(32));
            btnCancel.Text = "Cancel";
            btnCancel.Click += delegate { Close(); };
            btnMain.Click += OnMain;
            bar.SetBounds(Px(24), Px(22), Px(280), Px(18));
            bar.Visible = false;
            footer.Controls.AddRange(new Control[] { bar, btnMain, btnCancel });

            body.Dock = DockStyle.Fill;
            body.Padding = new Padding(Px(28), Px(18), Px(28), Px(8));
            Controls.Add(body);
            Controls.Add(footer);
            Controls.Add(header);
            AcceptButton = btnMain;
            BuildOptions();
            Shown += delegate
            {
                ActiveControl = btnMain;
                txtDir.SelectionStart = 0;
                txtDir.SelectionLength = 0;
            };
        }

        void PaintHeader(object sender, PaintEventArgs e)
        {
            var p = (Panel)sender;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var br = new LinearGradientBrush(p.ClientRectangle, Color.FromArgb(28, 44, 72), Color.FromArgb(12, 18, 30), 0f))
                g.FillRectangle(br, p.ClientRectangle);
            int isz = Px(64);
            if (appIcon != null)
                using (var big = new Icon(appIcon, new Size(256, 256)))
                using (var bmp = big.ToBitmap())
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bmp, new Rectangle(Px(24), (p.Height - isz) / 2, isz, isz));
                }
            using (var f1 = new Font("Segoe UI Semibold", 17f))
            using (var f2 = new Font("Segoe UI", 9.5f))
            {
                g.DrawString(Info.AppName, f1, Brushes.White, Px(104), Px(22));
                g.DrawString("Hardware information & interactive 3D mainboard viewer  ·  v" + Info.Version, f2,
                             new SolidBrush(Color.FromArgb(170, 200, 230)), Px(106), Px(60));
            }
        }

        void BuildOptions()
        {
            body.Controls.Clear();
            string existing = Info.InstalledDir();
            string oldVer = Info.InstalledVersion();

            var lblIntro = new Label
            {
                AutoSize = false, Dock = DockStyle.Top, Height = Px(44),
                Text = existing != null
                    ? "HW Inspector " + oldVer + " is already installed. Setup will upgrade it to version " + Info.Version + ". Your board layout files are kept."
                    : "Setup will install HW Inspector for your Windows account. No administrator rights are required."
            };
            var lblDir = new Label { Text = "Install location", AutoSize = true, Font = new Font("Segoe UI Semibold", 9.5f), Location = new Point(Px(28), Px(70)) };
            txtDir.SetBounds(Px(28), Px(94), Px(410), Px(28));
            txtDir.Text = existing ?? Info.DefaultDir;
            var btnBrowse = new Button { Text = "Browse...", FlatStyle = FlatStyle.System };
            btnBrowse.SetBounds(Px(446), Px(92), Px(106), Px(30));
            btnBrowse.Click += delegate
            {
                using (var dlg = new FolderBrowserDialog { Description = "Choose where to install HW Inspector", SelectedPath = txtDir.Text })
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                        txtDir.Text = dlg.SelectedPath.EndsWith(Info.AppName, StringComparison.OrdinalIgnoreCase) ? dlg.SelectedPath : Path.Combine(dlg.SelectedPath, Info.AppName);
            };
            chkDesktop.Text = "Create a desktop shortcut";
            chkStart.Text = "Add to the Start menu";
            chkLaunch.Text = "Launch HW Inspector when setup finishes";
            chkDesktop.Checked = chkStart.Checked = chkLaunch.Checked = true;
            int y = 140;
            foreach (var c in new[] { chkStart, chkDesktop, chkLaunch })
            {
                c.AutoSize = true;
                c.Location = new Point(Px(28), Px(y));
                y += 30;
            }
            var lblReq = new Label
            {
                AutoSize = true, ForeColor = Color.FromArgb(110, 115, 125), Location = new Point(Px(28), Px(236)),
                Text = "Requires Windows 10 / 11 (.NET Framework 4.x, built in).  Disk space: under 1 MB."
            };
            body.Controls.AddRange(new Control[] { lblDir, txtDir, btnBrowse, chkStart, chkDesktop, chkLaunch, lblReq });
            lblIntro.Location = new Point(Px(28), Px(16));
            lblIntro.Dock = DockStyle.None;
            lblIntro.Size = new Size(Px(524), Px(44));
            body.Controls.Add(lblIntro);
            btnMain.Text = existing != null ? "Upgrade" : "Install";
        }

        void OnMain(object sender, EventArgs e)
        {
            if (done) { Close(); return; }
            string dir = txtDir.Text.Trim();
            if (dir == "" || !Path.IsPathRooted(dir))
            {
                MessageBox.Show(this, "Please choose a valid folder.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            btnMain.Enabled = btnCancel.Enabled = false;
            body.Enabled = false;
            bar.Visible = true;
            bool desktop = chkDesktop.Checked, start = chkStart.Checked, launch = chkLaunch.Checked;
            var t = new Thread(() =>
            {
                Exception err = null;
                try { Installer.Install(dir, desktop, start, (pct, msg) => BeginInvoke((Action)(() => { bar.Value = pct; }))); }
                catch (Exception ex) { err = ex; }
                BeginInvoke((Action)(() => Finished(dir, launch, err)));
            });
            t.IsBackground = true;
            t.Start();
        }

        void Finished(string dir, bool launch, Exception err)
        {
            bar.Visible = false;
            btnCancel.Enabled = true;
            if (err != null)
            {
                body.Enabled = true;
                btnMain.Enabled = true;
                MessageBox.Show(this, "Installation failed:\n\n" + err.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            done = true;
            body.Enabled = true;
            body.Controls.Clear();
            var ok = new Label
            {
                AutoSize = false, Location = new Point(Px(28), Px(24)), Size = new Size(Px(524), Px(40)),
                Font = new Font("Segoe UI Semibold", 13f), ForeColor = Color.FromArgb(30, 120, 60),
                Text = "Installation complete"
            };
            var info = new Label
            {
                AutoSize = false, Location = new Point(Px(28), Px(72)), Size = new Size(Px(524), Px(150)),
                Text = "HW Inspector " + Info.Version + " was installed to:\n" + dir +
                       "\n\nOpen it from the Start menu" + (chkDesktop.Checked ? " or the desktop shortcut" : "") + ".\n" +
                       "To remove it: Settings > Apps > Installed apps > HW Inspector > Uninstall."
            };
            body.Controls.Add(ok);
            body.Controls.Add(info);
            btnMain.Text = "Finish";
            btnMain.Enabled = true;
            btnCancel.Visible = false;
            if (launch)
                try { Process.Start(new ProcessStartInfo(Path.Combine(dir, Info.ExeName)) { WorkingDirectory = dir }); } catch { }
        }
    }

    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            bool silent = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase) || a.Equals("/silent", StringComparison.OrdinalIgnoreCase));
            bool uninstall = args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)) ||
                             Path.GetFileName(Assembly.GetExecutingAssembly().Location).Equals("uninstall.exe", StringComparison.OrdinalIgnoreCase);
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (uninstall)
            {
                string dir = Info.InstalledDir() ?? Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                bool keepLayouts = false;
                if (!silent)
                {
                    if (MessageBox.Show("Remove HW Inspector from this computer?", "Uninstall HW Inspector",
                                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 1;
                    string layouts = Path.Combine(dir, "layouts");
                    if (Directory.Exists(layouts) && Directory.EnumerateFiles(layouts).Any())
                        keepLayouts = MessageBox.Show("Keep your custom board layout files?\n(" + layouts + ")", "Uninstall HW Inspector",
                                                      MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                }
                try { Installer.Uninstall(dir, keepLayouts); }
                catch (Exception ex)
                {
                    if (!silent) MessageBox.Show("Uninstall failed: " + ex.Message, "Uninstall HW Inspector", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 2;
                }
                if (!silent) MessageBox.Show("HW Inspector has been removed.", "Uninstall HW Inspector", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            if (silent)
            {
                try { Installer.Install(Info.InstalledDir() ?? Info.DefaultDir, true, true, (p, m) => { }); return 0; }
                catch { return 2; }
            }
            Application.Run(new SetupForm());
            return 0;
        }
    }
}
