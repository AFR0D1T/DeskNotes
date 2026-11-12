// DeskNotes installer / uninstaller. One small exe with DeskNotes.exe embedded inside.
//   DeskNotes-Setup.exe              -> install window
//   DeskNotes-Setup.exe /uninstall   -> remove (this is what "Apps -> Uninstall" in Windows runs)
// Installs per user (no admin rights): %LOCALAPPDATA%\Programs\DeskNotes. Notes stay in %APPDATA%\DeskNotes.

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Установка DeskNotes")]
[assembly: AssemblyProduct("DeskNotes")]
[assembly: AssemblyVersion("2.1.0.0")]
[assembly: AssemblyFileVersion("2.1.0.0")]

class Setup : Form
{
    const string Version = "2.1.0";
    const string VoskLibUrl = "https://github.com/alphacep/vosk-api/releases/download/v0.3.45/vosk-win64-0.3.45.zip";
    const string VoskModelUrl = "https://alphacephei.com/vosk/models/vosk-model-small-ru-0.22.zip";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DeskNotes";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DeskNotes");
    static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskNotes");
    static readonly string VoskDir = Path.Combine(DataDir, "vosk");
    static readonly string StartMenuLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "DeskNotes.lnk");
    static string AppExe { get { return Path.Combine(InstallDir, "DeskNotes.exe"); } }
    static bool VoiceInstalled { get { return File.Exists(Path.Combine(VoskDir, "lib", "libvosk.dll")) && Directory.Exists(Path.Combine(VoskDir, "model", "am")); } }

    readonly Color bg = Color.FromArgb(58, 59, 63), fg = Color.FromArgb(236, 234, 228), dim = Color.FromArgb(160, 160, 160), accent = Color.FromArgb(216, 67, 63);
    CheckBox cVoice, cAuto, cMenu;
    ProgressBar bar;
    Label status;
    Button go, cancel;
    bool busy;

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Func<string, bool> has = f => args.Any(a => a.Equals(f, StringComparison.OrdinalIgnoreCase));
        if (has("/uninstall")) { Uninstall(); return; }
        if (has("/silent"))
        {
            // DeskNotes-Setup.exe /silent [/novoice] [/noautostart] [/nomenu] — install or update without a window
            try { new Setup().Install(!has("/novoice") && !VoiceInstalled, !has("/noautostart"), !has("/nomenu")); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.Exit(1); }
            return;
        }
        Application.Run(new Setup());
    }

    Setup()
    {
        Text = "Установка DeskNotes " + Version;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = bg; ForeColor = fg;
        Font = new Font("Segoe UI", 10f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(480, 360);
        Padding = new Padding(24);

        var title = new Label { Text = "DeskNotes", Font = new Font("Segoe Print", 20f, FontStyle.Bold), AutoSize = true, Location = new Point(22, 14) };
        var sub = new Label
        {
            Text = (File.Exists(AppExe) ? "Обновление до версии " : "Версия ") + Version + ". Рисованный стикер с делами, заметками и рисунками.\n" +
                   "Без значков, не видно на стримах, в простое не нагружает компьютер.",
            ForeColor = dim, Location = new Point(24, 66), Size = new Size(440, 44)
        };
        bool voice = VoiceInstalled;
        cVoice = new CheckBox
        {
            Text = voice ? "Голосовой ввод (уже установлен)" : "Голосовой ввод — офлайн-распознавание русской речи (скачается ~60 МБ)",
            Checked = true, Enabled = !voice, Location = new Point(26, 122), Size = new Size(440, 40)
        };
        cAuto = new CheckBox { Text = "Запускать вместе с Windows", Checked = true, Location = new Point(26, 166), AutoSize = true };
        cMenu = new CheckBox { Text = "Ярлык в меню «Пуск»", Checked = true, Location = new Point(26, 196), AutoSize = true };
        bar = new ProgressBar { Location = new Point(26, 244), Size = new Size(428, 10), Visible = false };
        status = new Label { Location = new Point(24, 260), Size = new Size(440, 44), ForeColor = dim, Text = "Папка: " + InstallDir };
        go = new Button { Text = File.Exists(AppExe) ? "Обновить" : "Установить", Location = new Point(244, 310), Size = new Size(110, 34), FlatStyle = FlatStyle.Flat, BackColor = accent, ForeColor = Color.White };
        go.FlatAppearance.BorderSize = 0;
        cancel = new Button { Text = "Отмена", Location = new Point(362, 310), Size = new Size(92, 34), FlatStyle = FlatStyle.Flat, BackColor = bg, ForeColor = fg };
        cancel.FlatAppearance.BorderColor = dim;
        go.Click += delegate { if (go.Text == "Готово") Close(); else StartInstall(); };
        cancel.Click += delegate { Close(); };
        FormClosing += (s, e) => { if (busy) e.Cancel = true; };
        AcceptButton = go;
        Controls.AddRange(new Control[] { title, sub, cVoice, cAuto, cMenu, bar, status, go, cancel });
    }

    void Status(string text, int percent = -1)
    {
        if (!IsHandleCreated) return; // silent mode: no window
        BeginInvoke(new Action(() =>
        {
            status.Text = text;
            if (percent >= 0) { bar.Style = ProgressBarStyle.Continuous; bar.Value = Math.Min(100, percent); }
            else bar.Style = ProgressBarStyle.Marquee;
        }));
    }

    void StartInstall()
    {
        busy = true;
        go.Enabled = cancel.Enabled = cVoice.Enabled = cAuto.Enabled = cMenu.Enabled = false;
        bar.Visible = true;
        bool voice = cVoice.Checked && !VoiceInstalled, auto = cAuto.Checked, menu = cMenu.Checked;
        new Thread(() =>
        {
            string error = null;
            try { Install(voice, auto, menu); }
            catch (Exception ex) { error = ex.Message; }
            BeginInvoke(new Action(() =>
            {
                busy = false;
                if (error != null)
                {
                    status.ForeColor = Color.FromArgb(255, 140, 120);
                    status.Text = "Не получилось: " + error;
                    go.Text = "Повторить"; go.Enabled = cancel.Enabled = true;
                    return;
                }
                bar.Style = ProgressBarStyle.Continuous; bar.Value = 100;
                status.ForeColor = fg;
                status.Text = "Готово! Стикер уже на рабочем столе.\nCtrl+Alt+N — достать его, Esc — убрать обратно.";
                go.Text = "Готово"; go.Enabled = true; cancel.Visible = false;
            }));
        }) { IsBackground = true }.Start();
    }

    void Install(bool voice, bool auto, bool menu)
    {
        Status("Закрываю запущенный стикер…");
        StopApp();

        Status("Копирую программу…");
        Directory.CreateDirectory(InstallDir);
        using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("DeskNotes.exe"))
        using (var f = File.Create(AppExe)) res.CopyTo(f);
        File.WriteAllText(AppExe + ".config",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<configuration>\r\n  <startup><supportedRuntime version=\"v4.0\"/></startup>\r\n" +
            "  <runtime><gcConcurrent enabled=\"false\"/></runtime>\r\n</configuration>\r\n");
        string me = Application.ExecutablePath, uninstaller = Path.Combine(InstallDir, "Uninstall.exe");
        if (!string.Equals(me, uninstaller, StringComparison.OrdinalIgnoreCase)) File.Copy(me, uninstaller, true);

        if (voice) InstallVoice();

        Status("Настраиваю…");
        if (menu) CreateShortcut(StartMenuLink, AppExe);
        else if (File.Exists(StartMenuLink)) File.Delete(StartMenuLink);
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (auto) k.SetValue("DeskNotes", "\"" + AppExe + "\"");
            else k.DeleteValue("DeskNotes", false);
        }
        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "DeskNotes");
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "DeskNotes");
            k.SetValue("DisplayIcon", AppExe);
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(DirSize(InstallDir) / 1024 + (Directory.Exists(VoskDir) ? DirSize(VoskDir) / 1024 : 0)), RegistryValueKind.DWord);
        }

        Status("Запускаю…");
        Process.Start(new ProcessStartInfo(AppExe) { WorkingDirectory = InstallDir });
    }

    void InstallVoice()
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        string tmp = Path.Combine(Path.GetTempPath(), "DeskNotes-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string libZip = Path.Combine(tmp, "lib.zip"), modelZip = Path.Combine(tmp, "model.zip");
            Download(VoskLibUrl, libZip, "Скачиваю движок распознавания", 0, 25);
            Download(VoskModelUrl, modelZip, "Скачиваю русскую модель речи", 25, 90);

            Status("Распаковываю голосовой ввод…", 92);
            string lib = Path.Combine(VoskDir, "lib"), model = Path.Combine(VoskDir, "model");
            if (Directory.Exists(lib)) Directory.Delete(lib, true);
            if (Directory.Exists(model)) Directory.Delete(model, true);
            Directory.CreateDirectory(lib);
            using (var z = ZipFile.OpenRead(libZip))
                foreach (var e in z.Entries.Where(e => e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                    e.ExtractToFile(Path.Combine(lib, e.Name), true);
            using (var z = ZipFile.OpenRead(modelZip))
                foreach (var e in z.Entries)
                {
                    if (e.Name.Length == 0) continue; // folder entry
                    int slash = e.FullName.IndexOf('/');
                    string rel = slash >= 0 ? e.FullName.Substring(slash + 1) : e.FullName; // drop the top "vosk-model-small-ru-0.22/" folder
                    string dest = Path.GetFullPath(Path.Combine(model, rel.Replace('/', '\\')));
                    if (!dest.StartsWith(model, StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    e.ExtractToFile(dest, true);
                }
            if (!VoiceInstalled) throw new Exception("голосовой модуль распаковался не полностью");
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    void Download(string url, string file, string what, int from, int to)
    {
        var done = new ManualResetEvent(false);
        Exception err = null;
        using (var wc = new WebClient())
        {
            wc.Headers[HttpRequestHeader.UserAgent] = "DeskNotes-Setup";
            wc.DownloadProgressChanged += (s, e) =>
            {
                double mb = e.BytesReceived / 1048576.0, total = e.TotalBytesToReceive > 0 ? e.TotalBytesToReceive / 1048576.0 : 0;
                int pct = e.TotalBytesToReceive > 0 ? from + (int)((to - from) * e.BytesReceived / e.TotalBytesToReceive) : from;
                Status(string.Format("{0}… {1:0.0} из {2:0.0} МБ", what, mb, total), pct);
            };
            wc.DownloadFileCompleted += (s, e) => { err = e.Error; done.Set(); };
            wc.DownloadFileAsync(new Uri(url), file);
            done.WaitOne();
        }
        if (err != null) throw new Exception("не удалось скачать голосовой модуль (" + err.Message + "). Проверь интернет или сними галочку «Голосовой ввод»");
    }

    static void StopApp()
    {
        foreach (var p in Process.GetProcessesByName("DeskNotes"))
        {
            try { p.Kill(); p.WaitForExit(5000); } catch { }
        }
        Thread.Sleep(300);
    }

    static void CreateShortcut(string link, string target)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(shellType);
        object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
        var t = sc.GetType();
        t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
        t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
        t.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "DeskNotes — стикер с делами (Ctrl+Alt+N)" });
        t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
        t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
    }

    static long DirSize(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { return 0; }
    }

    static void Uninstall()
    {
        if (MessageBox.Show("Удалить DeskNotes с компьютера?", "Удаление DeskNotes", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        bool wipeData = MessageBox.Show(
            "Удалить и твои дела, заметки и рисунки?\n\nНет — они останутся в " + DataDir + " и вернутся, если поставить DeskNotes снова.",
            "Удаление DeskNotes", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

        StopApp();
        try { if (File.Exists(StartMenuLink)) File.Delete(StartMenuLink); } catch { }
        try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null) k.DeleteValue("DeskNotes", false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        try { if (Directory.Exists(VoskDir)) Directory.Delete(VoskDir, true); } catch { }
        if (wipeData) { try { Directory.Delete(DataDir, true); } catch { } }

        // this exe lives in the folder being removed, so a tiny command finishes the job after it exits
        string cmd = "/c timeout /t 2 /nobreak >nul & rmdir /s /q \"" + InstallDir + "\"";
        Process.Start(new ProcessStartInfo("cmd.exe", cmd) { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true });
        MessageBox.Show("DeskNotes удалён." + (wipeData ? "" : "\nТвои записи сохранены в " + DataDir), "Удаление DeskNotes");
    }
}
