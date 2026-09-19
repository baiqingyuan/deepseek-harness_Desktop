using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
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
            // 兜底：任何漏网的异常都要留下痕迹并提示，绝不能像 v0.7.0 那样
            // 双击之后毫无反应地静默退出（用户完全不知道发生了什么）。
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
            {
                Log.Error("Application.ThreadException", e.Exception);
                try
                {
                    MessageBox.Show(
                        "DeepSeek Harness 遇到问题：\r\n\r\n" +
                        (e.Exception == null ? "(未知)" : e.Exception.Message) +
                        "\r\n\r\n详细日志：\r\n" + Log.DirectoryPath,
                        "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                Log.Error("AppDomain.UnhandledException",
                    ex ?? new Exception(Convert.ToString(e.ExceptionObject)));
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // 让 WinForms 按每显示器 DPI 缩放（配合 src/app.manifest 的 PerMonitorV2 声明）。
            // 用反射调用，避免在只装了 .NET Framework 4.6 及更旧的机器上直接 MissingMethodException。
            TryEnablePerMonitorV2();

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

            MainForm form;
            try
            {
                form = new MainForm();
            }
            catch (Exception ex)
            {
                Log.Error("MainForm 构造", ex);
                try
                {
                    MessageBox.Show(
                        "界面初始化失败：\r\n\r\n" + ex.Message +
                        "\r\n\r\n详细日志：\r\n" + Log.DirectoryPath,
                        "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
                return;
            }
            try
            {
                Application.Run(form);
            }
            finally
            {
                try { singleInstance.ReleaseMutex(); } catch { }
                singleInstance.Dispose();
            }
        }

        // Application.SetHighDpiMode(HighDpiMode.PerMonitorV2) 仅在 .NET Framework 4.7+ 存在，
        // 因此用反射探测调用；失败不影响运行（exe 内嵌 manifest 已声明进程级 DPI 感知）。
        private static void TryEnablePerMonitorV2()
        {
            try
            {
                Type modeType = Type.GetType("System.Windows.Forms.HighDpiMode, System.Windows.Forms");
                if (modeType == null) return;
                object mode = Enum.Parse(modeType, "PerMonitorV2");
                System.Reflection.MethodInfo mi = typeof(Application).GetMethod(
                    "SetHighDpiMode",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public,
                    null, new Type[] { modeType }, null);
                if (mi != null) mi.Invoke(null, new object[] { mode });
            }
            catch { }
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

    // 扁平化 Windows 风格配色与统一的字体。
    // v0.8.0 起去掉了 v0.7.x 的 iOS 化外观（交通灯、大圆角、居中标题），
    // 改为 Windows 11 观感：白底标题栏 + 1px 分隔线 + 右侧标准三个标题按钮。
    internal static class UI
    {
        // 注意：这些配色是**可变**的 —— 网页会把当前主题背景色上报给桌面壳，
        // 标题栏 / 描边 / 文字会跟着切换（见 ApplyChromeTheme）。
        // 默认取深色：启动加载期（网页还没上报主题时）就与 dsh 深色主界面一致，
        // 不再出现"白底加载页 → 深色主界面"的突兀跳变；浅色页面加载后由上报切回浅色。
        public static Color WindowBg = Color.FromArgb(26, 27, 31);          // 窗口底色
        public static Color Bar = Color.FromArgb(26, 27, 31);               // 标题栏
        public static Color Border = Color.FromArgb(14, 15, 17);            // 窗口 1px 描边（柔和）
        public static Color Separator = Color.FromArgb(54, 56, 61);         // 标题栏底部分隔线
        public static Color Hover = Color.FromArgb(56, 58, 63);             // 标题按钮悬停
        public static Color Pressed = Color.FromArgb(72, 74, 80);           // 标题按钮按下
        public static Color CloseHover = Color.FromArgb(232, 17, 35);       // 关闭按钮悬停
        public static Color Label = Color.FromArgb(235, 235, 238);          // 主文字
        public static Color LabelSecondary = Color.FromArgb(158, 162, 170); // 次级文字
        public static Color Card = Color.FromArgb(41, 43, 48);
        public static readonly Color Accent = Color.FromArgb(77, 107, 254); // DeepSeek 蓝
        public const string FontName = "Segoe UI";                          // Win11 为 Segoe UI Variable
        public const int TitleBarHeight = 40;                               // 标题栏高度
        public const int CaptionButtonWidth = 46;                           // 单个标题按钮宽度

        // 主题切换时的默认浅色值（恢复用）
        public static void ResetLightTheme()
        {
            WindowBg = Color.FromArgb(245, 246, 248);
            Bar = Color.White;
            Border = Color.FromArgb(222, 224, 229);
            Separator = Color.FromArgb(234, 236, 240);
            Hover = Color.FromArgb(240, 241, 245);
            Pressed = Color.FromArgb(226, 228, 234);
            Label = Color.FromArgb(31, 35, 40);
            LabelSecondary = Color.FromArgb(108, 114, 124);
            Card = Color.White;
        }

        // 精简/服务器版系统上可能没有 Segoe UI，new Font 会抛异常。
        // 统一走这里，逐级回退，保证界面构造不会因为字体而失败。
        public static Font MakeFont(float size, FontStyle style)
        {
            try { return new Font(FontName, size, style); }
            catch { }
            try { return new Font("Microsoft YaHei UI", size, style); }
            catch { }
            try { return new Font(SystemFonts.DefaultFont.FontFamily, size, style); }
            catch { }
            return SystemFonts.DefaultFont;
        }

        public static Font MakeFont(float size) { return MakeFont(size, FontStyle.Regular); }
    }

    // 崩溃日志：写进 %LOCALAPPDATA%\DeepSeekHarness\logs，便于排查"双击没反应"这类问题
    internal static class Log
    {
        public static string DirectoryPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekHarness", "logs");
            }
        }

        public static void Error(string where, Exception ex)
        {
            try
            {
                string dir = DirectoryPath;
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir,
                    "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                File.WriteAllText(file,
                    "DeepSeek Harness " + AppInfo.Version + "\r\n" +
                    "时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" +
                    "位置: " + where + "\r\n\r\n" + (ex == null ? "(无异常对象)" : ex.ToString()));
            }
            catch { }
        }
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
        private Panel titleBar;
        private Label titleLabel;
        private Label portBadge;
        private Panel loadingCard;
        private Label loadingText;
        private ResizeGrip[] grips;
        private CaptionButton btnMin, btnMax, btnClose;
        private Process serverProc;
        private bool ownsServer;
        private bool shuttingDown;
        private bool navWarned;
        private NotifyIcon trayIcon;
        private bool trayExit;
        // 最近一次查到的可用更新（启动时静默检查发现后，点托盘气泡或界面按钮即可升级）
        private UpdateInfo pendingUpdate;
        // 更新流程状态：会同步给网页里的左下角更新按钮（idle/available/downloading/ready…）
        private string updatePhase = "idle";
        private int updatePercent;
        private bool updateBusy;
        private string downloadedInstaller;
        private string downloadedVersion;
        // 首选静态清单 latest.json（由 CI 随 Release 上传）。它走的是 release 资源下载域名，
        // 不受 GitHub API 匿名限流（60 次/小时/IP）影响——同一出口 IP 下多人使用也能拿到更新。
        private const string UpdateManifestUrl =
            "https://github.com/baiqingyuan/deepseek-harness_Desktop/releases/latest/download/latest.json";
        // 清单不可用（老版本 Release、网络拦截等）时回退到 API
        private const string UpdateApiUrl =
            "https://api.github.com/repos/baiqingyuan/deepseek-harness_Desktop/releases/latest";
        private const string ReleasesPageUrl =
            "https://github.com/baiqingyuan/deepseek-harness_Desktop/releases";
        // GitHub 的两个域名在网络不通时常常既连不上也不报错（请求一直挂着），
        // 界面就会永远停在「检查中…」。这里给每次请求都套上硬超时：
        // 清单 / API 各 12 秒（串行最坏 ~24 秒），再在外面整体兜一层（见 RunUpdateFlowAsync）。
        private const int CheckTimeoutMs = 12000;
        // 下载阶段：60 秒一点进度都没有就判定卡死并取消，避免「下载中 12%」挂一整天
        private const int DownloadStallMs = 60000;

        // 注入到 dsh 网页里的「桌面更新」脚本：
        // 1) 挂一个更新按钮，视觉上位于**侧边栏内部**（设置按钮上方一行），
        //    侧边栏收起 / 隐藏时按钮同步消失；
        // 2) 同时暴露官方 dsh 0.1.6+ 约定的 window.dshDesktop 更新桥接，
        //    将来升级 dsh 后由官方界面自己渲染按钮，本脚本会自动隐藏、不重复。
        // 全部使用单引号，便于直接放进 C# 的逐字字符串。
        private const string UpdateBridgeScript = @"
(function(){
 if (window.__dshUpdateReady) return;
 if (window.top !== window) return;
 window.__dshUpdateReady = true;
 var BTN='dsh-desktop-update-btn';
 var listeners=[];
 var state={phase:'idle'};
 var pending=false;
 var lastRun=0;
 var lastTheme='';
 function label(s){
  if(s.phase==='checking') return '检查更新…';
  if(s.phase==='available') return '新版本' + (s.version ? ' v'+s.version : '');
  if(s.phase==='downloading') return '下载中 ' + (s.percent||0) + '%';
  if(s.phase==='ready') return '安装并重启';
  if(s.phase==='installing') return '正在安装…';
  if(s.phase==='uptodate') return '已是最新版';
  if(s.phase==='error') return '重试更新';
  return '检查更新';
 }
 function busy(s){ return s.phase==='checking'||s.phase==='downloading'||s.phase==='installing'; }
 function render(){
  for (var i=0;i<listeners.length;i++){ try{listeners[i](state);}catch(e){} }
  var b=document.getElementById(BTN);
  if(!b) return;
  var t=label(state);
  b.textContent=t;
  b.setAttribute('aria-label',t);
  if(busy(state)) b.setAttribute('data-busy','1'); else b.removeAttribute('data-busy');
  if(state.phase==='error') b.setAttribute('data-error','1'); else b.removeAttribute('data-error');
  if(state.phase==='idle') b.setAttribute('data-idle','1'); else b.removeAttribute('data-idle');
 }
 function send(){ try{ window.chrome.webview.postMessage('dsh-update-open'); }catch(e){} }
 function style(){
  if(document.getElementById('dsh-desktop-update-css')) return;
  var st=document.createElement('style');
  st.id='dsh-desktop-update-css';
  st.textContent='#'+BTN+'{position:fixed;z-index:2147483000;display:inline-flex;align-items:center;height:28px;padding:0 10px;margin:0;border:1px solid rgba(128,128,128,0.22);border-radius:8px;background:rgba(128,128,128,0.12);color:inherit;font-family:inherit;font-size:12px;line-height:1;white-space:nowrap;cursor:pointer;backdrop-filter:blur(6px);}'
   +'#'+BTN+':hover{background:rgba(128,128,128,0.22);}'
   +'#'+BTN+'[data-idle]{opacity:.5;}'
   +'#'+BTN+'[data-busy]{cursor:progress;opacity:.8;}'
   +'#'+BTN+'[data-error]{border-color:#E81123;color:#E81123;opacity:1;}';
  (document.head||document.documentElement).appendChild(st);
 }
 function build(){
  var b=document.getElementById(BTN);
  if(b) return b;
  b=document.createElement('button');
  b.id=BTN; b.type='button'; b.textContent='检查更新';
  b.addEventListener('click', function(){ if(b.getAttribute('data-busy')) return; send(); });
  document.body.appendChild(b);
  return b;
 }
 function sidebar(){
  var cands=document.querySelectorAll('aside,nav,[class*=sidebar],[class*=Sidebar]');
  var best=null,bestH=0;
  for(var i=0;i<cands.length;i++){
   var r=cands[i].getBoundingClientRect();
   if(r.left<60 && r.width>120 && r.width<560 && r.height>300 && r.height>bestH){best=cands[i];bestH=r.height;}
  }
  return best;
 }
 function settingsBtn(){
  var cands=document.querySelectorAll('button,a,[role=button]');
  var best=null,bestTop=-1;
  for(var i=0;i<cands.length;i++){
   var el=cands[i];
   if(el.id===BTN) continue;
   var r=el.getBoundingClientRect();
   if(r.width<8||r.height<8) continue;
   if(r.left>320 || r.top < window.innerHeight*0.5) continue;
   var txt=(el.getAttribute('aria-label')||'')+' '+(el.getAttribute('title')||'')+' '+(el.textContent||'');
   if(!/设置|Settings|偏好|Preferences|账户|Account/.test(txt)) continue;
   if(r.top>bestTop){best=el;bestTop=r.top;}
  }
  return best;
 }
 function hasNative(){
  var els=document.querySelectorAll('[aria-label]');
  for(var i=0;i<els.length;i++){
   if(els[i].id===BTN) continue;
   var a=els[i].getAttribute('aria-label');
   if(a && /^(新版本|Update|安装并重启|Install and Restart|重试更新|Retry update|下载中)/.test(a)) return true;
  }
  return false;
 }
 // 按钮常驻 document.body（React 不管理这里），视觉上定位到**侧边栏内部**：
 // 侧边栏内、设置按钮上方一行。侧边栏收起（找不到符合条件的侧边栏）时按钮隐藏，
// 与侧边栏同步出现 / 消失。
 // 之前把按钮塞进 React 管理的行容器：React 重渲染会删掉它 → 我们再插回去 →
// 互相打架，配合 MutationObserver 形成插入/删除风暴，页面会一直卡在 Loading 界面。
 function place(){
  var b=document.getElementById(BTN);
  if(!b) return;
  var sb=sidebar();
  if(!sb){ b.style.display='none'; return; }
  b.style.position='fixed';
  var sr=sb.getBoundingClientRect();
  b.style.bottom='auto';
  b.style.left=Math.round(sr.left+12)+'px';
  var s=settingsBtn();
  if(s){
   var r=s.getBoundingClientRect();
   // 设置按钮在侧边栏内：放到它上方一行，视觉上属于侧边栏
   if(r.top>sr.top && r.left<sr.right){
    b.style.top=Math.round(r.top-b.offsetHeight-8)+'px';
    return;
   }
  }
  b.style.top=Math.round(sr.bottom-b.offsetHeight-14)+'px';
 }
 // 把页面背景色上报给桌面壳：标题栏/描边/文字跟着主题切换（深色界面配深色外框）
 function theme(){
  try{
   var c=getComputedStyle(document.body).backgroundColor;
   if(c && c!==lastTheme){
    lastTheme=c;
    try{ window.chrome.webview.postMessage('dsh-theme:'+c); }catch(e){}
   }
  }catch(e){}
 }
 function tick(){
  lastRun=Date.now(); pending=false;
  // 一体化标题栏的窗口按钮跟着网页布局重定位（侧边栏/标签页变化、窗口缩放等）
  try{ if(window.__dshCaptionPlace) window.__dshCaptionPlace(); }catch(e){}
  var b=document.getElementById(BTN);
  if(!b){ style(); b=build(); }
  // 侧边栏不在（收起/隐藏）或官方原生更新入口存在：按钮一律隐藏
  if(!sidebar() || hasNative()){ b.style.display='none'; render(); theme(); return; }
  b.style.display='inline-flex';
  place(); render(); theme();
 }
 // MutationObserver 回调只登记，真实工作经节流合并 —— 启动期 React 高频改 DOM，
 // 不节流的话每帧都在全树 querySelector + 强制布局，页面会卡在 Loading 转圈界面。
 function requestTick(){
  if(pending) return;
  pending=true;
  var wait=600-(Date.now()-lastRun);
  if(wait<0) wait=0;
  setTimeout(tick, wait);
 }
 function boot(){ style(); tick(); setInterval(tick,2000);
  try{ new MutationObserver(requestTick).observe(document.documentElement,{childList:true,subtree:true}); }catch(e){}
 }
 window.__dshDesktopUpdate=function(s){ state=s||{phase:'idle'}; render(); };
 if(!window.dshDesktop){
  window.dshDesktop={protocolVersion:1,updates:{
   status:function(){ return Promise.resolve(state); },
   open:function(){ send(); return Promise.resolve(); },
   subscribe:function(l){ listeners.push(l); return function(){ var i=listeners.indexOf(l); if(i>=0) listeners.splice(i,1); }; }
  }};
 }
 if(document.body) boot(); else document.addEventListener('DOMContentLoaded', boot);
})();

(function(){
 // 一体化标题栏：桌面壳的窗口控制融进网页头部，不再有独立的内外两道框。
 // 1) 右上角固定三个窗口按钮（最小化 / 最大化 / 关闭），颜色继承网页主题（深浅色自适应）；
 // 2) 顶部空白区按下拖动窗口、双击最大化 —— 用文档级捕获监听转发给桌面壳，
 //    不叠加任何遮挡层，网页头部自己的按钮（侧边栏开关、标签页等）照常可点；
 // 3) 注入完成上报 dsh-chrome-ready，桌面壳收到后隐藏自己的兜底标题栏。
 if (window.__dshChromeReady) return;
 if (window.top !== window) return; // 只在顶层文档注入，iframe 里不生成窗口按钮
 window.__dshChromeReady = true;
 var BOX='dsh-desktop-caption';
 var DRAG_H=44;
 function post(m){ try{ window.chrome.webview.postMessage(m); }catch(e){} }
 function svg(tag,attrs){
  var e=document.createElementNS('http://www.w3.org/2000/svg',tag);
  for(var k in attrs){ if(attrs.hasOwnProperty(k)) e.setAttribute(k,attrs[k]); }
  return e;
 }
 function drawMax(s,maxed){
  while(s.firstChild) s.removeChild(s.firstChild);
  if(maxed){
   // 还原图标：两个错开的小方框
   s.appendChild(svg('rect',{x:2.5,y:0.5,width:7,height:7,stroke:'currentColor','stroke-width':1,fill:'none'}));
   s.appendChild(svg('rect',{x:0.5,y:2.5,width:7,height:7,stroke:'currentColor','stroke-width':1,fill:'none'}));
  }else{
   s.appendChild(svg('rect',{x:0.5,y:0.5,width:9,height:9,stroke:'currentColor','stroke-width':1,fill:'none'}));
  }
 }
 function icon(kind){
  var s=svg('svg',{width:10,height:10,viewBox:'0 0 10 10'});
  if(kind==='min'){
   s.appendChild(svg('path',{d:'M0 7.5 H10',stroke:'currentColor','stroke-width':1,fill:'none'}));
  }else if(kind==='max'){
   drawMax(s,false);
  }else{
   s.appendChild(svg('path',{d:'M0 0 L10 10 M10 0 L0 10',stroke:'currentColor','stroke-width':1,fill:'none'}));
  }
  return s;
 }
 function interactive(t){
  if(t && t.closest){
   if(t.closest('button,a,input,textarea,select,label,[role=button],[contenteditable],iframe,object,embed,video')) return true;
   if(t.closest('#'+BOX)) return true;
  }
  return false;
 }
 // 窗口按钮固定钉死在右上角，绝不移动（之前按网页控件位置动态避让，点工作区/三个点
 // 弹出菜单后几何一变按钮就跟着回弹，观感很差）。让位改为反向操作：给 dsh 头部容器
 // 注入右内边距，把网页自己的右上角控件往左推，永远腾出三个按钮的宽度。
 // 只查这一小组选择器（不是全树遍历），配合节流不会重蹈 v0.8.2 的重排风暴。
 var CAP_W=140; // 3 个按钮 × 46px，留 2px 余量
 function reserve(){
  var box=document.getElementById(BOX);
  var cands=document.querySelectorAll(
   'button,a,[role=button],[class*=button],[class*=Button],[class*=trigger],[class*=Trigger],[class*=icon],[class*=Icon]');
  var w=window.innerWidth;
  for(var i=0;i<cands.length;i++){
   var el=cands[i];
   if(box&&box.contains(el)) continue;
   var r=el.getBoundingClientRect();
   if(r.width<10||r.width>180||r.height<12||r.height>72) continue;
   if(r.top>56||r.bottom<0) continue;
   if(r.right<w-280) continue; // 只看最右 280px 内的顶部控件
   // 直接给控件的父容器注入右内边距：flex 行内子项必然被推向左边，
   // 不依赖高层祖先是否传递 padding（v0.8.7 就是因此漏推、仍然重叠）
   var p=el.parentElement;
   if(p&&!p.__dshPad){
    p.__dshPad=true;
    p.style.paddingRight=CAP_W+'px';
    p.style.boxSizing='border-box';
   }
   break;
  }
 }
 function place(){
  var box=document.getElementById(BOX);
  if(!box) return;
  box.style.left='auto';
  box.style.right='0px';
  reserve();
 }
 window.__dshCaptionPlace=place;
 function build(){
  var box=document.getElementById(BOX);
  if(box) return;
  var st=document.createElement('style');
  st.id='dsh-desktop-caption-css';
  st.textContent='#'+BOX+'{position:fixed;top:0;right:0;z-index:2147483000;display:flex;height:40px;user-select:none;-webkit-user-select:none;}'
   +'.dsh-cap-btn{width:46px;height:40px;display:flex;align-items:center;justify-content:center;color:inherit;opacity:.72;cursor:default;'
   +'transition:opacity .16s ease,background-color .16s ease;}'
   +'.dsh-cap-btn svg{border-radius:3px;}'
   +'.dsh-cap-btn:hover{background:rgba(128,128,128,.14);opacity:1;}'
   +'.dsh-cap-btn:active{background:rgba(128,128,128,.22);}'
   +'.dsh-cap-btn[data-kind=close]:hover{background:rgba(232,17,35,.88);color:#fff;opacity:1;}'
   +'.dsh-cap-btn[data-kind=close]:active{background:rgba(200,15,30,.92);color:#fff;}';
  (document.head||document.documentElement).appendChild(st);
  box=document.createElement('div');
  box.id=BOX;
  var kinds=['min','max','close'];
  for(var i=0;i<kinds.length;i++){
   (function(kind){
    var b=document.createElement('div');
    b.className='dsh-cap-btn';
    b.setAttribute('data-kind',kind);
    b.appendChild(icon(kind));
    b.addEventListener('click',function(){ post('dsh-'+kind); });
    box.appendChild(b);
   })(kinds[i]);
  }
  document.body.appendChild(box);
 }
 // 最大化状态变化时由桌面壳回调，切换最大化 / 还原图标
 window.__dshCaptionMax=function(m){
  var box=document.getElementById(BOX);
  if(!box||!box.children[1]) return;
  var s=box.children[1].firstChild;
  if(s) drawMax(s,!!m);
 };
 // 顶部 44px 内的空白处：按下拖动窗口（阻止网页选中文本），双击切换最大化。
 // 交互元素（按钮 / 链接 / 输入框…）不拦截，点击行为完全不受影响。
 document.addEventListener('mousedown',function(e){
  if(e.button!==0||e.clientY>DRAG_H) return;
  if(interactive(e.target)) return;
  e.preventDefault();
  post('dsh-drag');
 },true);
 document.addEventListener('dblclick',function(e){
  if(e.clientY>DRAG_H||interactive(e.target)) return;
  post('dsh-dblmax');
 },true);
 function boot(){ build(); place(); post('dsh-chrome-ready'); }
 if(document.body) boot(); else document.addEventListener('DOMContentLoaded', boot);
})();
";

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        // ---- 无边框窗口自绘所需 ----
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_NCHITTEST = 0x0084;
        private const int HTCAPTION = 2;
        private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                          HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public MainForm()
        {
            baseDir = AppDomain.CurrentDomain.BaseDirectory;
            nodePath = Path.Combine(baseDir, "node.exe");
            dshPath = Path.Combine(baseDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");

            Text = "DeepSeek Harness";
            ClientSize = new Size(1280, 820);
            MinimumSize = new Size(760, 560);
            StartPosition = FormStartPosition.CenterScreen;
            // 无边框窗口自己画一圈 1px 描边：底色即边框色，子控件靠 Padding 内缩 1px
            BackColor = UI.Border;
            Padding = new Padding(1);
            // 无边框 + 自绘标题栏（系统标题栏无法与应用内容统一配色）
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.Dpi;
            // 无边框自绘窗口整体双缓冲，缩放/重绘时不再闪
            DoubleBuffered = true;
            Font = UI.MakeFont(9f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // 自绘界面若失败，绝不能让应用打不开 —— 降级为系统边框窗口，功能照常可用
            try
            {
                BuildTitleBar();
                BuildLoadingPlaceholder();
                BuildResizeGrips();
                LayoutChrome();
            }
            catch (Exception ex)
            {
                Log.Error("自绘界面初始化", ex);
                FallbackChrome();
            }

            InitializeTray();

            Shown += async delegate { await InitializeAsync(); };
        }

        // 自绘界面初始化失败时的降级路径：退回普通系统窗口，宁可不好看也不能打不开
        private void FallbackChrome()
        {
            try
            {
                for (int i = Controls.Count - 1; i >= 0; i--) Controls.RemoveAt(i);
                grips = null;
                titleBar = null;
                titleLabel = null;
                portBadge = null;
                loadingCard = null;
                loadingText = null;
                btnMin = null;
                btnMax = null;
                btnClose = null;
                Padding = new Padding(0);
                FormBorderStyle = FormBorderStyle.Sizable;
                Controls.Add(new Label
                {
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Text = "正在启动 DeepSeek Harness 本地服务…",
                    Font = SystemFonts.DefaultFont
                });
            }
            catch { }
        }

        // ---------- 窗口外观 ----------

        // CS_DROPSHADOW：无边框窗口没有系统边框阴影，手动加才有浮起感
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;
                return cp;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // 最大化时贴着屏幕边缘，再画 1px 描边就成了一条灰线，去掉
            Padding want = WindowState == FormWindowState.Maximized ? new Padding(0) : new Padding(1);
            if (!Padding.Equals(want)) Padding = want;
            LayoutChrome();
            LayoutGrips();
            SyncCaptionMaxState(); // 网页右上角窗口按钮的 最大化/还原图标 跟着切
        }

        // 无边框窗口没有系统边框，缩放热区需要自己做：
        // 用 8 个贴边的透明抓手控件（4 边 + 4 角）投递 WM_NCLBUTTONDOWN。
        // 之所以不用 WndProc 处理 WM_NCHITTEST：WebView2 覆盖了整个客户区，
        // 鼠标落在子窗口上时该消息根本不会派发到窗体，命中测试拿不到。
        private void BuildResizeGrips()
        {
            int[] hits = new int[]
            { HTTOPLEFT, HTTOP, HTTOPRIGHT, HTRIGHT, HTBOTTOMRIGHT, HTBOTTOM, HTBOTTOMLEFT, HTLEFT };
            grips = new ResizeGrip[hits.Length];
            for (int i = 0; i < hits.Length; i++)
            {
                grips[i] = new ResizeGrip(this, hits[i]);
                Controls.Add(grips[i]); // 最后添加 → 位于最上层
            }
            LayoutGrips();
        }

        private void LayoutGrips()
        {
            if (grips == null) return;
            int g = 6;
            int w = ClientSize.Width, h = ClientSize.Height;
            // 角（12x12）
            grips[0].Bounds = new Rectangle(0, 0, g * 2, g * 2);
            grips[2].Bounds = new Rectangle(w - g * 2, 0, g * 2, g * 2);
            grips[4].Bounds = new Rectangle(w - g * 2, h - g * 2, g * 2, g * 2);
            grips[6].Bounds = new Rectangle(0, h - g * 2, g * 2, g * 2);
            // 边
            grips[1].Bounds = new Rectangle(g * 2, 0, Math.Max(0, w - g * 4), g);
            grips[3].Bounds = new Rectangle(w - g, g * 2, g, Math.Max(0, h - g * 4));
            grips[5].Bounds = new Rectangle(g * 2, h - g, Math.Max(0, w - g * 4), g);
            grips[7].Bounds = new Rectangle(0, g * 2, g, Math.Max(0, h - g * 4));

            bool show = WindowState != FormWindowState.Maximized;
            for (int i = 0; i < grips.Length; i++) grips[i].Visible = show;
        }

        // WebView2 是运行时才加入的，加进来会盖住抓手，需要重新提到最上层
        private void BringGripsToFront()
        {
            if (grips == null) return;
            for (int i = 0; i < grips.Length; i++) grips[i].BringToFront();
        }

        // 自绘标题栏：白底 + 底部 1px 分隔线；左侧图标与标题，右侧本地端口 + 三个标准标题按钮
        private void BuildTitleBar()
        {
            titleBar = new Panel { Dock = DockStyle.Top, Height = UI.TitleBarHeight, BackColor = UI.Bar };
            titleBar.Paint += delegate (object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(UI.Separator))
                    e.Graphics.DrawLine(p, 0, titleBar.Height - 1, titleBar.Width, titleBar.Height - 1);
            };

            // 无边框窗口没有系统标题栏，鼠标按下时投递 HTCAPTION 交给系统处理拖动；
            // 双击标题栏最大化。拖动/双击必须同时挂到标题与图标上，
            // 否则点在它们上面时消息不会冒泡到 titleBar。
            MouseEventHandler drag = delegate (object s, MouseEventArgs e) { BeginDrag(e); };
            // MouseDoubleClick 的委托类型是 MouseEventHandler（带 MouseEventArgs），不是 EventHandler
            MouseEventHandler dbl = delegate (object s, MouseEventArgs e) { ToggleMaximize(); };
            titleBar.MouseDown += drag;
            titleBar.MouseDoubleClick += dbl;

            // 左侧：程序图标
            PictureBox appIcon = new PictureBox
            {
                Size = new Size(16, 16),
                Left = 12,
                Top = (UI.TitleBarHeight - 16) / 2,
                SizeMode = PictureBoxSizeMode.StretchImage,
                BackColor = UI.Bar
            };
            try
            {
                using (Icon ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                {
                    if (ic != null)
                    {
                        using (Bitmap src = ic.ToBitmap())
                            appIcon.Image = new Bitmap(src, 16, 16);
                    }
                }
            }
            catch { appIcon.Visible = false; }
            appIcon.MouseDown += drag;
            appIcon.MouseDoubleClick += dbl;

            // 左侧：标题（不再居中，跟随 Windows 习惯靠左）
            titleLabel = new Label
            {
                Text = "DeepSeek Harness",
                AutoSize = true,
                Left = 36,
                Top = 0,
                Height = UI.TitleBarHeight,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UI.Label,
                Font = UI.MakeFont(9.5f),
                BackColor = UI.Bar
            };
            titleLabel.MouseDown += drag;
            titleLabel.MouseDoubleClick += dbl;

            // 右侧：本地端口（服务就绪后会带上实际端口）
            portBadge = new Label
            {
                Text = "127.0.0.1:" + port,
                AutoSize = true,
                ForeColor = UI.LabelSecondary,
                Font = UI.MakeFont(8.5f),
                BackColor = UI.Bar,
                TextAlign = ContentAlignment.MiddleRight
            };
            portBadge.MouseDown += drag;

            // 右侧：最小化 / 最大化 / 关闭（Windows 顺序，关闭在最右）
            btnMin = new CaptionButton(this, CaptionButton.Kind.Minimize);
            btnMax = new CaptionButton(this, CaptionButton.Kind.Maximize);
            btnClose = new CaptionButton(this, CaptionButton.Kind.Close);
            btnMin.Click += delegate { WindowState = FormWindowState.Minimized; };
            btnMax.Click += delegate { ToggleMaximize(); };
            btnClose.Click += delegate { Close(); }; // 关窗即最小化到托盘，见 OnFormClosing

            // 先加的在 z-order 更靠上，按钮不会被标题文字遮挡（v0.7.0 就是被遮挡才点不到）
            titleBar.Controls.Add(btnClose);
            titleBar.Controls.Add(btnMax);
            titleBar.Controls.Add(btnMin);
            titleBar.Controls.Add(portBadge);
            titleBar.Controls.Add(titleLabel);
            titleBar.Controls.Add(appIcon);

            Controls.Add(titleBar);
        }

        // 标题栏是手写的绝对布局，尺寸变化（含最大化/还原）都要重排右侧元素
        private void LayoutChrome()
        {
            if (titleBar == null || titleBar.IsDisposed) return;
            int h = titleBar.Height;
            int bw = UI.CaptionButtonWidth;
            if (btnClose != null && !btnClose.IsDisposed)
                btnClose.Bounds = new Rectangle(titleBar.Width - bw, 0, bw, h);
            if (btnMax != null && !btnMax.IsDisposed)
            {
                btnMax.Bounds = new Rectangle(titleBar.Width - bw * 2, 0, bw, h);
                btnMax.Invalidate(); // 最大化/还原时图标要切换
            }
            if (btnMin != null && !btnMin.IsDisposed)
                btnMin.Bounds = new Rectangle(titleBar.Width - bw * 3, 0, bw, h);

            if (portBadge != null && !portBadge.IsDisposed)
            {
                portBadge.Top = (h - portBadge.Height) / 2;
                portBadge.Left = Math.Max(48, titleBar.Width - bw * 3 - portBadge.Width - 16);
            }
        }

        // 无边框窗口没有系统标题栏，鼠标按下时投递 HTCAPTION 交给系统处理拖动
        private void BeginDrag(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && WindowState != FormWindowState.Maximized)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
        }

        // 端口可能在启动期回退（3080 被占用），确定后刷新标题栏右侧的端口文字
        private void UpdatePortBadge()
        {
            if (portBadge == null || portBadge.IsDisposed) return;
            portBadge.Text = "127.0.0.1:" + port;
            LayoutChrome();
        }

        // 一体化标题栏注入成功后隐藏兜底标题栏：窗口按钮由网页右上角渲染，
        // 与网页头部合成一道框，不再有桌面壳额外的内外两层。
        // 若注入脚本被拦截或页面异常，兜底标题栏保持可见，窗口始终可控。
        private void HideFallbackTitleBar()
        {
            try
            {
                if (titleBar != null && !titleBar.IsDisposed && titleBar.Visible)
                    titleBar.Visible = false; // Dock 布局会自动把 WebView 扩展到整个窗口
            }
            catch { }
        }

        // 网页顶部空白区按下后的窗口拖动（等价于标题栏拖动）
        private void BeginDragFromWeb()
        {
            if (WindowState != FormWindowState.Maximized)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
        }

        // 最大化状态变化时通知网页切换窗口按钮的 最大化/还原图标
        private void SyncCaptionMaxState()
        {
            if (webView == null || webView.CoreWebView2 == null || IsDisposed) return;
            string maxed = WindowState == FormWindowState.Maximized ? "true" : "false";
            try
            {
                webView.CoreWebView2.ExecuteScriptAsync(
                    "if(window.__dshCaptionMax)window.__dshCaptionMax(" + maxed + ");");
            }
            catch { }
        }

        // 启动占位：居中的扁平卡片 + 走马灯进度条，避免启动期白屏。
        // 用双缓冲面板：启动期每 500ms 会刷新一次等待文案，普通 Panel 会明显闪烁。
        private void BuildLoadingPlaceholder()
        {
            loadingCard = new BufferedPanel { Dock = DockStyle.Fill, BackColor = UI.WindowBg };

            Panel card = new BufferedPanel { Size = new Size(420, 156), BackColor = UI.WindowBg };
            card.Paint += delegate (object s, PaintEventArgs e)
            {
                // 柔和圆角卡片：先铺窗口底色，再画圆角矩形盖上去
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (SolidBrush bg = new SolidBrush(UI.WindowBg))
                    e.Graphics.FillRectangle(bg, 0, 0, card.Width, card.Height);
                using (System.Drawing.Drawing2D.GraphicsPath path = RoundedPath(new Rectangle(0, 0, card.Width - 1, card.Height - 1), 16))
                {
                    using (SolidBrush fill = new SolidBrush(UI.Card))
                        e.Graphics.FillPath(fill, path);
                    using (Pen p = new Pen(UI.Border))
                        e.Graphics.DrawPath(p, path);
                }
            };

            Label title = new Label
            {
                Text = "正在启动 DeepSeek Harness…",
                Font = UI.MakeFont(12f, FontStyle.Bold),
                ForeColor = UI.Label,
                Left = 20, Top = 30, Width = 380, Height = 26,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = UI.Card
            };
            loadingText = new Label
            {
                Text = "首次启动需要几十秒，请稍候",
                Font = UI.MakeFont(9f),
                ForeColor = UI.LabelSecondary,
                Left = 20, Top = 60, Width = 380, Height = 24,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = UI.Card
            };

            // 走马灯（系统原生不确定进度条），比自绘转圈更稳也比纯文字更有反馈
            ProgressBar bar = new ProgressBar
            {
                Left = 40, Top = 96, Width = 340, Height = 6,
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30
            };

            Label version = new Label
            {
                Text = "v" + AppInfo.Version,
                Font = UI.MakeFont(8.5f),
                ForeColor = UI.LabelSecondary,
                Left = 20, Top = 118, Width = 380, Height = 20,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = UI.Card
            };

            card.Controls.Add(title);
            card.Controls.Add(loadingText);
            card.Controls.Add(bar);
            card.Controls.Add(version);
            loadingCard.Controls.Add(card);
            loadingCard.Resize += delegate { CenterLoadingCard(card); };
            Controls.Add(loadingCard);
        }

        private void CenterLoadingCard(Panel card)
        {
            card.Left = Math.Max(0, (loadingCard.Width - card.Width) / 2);
            card.Top = Math.Max(0, (loadingCard.Height - card.Height) / 2);
        }

        // 圆角矩形路径（柔和卡片用）
        private static System.Drawing.Drawing2D.GraphicsPath RoundedPath(Rectangle r, int radius)
        {
            System.Drawing.Drawing2D.GraphicsPath p = new System.Drawing.Drawing2D.GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private void SetLoadingText(string text)
        {
            try
            {
                if (loadingText != null && !loadingText.IsDisposed)
                    loadingText.Text = text;
            }
            catch { }
        }

        private void RemoveLoadingPlaceholder()
        {
            if (loadingCard == null) return;
            try { Controls.Remove(loadingCard); loadingCard.Dispose(); } catch { }
            loadingCard = null;
            loadingText = null;
        }

        private void ToggleMaximize()
        {
            if (WindowState == FormWindowState.Maximized)
            {
                WindowState = FormWindowState.Normal;
            }
            else
            {
                // 无边框最大化默认会盖住任务栏，限定在工作区范围内
                try { MaximizedBounds = Screen.FromHandle(Handle).WorkingArea; } catch { }
                WindowState = FormWindowState.Maximized;
            }
        }

        private async Task InitializeAsync()
        {
            try
            {
                // 启动提速的关键：本地服务启动与 WebView2 运行时初始化同时进行。
                // 以前是「等服务就绪 → 再初始化 WebView2」串行走，白白多花 1-3 秒，
                // 而且 WebView2 的初始化压在 UI 线程上，正是启动进度条卡顿的来源。
                Task<string> serverTask = StartServerIfNeededAsync();
                Task<CoreWebView2Environment> envTask = PrepareWebViewEnvironmentAsync();

                string startUrl = await serverTask;
                if (shuttingDown) return;
                UpdatePortBadge();
                await InitializeWebViewAsync(startUrl, envTask);
                if (!string.IsNullOrEmpty(portNotice)) ShowBalloon(portNotice, ToolTipIcon.Info);
                // 界面就绪后再静默查一次更新，有新版只在左下角按钮提示，不打断使用
                await RunUpdateFlowAsync(false);
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

        // 提前把 WebView2 运行时拉起来（与服务启动并行），拿到环境后等真正建控件时再 attach
        private async Task<CoreWebView2Environment> PrepareWebViewEnvironmentAsync()
        {
            try
            {
                if (!IsWebView2RuntimeAvailable()) return null;
                return await CoreWebView2Environment.CreateAsync(null, WebViewUserDataDir, null);
            }
            catch { return null; }
        }

        private static string WebViewUserDataDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekHarness", "EBWebView");
            }
        }

        // WebView2 初始化（带重试：偶发 E_ABORT，多为上一次实例未完全退出导致，稍候重试即可）
        private async Task InitializeWebViewAsync(string startUrl, Task<CoreWebView2Environment> envTask)
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
                bool retry = false;
                try
                {
                    CoreWebView2Environment env = null;
                    if (envTask != null)
                    {
                        try { env = await envTask; }
                        catch { env = null; }
                    }
                    if (env == null)
                        env = await CoreWebView2Environment.CreateAsync(null, WebViewUserDataDir, null);
                    await AttachWebViewAsync(startUrl, env);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    envTask = null; // 环境创建失败过一次，下次重新建
                    retry = attempt < 3;
                }
                // 注意：await 不能写在 catch 块里 —— CI 用的 .NET Framework 自带编译器
                // 语言版本较老（CS1985: Cannot await in the body of a catch clause），
                // 所以重试前的等待必须挪到 catch 外面。
                if (retry) await Task.Delay(2000 * attempt);
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

        // 建好 WebView2 控件并加载界面；失败抛异常并清理控件
        private async Task AttachWebViewAsync(string startUrl, CoreWebView2Environment env)
        {
            WebView2 view = null;
            try
            {
                view = new WebView2 { Dock = DockStyle.Fill };
                Controls.Add(view);

                await view.EnsureCoreWebView2Async(env);

                // 网页里的「侧边栏更新按钮」与一体化标题栏：每个文档创建时都注入，
                // 刷新/跳转后依然在。注入完成后网页会上报 dsh-chrome-ready，
                // 桌面壳随即隐藏兜底标题栏（窗口按钮改由网页右上角渲染）。
                // 注意 API 名带 Async 后缀（WebView2 SDK 1.0.4129.50），写成不带 Async 的旧名会编译不过。
                // 注入失败不能影响主流程（界面照常可用、兜底标题栏保持可见），所以只记日志。
                try
                {
                    await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(UpdateBridgeScript);
                    view.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                }
                catch (Exception ex)
                {
                    Log.Error("注入更新按钮脚本", ex);
                }

                view.CoreWebView2.Settings.AreDevToolsEnabled = false;
                view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                view.CoreWebView2.Settings.IsStatusBarEnabled = false;
                // 禁止误操作（Ctrl+滚轮 / 捏合）改变缩放导致网页重新栅格化后发虚
                view.CoreWebView2.Settings.IsZoomControlEnabled = false;
                // 底色与主题一致（深色），避免加载期闪一下突兀的白底
                view.DefaultBackgroundColor = UI.WindowBg;
                view.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                // startUrl 形如 http://127.0.0.1:3080/?token=xxx：
                // 服务端校验 token 后写入会话 Cookie 并 303 跳转到干净的 /，之后一切正常。
                view.Source = new Uri(startUrl);

                // WebView 就绪，移除启动占位卡片
                RemoveLoadingPlaceholder();
                webView = view;
                BringGripsToFront(); // WebView 后加入会盖住边缘抓手，重新提到最上层
                // 当前状态立刻同步一次：若启动时已静默查到新版本，按钮一出现就是「新版本」
                PublishUpdateState(updatePhase, updatePercent, pendingUpdate == null ? "" : pendingUpdate.Version, true);
            }
            catch
            {
                try { if (view != null) { Controls.Remove(view); view.Dispose(); } } catch { }
                webView = null;
                throw;
            }
        }

        // 网页发来的消息：
        //   'dsh-update-open'  —— 网页更新按钮被点击，进入升级流程
        //   'dsh-theme:...'    —— 网页主题背景色上报，描边等兜底界面跟着切换
        //   'dsh-chrome-ready' —— 一体化标题栏注入完成，隐藏兜底标题栏
        //   'dsh-drag'         —— 网页顶部空白区按下，开始拖动窗口
        //   'dsh-dblmax'       —— 网页顶部空白区双击，切换最大化
        //   'dsh-min'/'dsh-max'/'dsh-close' —— 网页右上角窗口按钮
        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string msg = null;
            try { msg = e.TryGetWebMessageAsString(); } catch { }
            if (string.IsNullOrEmpty(msg)) return;
            if (msg.IndexOf("dsh-update-open", StringComparison.Ordinal) >= 0)
            {
                if (IsDisposed) return;
                BeginInvoke(new Action(() => { Task ignored = OnUpdateOpenAsync(); }));
                return;
            }
            if (msg.IndexOf("dsh-theme:", StringComparison.Ordinal) == 0)
            {
                Color bg = ParseCssColor(msg.Substring(10));
                if (!bg.IsEmpty && !IsDisposed)
                    BeginInvoke(new Action(() => ApplyChromeTheme(bg)));
                return;
            }
            if (msg == "dsh-chrome-ready")
            {
                // 注入完成即同步一次最大化状态：否则刚加载完网页按钮默认画成
                // 还原图标，窗口实际最大化时右数第二个按钮就不是方形最大化样式
                if (!IsDisposed) BeginInvoke(new Action(() => { HideFallbackTitleBar(); SyncCaptionMaxState(); }));
                return;
            }
            if (msg == "dsh-drag")
            {
                if (!IsDisposed) BeginInvoke(new Action(BeginDragFromWeb));
                return;
            }
            if (msg == "dsh-dblmax" || msg == "dsh-max")
            {
                if (!IsDisposed) BeginInvoke(new Action(ToggleMaximize));
                return;
            }
            if (msg == "dsh-min")
            {
                if (!IsDisposed) BeginInvoke(new Action(() => { WindowState = FormWindowState.Minimized; }));
                return;
            }
            if (msg == "dsh-close")
            {
                if (!IsDisposed) BeginInvoke(new Action(Close));
                return;
            }
        }

        // 解析 CSS 颜色：支持 rgb(r, g, b) 与 #rrggbb 两种写法（够用即可）
        private static Color ParseCssColor(string s)
        {
            if (string.IsNullOrEmpty(s)) return Color.Empty;
            try
            {
                s = s.Trim();
                if (s[0] == '#')
                {
                    string hex = s.Substring(1);
                    if (hex.Length != 6) return Color.Empty;
                    int r = Convert.ToInt32(hex.Substring(0, 2), 16);
                    int g = Convert.ToInt32(hex.Substring(2, 2), 16);
                    int b = Convert.ToInt32(hex.Substring(4, 2), 16);
                    return Color.FromArgb(r, g, b);
                }
                if (s.IndexOf("rgb", StringComparison.OrdinalIgnoreCase) == 0)
                {
                    int a = s.IndexOf('('), b2 = s.IndexOf(')');
                    if (a < 0 || b2 < 0 || b2 <= a) return Color.Empty;
                    string[] parts = s.Substring(a + 1, b2 - a - 1).Split(',');
                    if (parts.Length < 3) return Color.Empty;
                    return Color.FromArgb(
                        Clamp255(int.Parse(parts[0].Trim())),
                        Clamp255(int.Parse(parts[1].Trim())),
                        Clamp255(int.Parse(parts[2].Trim())));
                }
            }
            catch { }
            return Color.Empty;
        }

        private static int Clamp255(int v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }

        private static Color Lighten(Color c, double f)
        {
            return Color.FromArgb(
                Clamp255((int)(c.R + (255 - c.R) * f)),
                Clamp255((int)(c.G + (255 - c.G) * f)),
                Clamp255((int)(c.B + (255 - c.B) * f)));
        }

        private static Color Darken(Color c, double f)
        {
            return Color.FromArgb(
                Clamp255((int)(c.R * (1 - f))),
                Clamp255((int)(c.G * (1 - f))),
                Clamp255((int)(c.B * (1 - f))));
        }

        // 让标题栏 / 描边 / 标题按钮跟随网页主题：
        // 深色界面就配深色标题栏与柔和的同系描边，整窗视觉一体（旧版白标题栏 + 深色
        // 内容对比太生硬，是用户直接反馈的问题）。浅色主题恢复默认配色。
        private void ApplyChromeTheme(Color bg)
        {
            if (IsDisposed) return;
            double lum = 0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B;
            if (lum < 140)
            {
                UI.WindowBg = bg;
                UI.Bar = bg;
                UI.Card = Lighten(bg, 0.06);
                // 描边取界面色与黑之间：看得见轮廓但不扎眼
                UI.Border = Darken(bg, 0.45);
                UI.Separator = Lighten(bg, 0.10);
                UI.Hover = Lighten(bg, 0.12);
                UI.Pressed = Lighten(bg, 0.20);
                UI.Label = Color.FromArgb(235, 235, 238);
                UI.LabelSecondary = Color.FromArgb(158, 162, 170);
            }
            else
            {
                UI.ResetLightTheme();
            }
            try
            {
                BackColor = UI.Border; // 1px 描边 = 窗体底色
                if (titleBar != null && !titleBar.IsDisposed)
                {
                    titleBar.BackColor = UI.Bar;
                    titleBar.Invalidate();
                }
                if (titleLabel != null && !titleLabel.IsDisposed)
                { titleLabel.ForeColor = UI.Label; titleLabel.BackColor = UI.Bar; }
                if (portBadge != null && !portBadge.IsDisposed)
                { portBadge.ForeColor = UI.LabelSecondary; portBadge.BackColor = UI.Bar; }
                if (loadingCard != null && !loadingCard.IsDisposed)
                {
                    loadingCard.BackColor = UI.WindowBg;
                    // 占位卡片里的子控件也跟着换色（卡 loading 时外框同样协调）
                    int idx = 0;
                    foreach (Control card in loadingCard.Controls)
                    {
                        card.BackColor = UI.Card;
                        int sub = 0;
                        foreach (Control t in card.Controls)
                        {
                            Label lt = t as Label;
                            if (lt != null)
                            {
                                lt.BackColor = UI.Card;
                                lt.ForeColor = sub == 0 ? UI.Label : UI.LabelSecondary;
                            }
                            sub++;
                        }
                        idx++;
                    }
                    loadingCard.Invalidate();
                }
                if (btnMin != null && !btnMin.IsDisposed) { btnMin.BackColor = UI.Bar; btnMin.Invalidate(); }
                if (btnMax != null && !btnMax.IsDisposed) { btnMax.BackColor = UI.Bar; btnMax.Invalidate(); }
                if (btnClose != null && !btnClose.IsDisposed) { btnClose.BackColor = UI.Bar; btnClose.Invalidate(); }
                Invalidate();
            }
            catch { }
        }

        private async Task OnUpdateOpenAsync()
        {
            if (updateBusy) return;
            // 已经下载好：这一下就是「安装并重启」——关掉自己，静默安装，装完由安装包拉起
            if (!string.IsNullOrEmpty(downloadedInstaller) && File.Exists(downloadedInstaller))
            {
                await InstallAndRestartAsync();
                return;
            }
            // 已经查到新版本：直接开下
            if (pendingUpdate != null)
            {
                await DownloadUpdateAsync(pendingUpdate, pendingUpdate.Mandatory);
                return;
            }
            await RunUpdateFlowAsync(true);
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
            // 端口探测、WMI 查残留进程、启动 node 全都是阻塞操作。以前它们跑在 UI 线程上，
            // 和 WebView2 初始化挤在一起，正是启动动画一卡一卡的来源；现在丢到后台线程。
            await Task.Run(new Action(PrepareAndLaunchServer));
            return await WaitForReadyUrlAsync();
        }

        // 后台线程：端口检查 → 清理上一轮残留 → 拉起 dsh 服务进程
        private void PrepareAndLaunchServer()
        {
            // 端口已被占用：先尝试停掉"本目录 dsh"的残留进程再重新拉起，
            // 因为新版 dsh 需要本次进程的一次性 token，接管旧进程拿不到。
            if (IsPortOpen(port))
            {
                if (!StopOwnedServer())
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

            LaunchServerProcess();
        }

        private void LaunchServerProcess()
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = nodePath;
            // --no-open：不额外打开系统默认浏览器（界面由本窗口承载）
            // --port：显式固定端口，与 port 字段保持一致
            psi.Arguments = "\"" + dshPath + "\" web --no-open --port " + port;
            psi.WorkingDirectory = baseDir;
            psi.UseShellExecute = false;
            // 关键：这里原本写的是 CreateNoWindow = false + WindowStyle = Hidden，
            // 但 WindowStyle 只在 UseShellExecute = true 时生效，UseShellExecute = false 时
            // 子进程会新建一个真正的控制台 —— 也就是用户看到的"启动时弹出一个终端"。
            // 改成 CreateNoWindow = true：压根不创建控制台窗口（stdout 重定向照常工作）。
            psi.CreateNoWindow = true;
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

                bool open = await Task.Run(() => IsPortOpen(port));
                if (open && portOpenAt == DateTime.MinValue) portOpenAt = DateTime.UtcNow;
                if (open && portOpenAt != DateTime.MinValue &&
                    DateTime.UtcNow - portOpenAt > TimeSpan.FromSeconds(10))
                    return "http://127.0.0.1:" + port + "/";

                if (DateTime.UtcNow - start > TimeSpan.FromSeconds(90))
                    throw new Exception("等待 dsh 服务就绪超时（90 秒）。" + ErrorTailText());

                // 不再显示已等待秒数（用户反馈：首次启动不要跳秒），固定一句提示
                BeginInvoke(new Action(() =>
                    SetLoadingText("正在启动本地服务，请稍候…")));
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
        // 同步版本：只在后台线程调用（里面有 WMI 查询与睡眠等待）。
        private bool StopOwnedServer()
        {
            int pid = FindOwnedServerPid();
            if (pid <= 0) return false;
            KillProcessTree(pid);
            for (int i = 0; i < 24; i++)
            {
                Thread.Sleep(500);
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
            // 左键单击 = 恢复主界面；右键只弹托盘菜单（提示栏），不弹主界面。
            // 注意必须用 MouseClick 且判断按钮 —— NotifyIcon 的 Click 事件左右键都会触发，
            // 挂在 Click 上就会出现「右键也把主界面拉起来」的行为（用户反馈过）。
            trayIcon.MouseClick += (s, ev) =>
            {
                if (ev.Button == MouseButtons.Left) ShowForm();
            };
            // 点更新提示气泡直接进升级流程；其它气泡（如端口提示）则恢复窗口
            trayIcon.BalloonTipClicked += (s, ev) =>
            {
                if (pendingUpdate != null)
                {
                    UpdateInfo info = pendingUpdate;
                    Task ignored = DownloadUpdateAsync(info, info.Mandatory);
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
            update.Click += async (s, ev) => await RunUpdateFlowAsync(true);
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
        //
        // 注意：v0.8.0 起服务进程以 CreateNoWindow = true 启动（不再弹出终端窗口），
        // 它没有控制台，AttachConsole 会失败 —— 于是直接走进程树强杀。这样虽拿不到
        // dsh 的 dispose 回调，但换来的是"启动不再闪一个黑框"，且退出必定无残留进程。
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

        // ---------- 本地服务停止 ----------

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

        // ---------- 更新检查与升级 ----------

        // 网页里的左下角更新按钮是否已生效（注入脚本 + WebView2 就绪）
        private bool WebUpdateUi
        {
            get { return webView != null && webView.CoreWebView2 != null; }
        }

        // 把更新状态推给网页里的按钮：
        // idle / checking / available / downloading / ready / installing / uptodate / error
        private void PublishUpdateState(string phase, int percent, string version, bool force)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => PublishUpdateState(phase, percent, version, force))); }
                catch { }
                return;
            }
            bool changed = force || phase != updatePhase || percent != updatePercent;
            updatePhase = phase;
            updatePercent = percent;
            if (!changed || !WebUpdateUi) return;

            string v = string.IsNullOrEmpty(version)
                ? "null"
                : "\"" + version.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            string js = "if(window.__dshDesktopUpdate)window.__dshDesktopUpdate({phase:'" +
                        phase + "',percent:" + percent + ",version:" + v + "});";
            try { webView.ExecuteScriptAsync(js); } catch { }
        }

        // 统一的更新流程：检查 → 下载 → 界面上点「安装并重启」→ 关闭自己静默安装 → 装完自动拉起。
        // userInitiated=false 用于启动时的静默检查，只在左下角挂个「新版本」，不打断使用。
        private async Task RunUpdateFlowAsync(bool userInitiated)
        {
            if (updateBusy) return;
            updateBusy = true;
            try
            {
                PublishUpdateState("checking", 0, "", false);
                UpdateInfo info = await FetchLatestReleaseAsync();
                if (info == null)
                {
                    PublishUpdateState("error", 0, "", true);
                    if (userInitiated && !WebUpdateUi)
                        MessageBox.Show("暂时无法获取更新信息，请稍后重试，或到 GitHub Releases 页面手动下载。",
                            "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (!IsNewerVersion(info.Version, AppInfo.Version))
                {
                    pendingUpdate = null;
                    if (userInitiated)
                    {
                        if (WebUpdateUi)
                        {
                            PublishUpdateState("uptodate", 0, info.Version, true);
                            await Task.Delay(2500);
                            PublishUpdateState("idle", 0, "", true);
                        }
                        else
                        {
                            MessageBox.Show("当前已是最新版本（v" + AppInfo.Version + "）。",
                                "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                    else PublishUpdateState("idle", 0, "", false);
                    return;
                }

                pendingUpdate = info;
                // 低于最低可用版本：必须弹窗告知并强制升级，不能只靠按钮（用户可能根本不看）
                if (info.Mandatory)
                {
                    if (!ConfirmUpdate(info)) { PublishUpdateState("available", 0, info.Version, true); return; }
                }
                else if (!userInitiated)
                {
                    PublishUpdateState("available", 0, info.Version, true);
                    if (!WebUpdateUi)
                        ShowBalloon("发现新版本 v" + info.Version + "，点此升级（或右键托盘选「检查更新…」）。",
                            ToolTipIcon.Info);
                    return;
                }

                await DownloadUpdateAsync(info, info.Mandatory);
            }
            catch (Exception ex)
            {
                PublishUpdateState("error", 0, "", true);
                if (!userInitiated) return;
                if (WebUpdateUi)
                {
                    // 界面上有按钮时按钮自身会变成「重试更新」，这里再补一条托盘提示，
                    // 免得用户没注意按钮、以为点了没反应。
                    ShowBalloon("检查更新失败：" + ex.Message +
                        "\n请稍后重试，或到 GitHub Releases 页面手动下载（右键托盘可「打开下载页面」）。",
                        ToolTipIcon.Warning);
                }
                else
                {
                    MessageBox.Show("检查更新失败：" + ex.Message, "检查更新",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            finally { updateBusy = false; }
        }

        // 强制升级时的确认弹窗（普通升级不弹窗，走界面按钮）
        private bool ConfirmUpdate(UpdateInfo info)
        {
            string notes = info.Notes ?? "";
            if (notes.Length > 600) notes = notes.Substring(0, 600) + "…";
            DialogResult r = MessageBox.Show(
                "当前版本 v" + AppInfo.Version + " 已停用，需要升级到 v" + info.Version + " 或更高版本。\r\n\r\n" +
                (string.IsNullOrEmpty(notes) ? "" : notes + "\r\n\r\n") +
                "是否现在下载并升级？\r\n（下载完成后会自动关闭应用、静默覆盖安装，装好自动重新打开）",
                "DeepSeek Harness 更新", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            return r == DialogResult.Yes;
        }

        // 下载安装包：进度直接显示在左下角按钮上（"下载中 42%"），
        // 没有网页按钮时退回独立的进度窗口。
        private async Task DownloadUpdateAsync(UpdateInfo info, bool autoInstall)
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

            PublishUpdateState("downloading", 0, info.Version, true);
            ProgressForm progress = WebUpdateUi ? null : new ProgressForm("正在下载更新 v" + info.Version);
            if (progress != null) progress.Show(this);

            WebClient wc = null;
            System.Threading.Timer watchdog = null;
            try
            {
                wc = new TimedWebClient(30000);
                DateTime lastProgress = DateTime.UtcNow;
                wc.Headers.Add("User-Agent", "DeepSeekHarness-Desktop/" + AppInfo.Version);
                wc.DownloadProgressChanged += delegate (object s, DownloadProgressChangedEventArgs ev)
                {
                    lastProgress = DateTime.UtcNow;
                    PublishUpdateState("downloading", ev.ProgressPercentage, info.Version, false);
                    if (progress != null)
                        progress.UpdateProgress(ev.ProgressPercentage,
                            FormatSize(ev.BytesReceived), FormatSize(ev.TotalBytesToReceive));
                };
                TaskCompletionSource<object> done = new TaskCompletionSource<object>();
                wc.DownloadFileCompleted += delegate (object s, System.ComponentModel.AsyncCompletedEventArgs ev)
                {
                    if (ev.Error != null) done.TrySetException(ev.Error);
                    else done.TrySetResult(null);
                };
                // 看门狗：网络抽风时下载可能既不报错也不推进，一直停在「下载中 x%」。
                // 超过 DownloadStallMs 没有新进度就主动取消，让界面回到可重试状态。
                watchdog = new System.Threading.Timer(delegate (object s)
                {
                    try
                    {
                        if ((DateTime.UtcNow - lastProgress).TotalMilliseconds >= DownloadStallMs)
                            wc.CancelAsync();
                    }
                    catch { }
                }, null, 10000, 10000);

                wc.DownloadFileAsync(new Uri(info.InstallerUrl), file);
                await done.Task;
            }
            catch (Exception ex)
            {
                // 下载失败（含超时 / 主动取消）不能再抛给调用方：托盘菜单那条路径没有
                // try/catch，抛出去就成了未处理异常。统一在这里回到「重试更新」状态。
                PublishUpdateState("error", 0, info.Version, true);
                if (!WebUpdateUi && !IsDisposed)
                    MessageBox.Show("下载更新失败：" + ex.Message +
                        "\n\n请稍后重试，或到 GitHub Releases 页面手动下载。",
                        "DeepSeek Harness 更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            finally
            {
                if (watchdog != null) { try { watchdog.Dispose(); } catch { } }
                if (wc != null) { try { wc.Dispose(); } catch { } }
                if (progress != null) { progress.Close(); progress.Dispose(); }
            }

            downloadedInstaller = file;
            downloadedVersion = info.Version;
            PublishUpdateState("ready", 100, info.Version, true);
            // 强制升级不再等用户点：直接走完「关闭 → 静默安装 → 自动重开」
            if (autoInstall)
            {
                await Task.Delay(800);
                await InstallAndRestartAsync();
            }
        }

        // 关闭应用 → 静默覆盖安装 → 由安装包成功后自行拉起新版。
        // 全程不弹出 NSIS 安装向导（/S），与主流桌面 Agent 的「安装并重启」一致。
        private async Task InstallAndRestartAsync()
        {
            string file = downloadedInstaller;
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return;

            PublishUpdateState("installing", 100, downloadedVersion, true);
            await Task.Delay(300);

            shuttingDown = true;
            trayExit = true;
            try { if (trayIcon != null) trayIcon.Visible = false; } catch { }
            // 先停服务，避免 exe / node 被占用导致覆盖失败
            StopServer();
            try { Hide(); } catch { }

            try
            {
                // /S 静默；/D= 必须是最后一个参数且不能加引号（即使路径含空格），
                // 显式指定原安装目录 —— 否则静默安装可能装到默认路径，桌面快捷方式就会
                // 继续指向旧目录，出现「更新了但打开的还是旧版本」。
                Process.Start(new ProcessStartInfo(file)
                {
                    UseShellExecute = true,
                    Arguments = "/S /D=" + Application.StartupPath
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动安装程序失败，请手动运行：\r\n" + file + "\r\n\r\n" + ex.Message,
                    "DeepSeek Harness 更新", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Application.Exit();
        }

        private static void OpenReleasesPage()
        {
            try { Process.Start(new ProcessStartInfo(ReleasesPageUrl) { UseShellExecute = true }); }
            catch { }
        }

        // 取最新版信息：先读静态清单 latest.json，拿不到再回退 GitHub API。
        // 两者都用正则抽取字段，避免为此引入 JSON 依赖。
        // 关键：每一步都有超时（见 CheckTimeoutMs）—— 网络不通时 GitHub 的这两个域名
        // 常常既连不上也不报错，请求会一直挂着，界面上的「检查中…」就永远消不掉。
        private static async Task<UpdateInfo> FetchLatestReleaseAsync()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

            UpdateInfo info = null;
            try { info = await WithTimeout(FetchManifestAsync(), CheckTimeoutMs); }
            catch { info = null; }
            if (info != null) return info;

            try { info = await WithTimeout(FetchApiAsync(), CheckTimeoutMs); }
            catch { info = null; }
            return info;
        }

        // 给任意任务套一层硬超时：超时即抛出，不再无限等待。
        // 注意：被放弃的那个任务仍在后台跑完自己（HttpClient / WebClient 本身也有 Timeout 兜底），
        // 这里只保证界面不会卡在「检查中…」上。
        private static async Task<T> WithTimeout<T>(Task<T> task, int timeoutMs)
        {
            Task finished = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (finished != task) throw new TimeoutException("请求超时（" + (timeoutMs / 1000) + " 秒）");
            return await task.ConfigureAwait(false);
        }

        private static async Task<string> DownloadTextAsync(string url, string accept, int timeoutMs)
        {
            using (WebClient wc = new TimedWebClient(timeoutMs))
            {
                wc.Encoding = Encoding.UTF8;
                wc.Headers.Add("User-Agent", "DeepSeekHarness-Desktop/" + AppInfo.Version);
                if (!string.IsNullOrEmpty(accept)) wc.Headers.Add("Accept", accept);
                Task<string> download = wc.DownloadStringTaskAsync(url);
                Task finished = await Task.WhenAny(download, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (finished != download)
                {
                    try { wc.CancelAsync(); } catch { }
                    throw new TimeoutException("请求超时（" + (timeoutMs / 1000) + " 秒）");
                }
                return await download.ConfigureAwait(false);
            }
        }

        // 带超时的 WebClient：WebClient 本身没有 Timeout 属性，只能在创建 WebRequest 时注入。
        private sealed class TimedWebClient : WebClient
        {
            private readonly int timeoutMs;
            public TimedWebClient(int timeoutMs) { this.timeoutMs = timeoutMs; }
            protected override WebRequest GetWebRequest(Uri address)
            {
                WebRequest r = base.GetWebRequest(address);
                try { r.Timeout = timeoutMs; } catch { }
                return r;
            }
        }

        // 静态清单：CI 随 Release 上传的 latest.json，无 API 限流
        private static async Task<UpdateInfo> FetchManifestAsync()
        {
            string json = await DownloadTextAsync(UpdateManifestUrl, "application/json", CheckTimeoutMs);
            if (string.IsNullOrEmpty(json)) return null;

            Match v = Regex.Match(json, "\"version\"\\s*:\\s*\"([^\"]+)\"");
            if (!v.Success) return null;

            UpdateInfo info = new UpdateInfo();
            info.Version = v.Groups[1].Value.TrimStart('v', 'V');

            Match min = Regex.Match(json, "\"minimumVersion\"\\s*:\\s*\"([^\"]*)\"");
            info.MinimumVersion = min.Success ? min.Groups[1].Value.TrimStart('v', 'V') : "";

            Match setup = Regex.Match(json, "\"installer\"\\s*:\\s*\\{[^{}]*\"url\"\\s*:\\s*\"([^\"]+)\"");
            if (!setup.Success) setup = Regex.Match(json, "\"installerUrl\"\\s*:\\s*\"([^\"]+)\"");
            info.InstallerUrl = setup.Success ? setup.Groups[1].Value : null;

            Match zip = Regex.Match(json, "\"portable\"\\s*:\\s*\\{[^{}]*\"url\"\\s*:\\s*\"([^\"]+)\"");
            if (!zip.Success) zip = Regex.Match(json, "\"portableUrl\"\\s*:\\s*\"([^\"]+)\"");
            info.ZipUrl = zip.Success ? zip.Groups[1].Value : null;

            Match page = Regex.Match(json, "\"page\"\\s*:\\s*\"([^\"]+)\"");
            info.PageUrl = page.Success ? page.Groups[1].Value : ReleasesPageUrl;

            info.Notes = ExtractJsonString(json, "notes");
            info.Mandatory = !string.IsNullOrEmpty(info.MinimumVersion) &&
                             IsNewerVersion(info.MinimumVersion, AppInfo.Version);
            return info;
        }

        // 回退：GitHub Releases API（匿名 60 次/小时/IP，可能被限流）
        private static async Task<UpdateInfo> FetchApiAsync()
        {
            string json = await DownloadTextAsync(UpdateApiUrl, "application/vnd.github+json", CheckTimeoutMs);
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

            info.Notes = ExtractJsonString(json, "body");
            info.Mandatory = false; // API 不带 minimumVersion，无法判定强制升级
            return info;
        }

        // 从 JSON 文本里取出指定字符串字段的值（含转义还原），取不到返回空串
        private static string ExtractJsonString(string json, string field)
        {
            Match m = Regex.Match(json, "\"" + field + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return "";
            try { return Regex.Unescape(m.Groups[1].Value); }
            catch { return m.Groups[1].Value; }
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
            // 最低可用版本：低于它的客户端被要求必须升级（最低版本本身不算必须升级）
            public string MinimumVersion = "";
            public bool Mandatory;
        }
    }

    // 无边框窗口的边缘/角落缩放抓手：透明控件，按下时向所属窗体投递系统缩放命令
    internal sealed class ResizeGrip : Control
    {
        private readonly Form owner;
        private readonly int hitTest;
        private const int WM_NCLBUTTONDOWN = 0x00A1;

        public ResizeGrip(Form owner, int hitTest)
        {
            this.owner = owner;
            this.hitTest = hitTest;
            // 同上：先 SetStyle 再设 Transparent，顺序颠倒会抛 ArgumentException
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = CursorFor(hitTest);
        }

        private static Cursor CursorFor(int hit)
        {
            if (hit == 12 || hit == 15) return Cursors.SizeNS;          // HTTOP / HTBOTTOM
            if (hit == 10 || hit == 11) return Cursors.SizeWE;          // HTLEFT / HTRIGHT
            if (hit == 13 || hit == 16) return Cursors.SizeNWSE;        // HTTOPLEFT / HTBOTTOMRIGHT
            return Cursors.SizeNESW;                                    // HTTOPRIGHT / HTBOTTOMLEFT
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(owner.Handle, WM_NCLBUTTONDOWN, (IntPtr)hitTest, IntPtr.Zero);
            }
            base.OnMouseDown(e);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }

    // 自绘标题栏按钮（Windows 11 观感）：常态无底色，悬停浅灰底，关闭键悬停红底白叉。
    // 图标全部用 GDI 画线，不依赖 Segoe MDL2 Assets 等可能缺失的符号字体。
    internal sealed class CaptionButton : Control
    {
        internal enum Kind { Minimize, Maximize, Close }

        private readonly Kind kind;
        private readonly Form owner;
        private bool hover;
        private bool pressed;

        public CaptionButton(Form owner, Kind k)
        {
            this.owner = owner;
            this.kind = k;
            // 注意：不要在这里用 Transparent 背景色 —— 必须先 SetStyle 开启
            // SupportsTransparentBackColor 才能赋值，顺序颠倒会抛 ArgumentException
            // （v0.7.0 启动即崩溃的根因）。这里直接用标题栏实色，天然规避。
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = UI.Bar;
            Cursor = Cursors.Default; // 与系统标题按钮一致，不用手型光标
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e)
        { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        { if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        { pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            bool isClose = kind == Kind.Close;
            Color back = UI.Bar;
            if (hover) back = isClose ? UI.CloseHover : (pressed ? UI.Pressed : UI.Hover);
            if (hover)
            {
                using (SolidBrush b = new SolidBrush(back))
                    g.FillRectangle(b, 0, 0, Width, Height);
            }

            Color ink = (isClose && hover) ? Color.White : UI.Label;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = Width / 2f, cy = Height / 2f + 0.5f;
            using (Pen p = new Pen(ink, 1.2f))
            {
                if (kind == Kind.Minimize)
                {
                    g.DrawLine(p, cx - 5, cy + 3.5f, cx + 5, cy + 3.5f);
                }
                else if (kind == Kind.Maximize)
                {
                    bool maxed = owner != null && owner.WindowState == FormWindowState.Maximized;
                    if (maxed)
                    {
                        // 还原图标：两个错开的小窗，后画的那个先用底色盖住重叠部分
                        g.DrawRectangle(p, cx - 2.5f, cy - 5.5f, 7, 7);
                        using (SolidBrush b = new SolidBrush(back))
                            g.FillRectangle(b, cx - 5.5f, cy - 2.5f, 7, 7);
                        g.DrawRectangle(p, cx - 5.5f, cy - 2.5f, 7, 7);
                    }
                    else
                    {
                        g.DrawRectangle(p, cx - 5f, cy - 5f, 10, 10);
                    }
                }
                else
                {
                    g.DrawLine(p, cx - 4.5f, cy - 4.5f, cx + 4.5f, cy + 4.5f);
                    g.DrawLine(p, cx + 4.5f, cy - 4.5f, cx - 4.5f, cy + 4.5f);
                }
            }
        }
    }

    // 双缓冲面板：启动页与占位卡片每 500ms 刷新文案，普通 Panel 会闪，这个不会
    internal sealed class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            UpdateStyles();
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
