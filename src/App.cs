using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DeepSeekHarness
{
    internal static class Program
    {
        private static Mutex singleInstance;
        private const string MutexName = "DeepSeekHarness.Desktop.SingleInstance";

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 单实例：避免多个 exe 同时拉起/接管同一服务，导致互相误杀。
            // 用 initiallyOwned=false + WaitOne(0) 的方式，避免上一个实例崩溃留下的
            // abandoned mutex 让构造抛出 AbandonedMutexException。
            singleInstance = new Mutex(false, MutexName);
            bool owned;
            try { owned = singleInstance.WaitOne(0, false); }
            catch (AbandonedMutexException) { owned = true; }

            if (!owned)
            {
                BringExistingToFront();
                return;
            }

            try
            {
                Application.Run(new MainForm());
            }
            finally
            {
                try { singleInstance.ReleaseMutex(); } catch { }
                singleInstance.Dispose();
            }
        }

        private static void BringExistingToFront()
        {
            try
            {
                IntPtr h = FindWindow(null, "DeepSeek Harness");
                if (h != IntPtr.Zero)
                {
                    ShowWindow(h, 9); // SW_RESTORE
                    SetForegroundWindow(h);
                }
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }

    internal sealed class MainForm : Form
    {
        private const int Port = 3080;
        private readonly string baseDir;
        private readonly string nodePath;
        private readonly string dshPath;
        private readonly StringBuilder errorTail = new StringBuilder();
        private WebView2 webView;
        private Label statusLabel;
        private Process serverProc;
        private bool ownsServer;
        private bool shuttingDown;
        private bool navWarned;
        private NotifyIcon trayIcon;
        private bool trayExit;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        public MainForm()
        {
            baseDir = AppDomain.CurrentDomain.BaseDirectory;
            nodePath = Path.Combine(baseDir, "node.exe");
            dshPath = Path.Combine(baseDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");

            Text = "DeepSeek Harness";
            ClientSize = new Size(1280, 820);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // 启动期占位提示，避免白屏 + 让 UI 在等待服务时仍响应。
            statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "正在启动 DeepSeek Harness 本地服务…",
                Font = new Font("Microsoft YaHei", 12),
                ForeColor = Color.FromArgb(90, 90, 90)
            };
            Controls.Add(statusLabel);

            InitializeTray();

            Shown += async delegate { await InitializeAsync(); };
        }

        private async Task InitializeAsync()
        {
            try
            {
                await StartServerIfNeededAsync();
                if (shuttingDown) return;
                await InitializeWebViewAsync();
            }
            catch (Exception ex)
            {
                string detail = ex.ToString() + "\r\n\r\n" + ErrorTailText();
                try { File.WriteAllText(Path.Combine(baseDir, "dsh-app-error.log"), detail); } catch { }
                if (!shuttingDown && !IsDisposed)
                {
                    MessageBox.Show("DeepSeek Harness 启动失败：\r\n\r\n" + ex.Message + ErrorTailText(),
                        "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                BeginInvoke(new Action(RequestExit));
            }
        }

        // WebView2 初始化（带重试：偶发 E_ABORT，多为上一次实例未完全退出导致，稍候重试即可）
        private async Task InitializeWebViewAsync()
        {
            // 运行前先确认 WebView2 运行时已安装；缺失则引导用户一键安装后再继续。
            if (!IsWebView2RuntimeAvailable())
            {
                if (!await EnsureWebView2RuntimeAsync())
                {
                    if (!shuttingDown && !IsDisposed)
                        MessageBox.Show(
                            "未检测到 WebView2 运行时，应用无法启动。\n请先从 https://aka.ms/webview2 安装后重试。",
                            "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    BeginInvoke(new Action(RequestExit));
                    return;
                }
            }

            Exception last = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                Exception ex = await TryCreateWebViewAsync();
                if (ex == null) return;
                last = ex;
                if (attempt < 3) await Task.Delay(2000 * attempt);
            }
            throw last;
        }

        // 探测系统是否安装了 WebView2 运行时（Evergreen）。
        // 已安装返回 true；缺失或任何异常均视为不可用，交由引导安装流程处理。
        private static bool IsWebView2RuntimeAvailable()
        {
            try
            {
                string v = CoreWebView2Environment.GetAvailableBrowserVersionString(null);
                return !string.IsNullOrEmpty(v);
            }
            catch { return false; }
        }

        // 引导用户下载并静默安装 WebView2 Evergreen 运行时。成功返回 true。
        private async Task<bool> EnsureWebView2RuntimeAsync()
        {
            DialogResult r = MessageBox.Show(
                "未检测到 Microsoft WebView2 运行时，本应用需要它才能运行。\n\n是否立即下载并安装？（约 1-2 分钟，需联网；可能需要管理员权限）",
                "DeepSeek Harness", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return false;

            string setup = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebView2Setup.exe");
            try
            {
                if (!File.Exists(setup))
                {
                    using (var wc = new WebClient())
                    {
                        await Task.Run(() => wc.DownloadFile(
                            "https://go.microsoft.com/fwlink/p/?LinkId=2086042", setup));
                    }
                }

                ProcessStartInfo psi = new ProcessStartInfo(setup, "/silent /install")
                {
                    UseShellExecute = true,
                    Verb = "runas"
                };
                using (Process p = Process.Start(psi))
                {
                    if (p != null) p.WaitForExit();
                }

                return IsWebView2RuntimeAvailable();
            }
            catch (Exception ex)
            {
                MessageBox.Show("WebView2 运行时安装失败：" + ex.Message +
                    "\n\n请手动从 https://developer.microsoft.com/microsoft-edge/webview2/ 安装后重试。",
                    "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        // 成功返回 null；失败返回异常并清理控件
        private async Task<Exception> TryCreateWebViewAsync()
        {
            WebView2 view = null;
            try
            {
                view = new WebView2 { Dock = DockStyle.Fill };
                Controls.Add(view);

                string userData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekHarness", "EBWebView");
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, userData, null);
                await view.EnsureCoreWebView2Async(env);

                view.CoreWebView2.Settings.AreDevToolsEnabled = false;
                view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                view.CoreWebView2.Settings.IsStatusBarEnabled = false;
                view.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                view.Source = new Uri("http://127.0.0.1:" + Port);

                // WebView 就绪，移除占位提示
                if (statusLabel != null) { Controls.Remove(statusLabel); statusLabel.Dispose(); statusLabel = null; }
                webView = view;
                return null;
            }
            catch (Exception ex)
            {
                try { if (view != null) { Controls.Remove(view); view.Dispose(); } } catch { }
                webView = null;
                return ex;
            }
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess && !shuttingDown && !navWarned)
            {
                navWarned = true;
                BeginInvoke(new Action(() =>
                    MessageBox.Show("无法加载 DeepSeek Harness 界面，请检查本地服务是否正常运行。",
                        "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Warning)));
            }
        }

        // 异步等待服务就绪，避免阻塞 UI 线程（旧实现用 Thread.Sleep 导致白屏卡死）。
        private async Task StartServerIfNeededAsync()
        {
            if (IsPortOpen(Port))
            {
                AdoptExistingServer();
                // 端口开了但没找到本应用自己的 dsh 进程：说明被别的程序占用，
                // 不应静默接管/显示他人内容，直接提示用户。
                if (serverProc == null)
                    throw new Exception("端口 " + Port + " 已被其他程序占用，无法启动本地服务。\n请关闭占用该端口的程序后重试。");
                return;
            }

            if (!File.Exists(nodePath))
                throw new Exception("程序目录缺少 node.exe，请下载完整的发布包（整个文件夹）后重试。\n详见 GitHub Releases 页面。");
            if (!File.Exists(dshPath))
                throw new Exception("程序目录缺少 dsh 依赖（node_modules/@deepseek-ai/dsh），请下载完整的发布包后重试。\n详见 GitHub Releases 页面。");

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = nodePath;
            psi.Arguments = "\"" + dshPath + "\" web";
            psi.WorkingDirectory = baseDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            serverProc = Process.Start(psi);
            if (serverProc == null) throw new Exception("无法启动 dsh 服务进程。");
            ownsServer = true;

            serverProc.OutputDataReceived += delegate { };
            serverProc.ErrorDataReceived += delegate (object s, DataReceivedEventArgs ev)
            {
                if (ev.Data != null)
                {
                    lock (errorTail)
                    {
                        if (errorTail.Length > 8000) errorTail.Remove(0, 4000);
                        errorTail.AppendLine(ev.Data);
                    }
                }
            };
            serverProc.BeginOutputReadLine();
            serverProc.BeginErrorReadLine();

            DateTime deadline = DateTime.UtcNow.AddSeconds(90);
            int waited = 0;
            while (DateTime.UtcNow < deadline)
            {
                if (serverProc.HasExited)
                    throw new Exception("dsh 服务进程已退出。" + ErrorTailText());
                if (IsPortOpen(Port)) return;
                await Task.Delay(1000);
                waited++;
                if (statusLabel != null && !statusLabel.IsDisposed)
                {
                    BeginInvoke(new Action(() =>
                        statusLabel.Text = "正在启动 DeepSeek Harness 本地服务…（已等待 " + waited + " 秒）"));
                }
            }
            throw new Exception("等待 dsh 服务就绪超时（90 秒）。" + ErrorTailText());
        }

        // 若端口已被本目录安装的 dsh 服务占用（例如上次异常残留），接管该进程，
        // 使关闭窗口时也能一并停止，避免遗留后台服务。
        // 由于已做单实例互斥，这里接管到的只会是"上一次崩溃残留"的进程，可安全清理。
        private void AdoptExistingServer()
        {
            try
            {
                string marker = dshPath.Replace('/', '\\').ToLowerInvariant();
                using (System.Management.ManagementObjectSearcher searcher =
                    new System.Management.ManagementObjectSearcher(
                        "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'node.exe'"))
                {
                    foreach (System.Management.ManagementObject o in searcher.Get())
                    {
                        string cmd = Convert.ToString(o["CommandLine"]);
                        if (cmd == null) continue;
                        if (cmd.ToLowerInvariant().IndexOf(marker, StringComparison.Ordinal) >= 0)
                        {
                            int pid = Convert.ToInt32(o["ProcessId"]);
                            try { serverProc = Process.GetProcessById(pid); ownsServer = true; break; }
                            catch { }
                        }
                    }
                }
            }
            catch { }
        }

        private static bool IsPortOpen(int port)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect("127.0.0.1", port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(1500)) return false;
                    client.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        private string ErrorTailText()
        {
            lock (errorTail)
            {
                string t = errorTail.ToString().Trim();
                return t.Length == 0 ? "" : "\r\n\r\n服务日志（尾部）：\r\n" + t;
            }
        }

        // 托盘常驻：点 X（用户关闭）不直接退出，最小化到托盘并继续运行 dsh 服务，
        // 避免误关后重开又要等启动。只有「真正退出」菜单或程序自身异常才走彻底关闭。
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!trayExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                ShowTrayHint();
                return;
            }

            if (!shuttingDown)
            {
                shuttingDown = true;
                try
                {
                    if (serverProc != null && !serverProc.HasExited)
                    {
                        if (ownsServer) KillProcessTree(serverProc.Id); // 连带子进程一起清理
                        else serverProc.Kill();                         // 端口上是别人：仅尽力终止
                        serverProc.WaitForExit(3000);
                    }
                }
                catch { }
                try { serverProc.Dispose(); } catch { }
                serverProc = null;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); trayIcon = null; }
            base.OnFormClosed(e);
        }

        // 初始化系统托盘图标与右键菜单（显示主界面 / 真正退出）。
        private void InitializeTray()
        {
            trayIcon = new NotifyIcon
            {
                Icon = this.Icon ?? SystemIcons.Application,
                Text = "DeepSeek Harness",
                Visible = true,
                ContextMenuStrip = BuildTrayMenu()
            };
            // 单击托盘图标即恢复主窗口（用户要求比双击更顺手）；双击同样生效无副作用。
            trayIcon.Click += (s, ev) => ShowForm();
        }

        private ContextMenuStrip BuildTrayMenu()
        {
            var menu = new ContextMenuStrip();
            var open = new ToolStripMenuItem("显示主界面");
            open.Click += (s, ev) => ShowForm();
            var exit = new ToolStripMenuItem("真正退出");
            exit.Click += (s, ev) => RequestExit();
            menu.Items.Add(open);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exit);
            return menu;
        }

        // 从托盘恢复主窗口并置于前台。
        private void ShowForm()
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            Activate();
            try { SetForegroundWindow(this.Handle); } catch { }
        }

        // 真正退出：隐藏托盘图标并彻底关闭（OnFormClosing 会据此清理 dsh 服务）。
        private void RequestExit()
        {
            trayExit = true;
            if (trayIcon != null) trayIcon.Visible = false;
            Close();
        }

        private void ShowTrayHint()
        {
            if (trayIcon == null) return;
            try
            {
                trayIcon.ShowBalloonTip(3000, "DeepSeek Harness",
                    "已最小化到系统托盘，本地服务仍在运行。单击托盘图标恢复窗口，右键可「真正退出」。",
                    ToolTipIcon.Info);
            }
            catch { }
        }

        // 递归杀掉整棵进程树（WMI 查子进程），确保关窗即停、无残留。
        private void KillProcessTree(int pid)
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = " + pid))
                {
                    foreach (System.Management.ManagementObject o in searcher.Get())
                    {
                        try { KillProcessTree(Convert.ToInt32(o["ProcessId"])); } catch { }
                    }
                }
            }
            catch { }
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    if (!p.HasExited) p.Kill();
                }
            }
            catch { }
        }
    }
}
