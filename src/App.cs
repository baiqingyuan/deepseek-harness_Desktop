using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
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
        private const int DefaultPort = 3080;
        // 首选 3080；被其它程序占用时会自动换一个空闲端口（见 StartServerIfNeededAsync）
        private int port = DefaultPort;
        private string portNotice;
        private readonly string baseDir;
        private readonly string nodePath;
        private readonly string dshPath;
        private readonly StringBuilder errorTail = new StringBuilder();
        private WebView2 webView;
        // dsh 每次启动都会生成一次性 token，并把带 token 的 URL 打到 stdout；
        // 桌面壳必须拿到它才能通过新版 Web 控制台的浏览器鉴权（否则所有 /api 调用 401）。
        private readonly TaskCompletionSource<string> readyUrlSource = new TaskCompletionSource<string>();
        private string diagnosticsHint;
        private Label statusLabel;
        private Process serverProc;
        private bool ownsServer;
        private bool shuttingDown;
        private bool navWarned;
        private NotifyIcon trayIcon;
        private bool trayExit;
        // 最近一次查到的可用更新（启动时静默检查发现后，点托盘气泡即可升级）
        private UpdateInfo pendingUpdate;
        private const string UpdateApiUrl =
            "https://api.github.com/repos/baiqingyuan/deepseek-harness_Desktop/releases/latest";
        private const string ReleasesPageUrl =
            "https://github.com/baiqingyuan/deepseek-harness_Desktop/releases";

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
                string startUrl = await StartServerIfNeededAsync();
                if (shuttingDown) return;
                await InitializeWebViewAsync(startUrl);
                if (!string.IsNullOrEmpty(portNotice)) ShowBalloon(portNotice, ToolTipIcon.Info);
                // 界面就绪后再静默查一次更新，有新版只在托盘提示，不打断使用
                await SilentUpdateCheckAsync();
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
        private async Task InitializeWebViewAsync(string startUrl)
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
                Exception ex = await TryCreateWebViewAsync(startUrl);
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
        private async Task<Exception> TryCreateWebViewAsync(string startUrl)
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
                // startUrl 形如 http://127.0.0.1:3080/?token=xxx：
                // 服务端校验 token 后写入会话 Cookie 并 303 跳转到干净的 /，之后一切正常。
                view.Source = new Uri(startUrl);

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

        // 启动本地 dsh Web 服务，返回本次可直接访问的起始 URL（带一次性 token）。
        private async Task<string> StartServerIfNeededAsync()
        {
            // 端口已被占用：先尝试停掉"本目录 dsh"的残留进程再重新拉起，
            // 因为新版 dsh 需要本次进程的一次性 token，接管旧进程拿不到。
            if (IsPortOpen(port))
            {
                bool stopped = await StopOwnedServerAsync();
                if (!stopped)
                {
                    // 3080 被别的程序占用时不再直接放弃启动：换一个空闲端口继续，
                    // 只在托盘提示一次，减少「打不开」这类求助。
                    port = FindFreePort();
                    portNotice = "端口 " + DefaultPort + " 已被其它程序占用，本次改用端口 " + port + "。";
                }
            }

            if (!File.Exists(nodePath))
                throw new Exception("程序目录缺少 node.exe，请下载完整的发布包（整个文件夹）后重试。\n详见 GitHub Releases 页面。");
            if (!File.Exists(dshPath))
                throw new Exception("程序目录缺少 dsh 依赖（node_modules/@deepseek-ai/dsh），请下载完整的发布包后重试。\n详见 GitHub Releases 页面。");

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = nodePath;
            // --no-open：不额外打开系统默认浏览器（界面由本窗口承载）
            // --port：显式固定端口，与 port 字段保持一致
            psi.Arguments = "\"" + dshPath + "\" web --no-open --port " + port;
            psi.WorkingDirectory = baseDir;
            psi.UseShellExecute = false;
            // 保留（隐藏的）控制台：退出时才能投递 Ctrl+C，让 dsh 走官方的优雅关闭路径
            psi.CreateNoWindow = false;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            serverProc = Process.Start(psi);
            if (serverProc == null) throw new Exception("无法启动 dsh 服务进程。");
            ownsServer = true;

            serverProc.OutputDataReceived += delegate (object s, DataReceivedEventArgs ev)
            {
                TryCaptureReadyUrl(ev.Data);
            };
            serverProc.ErrorDataReceived += delegate (object s, DataReceivedEventArgs ev)
            {
                if (ev.Data != null)
                {
                    // 官方启动诊断会把完整报告落盘并在这里给出路径，单独记下来方便提示用户。
                    if (ev.Data.IndexOf("Full diagnostics:", StringComparison.Ordinal) >= 0)
                        diagnosticsHint = ev.Data.Trim();
                    lock (errorTail)
                    {
                        if (errorTail.Length > 8000) errorTail.Remove(0, 4000);
                        errorTail.AppendLine(ev.Data);
                    }
                }
            };
            serverProc.BeginOutputReadLine();
            serverProc.BeginErrorReadLine();
            // dsh 崩溃或被拦截时给个提示，避免用户对着一个已经失效的界面发呆。
            serverProc.EnableRaisingEvents = true;
            serverProc.Exited += OnServerExited;

            return await WaitForReadyUrlAsync();
        }

        // 从 stdout 抓取形如 "dsh web: http://127.0.0.1:3080/?token=xxx (LAN: ...)" 的就绪行。
        private void TryCaptureReadyUrl(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            int i = line.IndexOf("dsh web:", StringComparison.Ordinal);
            if (i < 0) return;
            Match m = Regex.Match(line, @"https?://\S+");
            if (m.Success) readyUrlSource.TrySetResult(m.Value);
        }

        // 等待服务就绪：优先等带 token 的 URL 行；若端口已开但迟迟没有 URL 行
        // （旧版 dsh 不打印），兜底直接访问根路径。
        private async Task<string> WaitForReadyUrlAsync()
        {
            DateTime start = DateTime.UtcNow;
            DateTime portOpenAt = DateTime.MinValue;
            while (true)
            {
                if (serverProc != null && serverProc.HasExited && !readyUrlSource.Task.IsCompleted)
                    throw new Exception("dsh 服务进程已退出。" + ErrorTailText());
                if (readyUrlSource.Task.IsCompleted) return readyUrlSource.Task.Result;

                bool open = IsPortOpen(port);
                if (open && portOpenAt == DateTime.MinValue) portOpenAt = DateTime.UtcNow;
                if (open && portOpenAt != DateTime.MinValue &&
                    DateTime.UtcNow - portOpenAt > TimeSpan.FromSeconds(10))
                    return "http://127.0.0.1:" + port + "/";

                if (DateTime.UtcNow - start > TimeSpan.FromSeconds(90))
                    throw new Exception("等待 dsh 服务就绪超时（90 秒）。" + ErrorTailText());

                int waited = (int)((DateTime.UtcNow - start).TotalSeconds);
                if (statusLabel != null && !statusLabel.IsDisposed)
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (statusLabel != null && !statusLabel.IsDisposed)
                            statusLabel.Text = "正在启动 DeepSeek Harness 本地服务…（已等待 " + waited + " 秒）";
                    }));
                }
                await Task.Delay(500);
            }
        }

        // dsh 进程意外退出（崩溃 / 被杀软拦截）时提示一次。
        private void OnServerExited(object sender, EventArgs e)
        {
            // 启动阶段失败由 InitializeAsync 的错误弹窗负责，这里只管「已经用起来之后又挂了」
            if (shuttingDown || webView == null) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (shuttingDown || IsDisposed) return;
                    ShowBalloon("本地 dsh 服务已退出，界面将无法继续使用。\n请通过托盘图标「真正退出」后重新启动应用。",
                        ToolTipIcon.Warning);
                }));
            }
            catch { }
        }

        private void ShowBalloon(string text, ToolTipIcon icon)
        {
            if (trayIcon == null) return;
            try { trayIcon.ShowBalloonTip(5000, "DeepSeek Harness", text, icon); }
            catch { }
        }

        // 找到并停掉"本目录 dsh"残留的服务进程（例如上次异常退出遗留的 node.exe）。
        private async Task<bool> StopOwnedServerAsync()
        {
            int pid = FindOwnedServerPid();
            if (pid <= 0) return false;
            KillProcessTree(pid);
            for (int i = 0; i < 24; i++)
            {
                await Task.Delay(500);
                if (!IsPortOpen(port)) return true;
            }
            return !IsPortOpen(port);
        }

        // 取一个当前空闲的回环端口（端口 0 让系统分配，随即释放）。
        private static int FindFreePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }

        private int FindOwnedServerPid()
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
                            try { return Convert.ToInt32(o["ProcessId"]); }
                            catch { }
                        }
                    }
                }
            }
            catch { }
            return 0;
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
            string hint = diagnosticsHint;
            lock (errorTail)
            {
                string t = errorTail.ToString().Trim();
                string extra = "";
                if (!string.IsNullOrEmpty(hint) && t.IndexOf(hint, StringComparison.Ordinal) < 0)
                    extra = "\r\n\r\n启动诊断：" + hint;
                return t.Length == 0 ? extra : "\r\n\r\n服务日志（尾部）：\r\n" + t + extra;
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
                StopServer();
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
            // 点更新提示气泡直接进升级流程；其它气泡（如端口提示）则恢复窗口
            trayIcon.BalloonTipClicked += (s, ev) =>
            {
                if (pendingUpdate != null)
                {
                    UpdateInfo info = pendingUpdate;
                    Task ignored = PromptUpdateAsync(info);
                }
                else ShowForm();
            };
        }

        private ContextMenuStrip BuildTrayMenu()
        {
            var menu = new ContextMenuStrip();
            var open = new ToolStripMenuItem("显示主界面");
            open.Click += (s, ev) => ShowForm();
            var update = new ToolStripMenuItem("检查更新…");
            update.Click += async (s, ev) => await CheckForUpdatesAsync(true);
            var releases = new ToolStripMenuItem("打开下载页面");
            releases.Click += (s, ev) => OpenReleasesPage();
            var about = new ToolStripMenuItem("关于 / 版本 v" + AppInfo.Version);
            about.Click += (s, ev) => MessageBox.Show(
                "DeepSeek Harness 桌面版\r\n\r\n版本：v" + AppInfo.Version +
                "\r\n本地服务：http://127.0.0.1:" + port +
                "\r\n\r\n检查更新可获取 GitHub 上的最新版本。",
                "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
            var exit = new ToolStripMenuItem("真正退出");
            exit.Click += (s, ev) => RequestExit();
            menu.Items.Add(open);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(update);
            menu.Items.Add(releases);
            menu.Items.Add(about);
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

        // ---------- 优雅停止（对齐官方 SIGTERM 行为） ----------
        private delegate bool ConsoleCtrlHandler(uint ctrlType);

        private static readonly ConsoleCtrlHandler CtrlHandlerRef = OnConsoleCtrl;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler handler, bool add);

        private const uint CTRL_C_EVENT = 0;
        private const uint CTRL_BREAK_EVENT = 1;

        // 我们自己不能被同一次 Ctrl+Break 带走：只吞掉 C / Break，其余交给默认处理。
        private static bool OnConsoleCtrl(uint ctrlType)
        {
            return ctrlType == CTRL_C_EVENT || ctrlType == CTRL_BREAK_EVENT;
        }

        // Windows 没有 SIGTERM。dsh 只在 SIGINT / SIGTERM 上做优雅关闭，而 node 在 Windows
        // 上把 Ctrl+C 映射为 SIGINT，因此先发 Ctrl+C；仍未退出再补一记 Ctrl+Break。
        // 任何一步失败都返回 false，由调用方退回强制结束进程树，行为不会比原来更差。
        private static bool TryGracefulStop(Process proc)
        {
            try
            {
                if (proc.HasExited) return true;
                // 本进程已有控制台（例如从命令行启动）时无法附加，直接放弃优雅路径。
                if (!AttachConsole((uint)proc.Id)) return false;
                try
                {
                    SetConsoleCtrlHandler(CtrlHandlerRef, true);
                    if (!GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0)) return false;
                    if (proc.WaitForExit(6000)) return true; // 官方最多 5 秒 dispose 后自行退出
                    GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, 0);
                    return proc.WaitForExit(2000);
                }
                finally
                {
                    try { SetConsoleCtrlHandler(CtrlHandlerRef, false); } catch { }
                    FreeConsole();
                }
            }
            catch { return false; }
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

        // ---------- 更新检查与升级 ----------

        // 停掉本地 dsh 服务并释放进程句柄（关窗、安装更新前都会走这里）
        private void StopServer()
        {
            try
            {
                if (serverProc != null && !serverProc.HasExited)
                {
                    if (ownsServer)
                    {
                        // 先按官方约定优雅排空（插件树最多 5 秒 dispose），失败再强杀整棵进程树
                        bool graceful = TryGracefulStop(serverProc);
                        if (!graceful || !serverProc.HasExited) KillProcessTree(serverProc.Id);
                    }
                    else
                    {
                        try { serverProc.Kill(); } catch { }
                    }
                    if (!serverProc.HasExited) serverProc.WaitForExit(3000);
                }
            }
            catch { }
            try { if (serverProc != null) serverProc.Dispose(); } catch { }
            serverProc = null;
        }

        // 启动时静默检查一次：失败一律静默，有新版只在托盘提示
        private async Task SilentUpdateCheckAsync()
        {
            try
            {
                UpdateInfo info = await FetchLatestReleaseAsync();
                if (info == null) return;
                if (!IsNewerVersion(info.Version, AppInfo.Version)) return;
                pendingUpdate = info;
                ShowBalloon("发现新版本 v" + info.Version + "，点击此处升级（或右键托盘选「检查更新…」）。",
                    ToolTipIcon.Info);
            }
            catch { }
        }

        // 用户主动检查：无论结果都给出反馈
        private async Task CheckForUpdatesAsync(bool userInitiated)
        {
            try
            {
                UpdateInfo info = await FetchLatestReleaseAsync();
                if (info == null)
                {
                    if (userInitiated)
                        MessageBox.Show("暂时无法获取更新信息，请稍后重试，或到 GitHub Releases 页面手动下载。",
                            "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (!IsNewerVersion(info.Version, AppInfo.Version))
                {
                    if (userInitiated)
                        MessageBox.Show("当前已是最新版本（v" + AppInfo.Version + "）。",
                            "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                pendingUpdate = info;
                await PromptUpdateAsync(info);
            }
            catch (Exception ex)
            {
                if (userInitiated)
                    MessageBox.Show("检查更新失败：" + ex.Message, "检查更新",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // 展示更新说明并询问是否下载升级
        private async Task PromptUpdateAsync(UpdateInfo info)
        {
            string notes = info.Notes;
            if (notes.Length > 600) notes = notes.Substring(0, 600) + "…";
            DialogResult r = MessageBox.Show(
                "发现新版本 v" + info.Version + "（当前 v" + AppInfo.Version + "）\r\n\r\n" +
                (string.IsNullOrEmpty(notes) ? "" : notes + "\r\n\r\n") +
                "是否现在下载安装包并升级？\r\n（约 50 MB，下载后会关闭本应用并启动安装程序）",
                "DeepSeek Harness 更新", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r != DialogResult.Yes) return;
            await DownloadAndInstallAsync(info);
        }

        // 下载安装包（带进度），完成后询问是否立即安装
        private async Task DownloadAndInstallAsync(UpdateInfo info)
        {
            if (string.IsNullOrEmpty(info.InstallerUrl))
            {
                // 该版本没有提供安装包，交给用户自己在下载页选择便携版
                OpenReleasesPage();
                return;
            }

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeepSeekHarness", "Updates");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "DeepSeekHarness-Setup-" + info.Version + ".exe");

            ProgressForm progress = new ProgressForm("正在下载更新 v" + info.Version);
            progress.Show(this);
            try
            {
                using (WebClient wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "DeepSeekHarness-Desktop/" + AppInfo.Version);
                    wc.DownloadProgressChanged += delegate (object s, DownloadProgressChangedEventArgs ev)
                    {
                        progress.UpdateProgress(ev.ProgressPercentage,
                            FormatSize(ev.BytesReceived), FormatSize(ev.TotalBytesToReceive));
                    };
                    TaskCompletionSource<object> done = new TaskCompletionSource<object>();
                    wc.DownloadFileCompleted += delegate (object s, System.ComponentModel.AsyncCompletedEventArgs ev)
                    {
                        if (ev.Error != null) done.TrySetException(ev.Error);
                        else done.TrySetResult(null);
                    };
                    wc.DownloadFileAsync(new Uri(info.InstallerUrl), file);
                    await done.Task;
                }
            }
            finally
            {
                progress.Close();
                progress.Dispose();
            }

            DialogResult r = MessageBox.Show(
                "更新 v" + info.Version + " 已下载完成。\r\n\r\n是否立即关闭应用并安装？\r\n" +
                "（安装程序会覆盖更新到原安装目录，配置与会话不会丢失）",
                "DeepSeek Harness 更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes)
            {
                MessageBox.Show("安装包已保存，可稍后手动运行：\r\n" + file,
                    "DeepSeek Harness 更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 先停服务再启动安装程序，避免 exe / node 被占用导致覆盖失败
            shuttingDown = true;
            StopServer();
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            Application.Exit();
        }

        private static void OpenReleasesPage()
        {
            try { Process.Start(new ProcessStartInfo(ReleasesPageUrl) { UseShellExecute = true }); }
            catch { }
        }

        // 取 GitHub Releases 最新版信息；用正则抽取字段，避免为此引入 JSON 依赖
        private static async Task<UpdateInfo> FetchLatestReleaseAsync()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            string json;
            using (WebClient wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8;
                wc.Headers.Add("User-Agent", "DeepSeekHarness-Desktop/" + AppInfo.Version);
                wc.Headers.Add("Accept", "application/vnd.github+json");
                json = await wc.DownloadStringTaskAsync(UpdateApiUrl);
            }
            if (string.IsNullOrEmpty(json)) return null;

            Match tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            if (!tag.Success) return null;

            UpdateInfo info = new UpdateInfo();
            info.Version = tag.Groups[1].Value.TrimStart('v', 'V');

            Match setup = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]*Setup-[^\"]*\\.exe)\"");
            info.InstallerUrl = setup.Success ? setup.Groups[1].Value : null;

            Match zip = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]*Desktop-[^\"]*win-x64\\.zip)\"");
            info.ZipUrl = zip.Success ? zip.Groups[1].Value : null;

            Match page = Regex.Match(json, "\"html_url\"\\s*:\\s*\"(https://github.com/[^\"]*releases/tag/[^\"]*)\"");
            info.PageUrl = page.Success ? page.Groups[1].Value : ReleasesPageUrl;

            Match body = Regex.Match(json, "\"body\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (body.Success)
            {
                try { info.Notes = Regex.Unescape(body.Groups[1].Value); }
                catch { info.Notes = body.Groups[1].Value; }
            }
            else info.Notes = "";
            return info;
        }

        // 只比较数字部分，v0.5.0 / 0.5.0-beta 都能正确处理
        private static bool IsNewerVersion(string latest, string current)
        {
            try
            {
                Version a, b;
                if (!Version.TryParse(NumericPart(latest), out a)) return false;
                if (!Version.TryParse(NumericPart(current), out b)) return false;
                return a > b;
            }
            catch { return false; }
        }

        private static string NumericPart(string v)
        {
            Match m = Regex.Match(v == null ? "" : v, "\\d+(?:\\.\\d+)*");
            return m.Success ? m.Value : "0.0.0";
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            return (bytes / 1048576.0).ToString("F1") + " MB";
        }

        private sealed class UpdateInfo
        {
            public string Version = "";
            public string Notes = "";
            public string InstallerUrl;
            public string ZipUrl;
            public string PageUrl = ReleasesPageUrl;
        }
    }

    // 下载更新时的简易进度窗口
    internal sealed class ProgressForm : Form
    {
        private readonly ProgressBar bar;
        private readonly Label label;
        private readonly Action<int, string, string> updater;

        public ProgressForm(string title)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 110);
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;

            bar = new ProgressBar { Left = 20, Top = 22, Width = 380, Height = 24 };
            label = new Label { Left = 20, Top = 58, Width = 380, Height = 22, Text = "准备下载…" };
            Controls.Add(bar);
            Controls.Add(label);

            updater = SetProgress;
        }

        public void UpdateProgress(int percent, string received, string total)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(updater, percent, received, total); } catch { }
                return;
            }
            SetProgress(percent, received, total);
        }

        private void SetProgress(int percent, string received, string total)
        {
            bar.Value = Math.Min(100, Math.Max(0, percent));
            label.Text = "已下载 " + received + " / " + total;
        }
    }
}
