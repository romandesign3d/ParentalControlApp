using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ParentalControlApp
{
    public partial class KioskForm : Form
    {
        private WebView2 webView;
        private bool allowClose = false;
        private string currentMode;

        private IntPtr _hookID = IntPtr.Zero;
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
        
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private LowLevelKeyboardProc _proc;

        private bool ctrlR_pressed = false;
        private string unlockSequence = "parent";
        private int sequenceIndex = 0;
        
        public KioskForm(string mode)
        {
            InitializeComponent();
            this.currentMode = mode;
            // Инициализация будет вызвана из события Shown, когда форма будет полностью готова.
            this.Shown += KioskForm_Shown;
        }

        private async void KioskForm_Shown(object? sender, EventArgs e)
        {
            // Этот метод теперь запускается, когда форма ПОЛНОСТЬЮ отрисована.
            await InitializeWebView();
        }
        
        private async Task InitializeWebView()
        {
            Logger.Log("=== InitializeWebView START ===");

            try
            {
                var hostHwnd = this.Handle;
                Logger.Log($"Form handle: 0x{hostHwnd.ToInt64():X}  IsHandleCreated={this.IsHandleCreated}");

                // Проверяем версию рантайма
                var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                Logger.Log($"WebView2 Runtime version: {version}");

                // Путь данных — используем %LOCALAPPDATA% (гарантированно доступно на запись)
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ParentalControlApp",
                    "WebView2_UserData");
                Directory.CreateDirectory(userDataFolder);
                Logger.Log($"UserDataFolder: {userDataFolder}");

                // Настраиваем свойства
                var creationProps = new CoreWebView2CreationProperties
                {
                    UserDataFolder = userDataFolder
                };

                // Попытка использовать локальный Fixed-Version Runtime, если он лежит в папке "FixedRuntime"
                try
                {
                    string fixedRuntimeBase = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FixedRuntime");
                    if (Environment.Is64BitProcess)
                        fixedRuntimeBase = Path.Combine(fixedRuntimeBase, "x64");
                    else
                        fixedRuntimeBase = Path.Combine(fixedRuntimeBase, "x86");

                    if (Directory.Exists(fixedRuntimeBase))
                    {
                        creationProps.BrowserExecutableFolder = fixedRuntimeBase;
                        Logger.Log($"Using fixed WebView2 runtime at: {fixedRuntimeBase}");
                    }
                    else
                    {
                        Logger.Log("FixedRuntime folder not found – falling back to system WebView2 runtime.");
                    }
                }
                catch(Exception ex)
                {
                    Logger.Log($"Failed to set FixedRuntime path: {ex.Message}. Using system runtime.");
                }

                webView.CreationProperties = creationProps;

                if (!webView.IsHandleCreated)
                {
                    webView.CreateControl();
                    Logger.Log("webView handle was not created – CreateControl() called.");
                }
                Logger.Log($"webView handle: 0x{webView.Handle.ToInt64():X}");

                // Основная попытка
                await webView.EnsureCoreWebView2Async();
                Logger.Log("EnsureCoreWebView2Async completed – awaiting InitCompleted event…");
            }
            catch (Exception ex)
            {
                Logger.Log($"EnsureCoreWebView2Async threw: {ex}");
                // Повтор через 500 мс
                await Task.Delay(500);
                try
                {
                    Logger.Log("Retrying EnsureCoreWebView2Async after delay…");
                    await webView.EnsureCoreWebView2Async();
                }
                catch (Exception retryEx)
                {
                    Logger.Log($"Retry failed: {retryEx}");
                    MessageBox.Show($"WebView2 init failed: {retryEx.Message}", "Critical Error",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                    this.Close();
                }
            }
            Logger.Log("=== InitializeWebView END ===");
        }
        
        private void WebView_CoreWebView2InitializationCompleted(
                object? sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                Logger.Log("InitCompleted → SUCCESS");
                webView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
                LoadHomePage();
            }
            else
            {
                Logger.Log($"InitCompleted → FAILURE: {e.InitializationException}");
                MessageBox.Show($"WebView2 error: {e.InitializationException?.Message}",
                                "Critical Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.Close();
            }
        }
        
        private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true; // Блокируем открытие любых новых окон (включая "Поделиться")
        }
        
        private void LoadHomePage()
        {
            try
            {
                string linksFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "links.txt");
                var linksHtml = new StringBuilder();

                if (System.IO.File.Exists(linksFilePath))
                {
                    var lines = System.IO.File.ReadAllLines(linksFilePath);
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        {
                            var videoId = GetVideoId(line.Trim());
                            if (!string.IsNullOrEmpty(videoId))
                            {
                                var thumbnailUrl = $"https://img.youtube.com/vi/{videoId}/0.jpg";
                                linksHtml.Append($@"
                                    <div class='video-item' onclick=""showVideo('{videoId}')"">
                                        <img src='{thumbnailUrl}' />
                                    </div>");
                            }
                        }
                    }
                }

                string htmlContent = $@"
                <html>
                    <head>
                        <title>Выбери сказку</title>
                        <meta charset='UTF-8'>
                        <style>
                            body {{ background-color: #222; margin: 0; padding: 20px; font-family: sans-serif; display: flex; flex-direction: column; align-items: center; }}
                            .video-item {{ cursor: pointer; margin-bottom: 20px; width: 33.33%; max-width: 480px; border: 2px solid #444; border-radius: 10px; overflow: hidden; transition: transform 0.2s; }}
                            .video-item:hover {{ transform: scale(1.05); border-color: #777; }}
                            .video-item img {{ width: 100%; display: block; }}
                            h1 {{ color: #eee; }}
                        </style>
                        <script>
                            function showVideo(videoId) {{
                                window.chrome.webview.postMessage(videoId);
                            }}
                        </script>
                    </head>
                    <body>
                        <h1>Выбери сказку</h1>
                        {linksHtml}
                    </body>
                </html>";
                webView.NavigateToString(htmlContent);
            }
            catch(Exception ex)
            {
                Logger.Log($"Error building home page: {ex.ToString()}");
            }
        }

        private void ShowVideoPage(string videoId)
        {
            string htmlContent = $@"
            <html>
                <head>
                    <title>Просмотр</title>
                    <meta charset='UTF-8'>
                    <style>
                        body {{ margin: 0; background-color: #000; display: flex; flex-direction: column; justify-content: center; align-items: center; height: 100vh; }}
                        iframe {{ width: 95vw; height: 95vh; border: none; }}
                        .back-button {{ position: absolute; top: 10px; left: 10px; padding: 10px 20px; background-color: #333; color: white; border: none; border-radius: 5px; cursor: pointer; font-size: 16px; }}
                    </style>
                </head>
                <body>
                    <button class='back-button' onclick='window.history.back()'>Назад к сказкам</button>
                    <iframe src='https://www.youtube.com/embed/{videoId}?autoplay=1&rel=0&iv_load_policy=3&showinfo=0&controls=1'></iframe>
                </body>
            </html>";
            webView.NavigateToString(htmlContent);
        }

        private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string videoId = e.TryGetWebMessageAsString();
            if (!string.IsNullOrEmpty(videoId))
            {
                ShowVideoPage(videoId);
            }
        }

        private string GetVideoId(string url)
        {
            var youtubeRegex = new Regex(@"(?:https?:\/\/)?(?:www\.)?(?:(?:youtube.com\/watch\?v=)|(?:youtu.be\/))([^&]{11})");
            var match = youtubeRegex.Match(url);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }
        
        public void UnlockAndClose()
        {
            Logger.Log("UnlockAndClose called.");
            allowClose = true;
            this.Close();
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
             if (nCode >= 0)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                Keys key = (Keys)vkCode;

                if (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN)
                {
                    if (key == Keys.R && (Control.ModifierKeys & Keys.Control) == Keys.Control)
                    {
                        ctrlR_pressed = true;
                        sequenceIndex = 0;
                        return (IntPtr)1; 
                    }

                    if (ctrlR_pressed)
                    {
                        if (sequenceIndex < unlockSequence.Length && key.ToString().ToLower() == unlockSequence[sequenceIndex].ToString())
                        {
                            sequenceIndex++;
                            if (sequenceIndex == unlockSequence.Length)
                            {
                                ctrlR_pressed = false;
                                this.Invoke(new MethodInvoker(UnlockAndClose));
                            }
                        }
                        else
                        {
                            ctrlR_pressed = false;
                        }
                         return (IntPtr)1;
                    }

                    bool isCtrl = (Control.ModifierKeys & Keys.Control) == Keys.Control;
                    bool isAlt = (Control.ModifierKeys & Keys.Alt) == Keys.Alt;

                    if ((isAlt && key == Keys.Tab) || (isAlt && key == Keys.F4) ||
                        (isCtrl && isAlt && key == Keys.Delete) ||
                        key == Keys.LWin || key == Keys.RWin)
                    {
                        return (IntPtr)1;
                    }
                }
            }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private void KioskForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            if (!allowClose && currentMode != "debug")
            {
                e.Cancel = true;
            }
            else
            {
                if(_hookID != IntPtr.Zero) UnhookWindowsHookEx(_hookID);
            }
        }
        
        private void SetHook()
        {
            _proc = HookCallback;
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule? curModule = curProcess.MainModule)
            {
                if(curModule != null)
                {
                   _hookID = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);
                }
            }
        }

        private void InitializeComponent()
        {
            this.webView = new Microsoft.Web.WebView2.WinForms.WebView2();
            ((System.ComponentModel.ISupportInitialize)(this.webView)).BeginInit();
            this.SuspendLayout();
            // 
            // webView
            // 
            this.webView.AllowExternalDrop = false;
            this.webView.CreationProperties = null;
            this.webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(((int)(((byte)(34)))), ((int)(((byte)(34)))), ((int)(((byte)(34)))));
            this.webView.Dock = System.Windows.Forms.DockStyle.Fill;
            this.webView.Location = new System.Drawing.Point(0, 0);
            this.webView.Name = "webView";
            this.webView.Size = new System.Drawing.Size(800, 600);
            this.webView.TabIndex = 0;
            this.webView.ZoomFactor = 1D;
            this.webView.CoreWebView2InitializationCompleted += new System.EventHandler<Microsoft.Web.WebView2.Core.CoreWebView2InitializationCompletedEventArgs>(this.WebView_CoreWebView2InitializationCompleted);
            this.webView.WebMessageReceived += new System.EventHandler<Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs>(this.CoreWebView2_WebMessageReceived);
            // 
            // KioskForm
            // 
            this.ClientSize = new System.Drawing.Size(800, 600);
            this.Controls.Add(this.webView);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
            this.Name = "KioskForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Kiosk Mode";
            this.TopMost = true;
            this.WindowState = System.Windows.Forms.FormWindowState.Maximized;
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.KioskForm_FormClosing);
            ((System.ComponentModel.ISupportInitialize)(this.webView)).EndInit();
            this.ResumeLayout(false);

            if (this.currentMode != "debug")
            {
                try
                {
                    SetHook();
                }
                catch(Exception ex)
                {
                    Logger.Log($"Failed to set keyboard hook: {ex.Message}. The app will continue without keyboard blocking.");
                }
            }
        }
    }
}
