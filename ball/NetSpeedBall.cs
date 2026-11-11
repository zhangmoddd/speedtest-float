using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using FormsTimer = System.Windows.Forms.Timer;
using ThreadingTimer = System.Threading.Timer;

namespace NetSpeedBall
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // 截图自检用的两个开关必须在单实例锁之前处理：球开着的时候也要能渲染，
            // 否则第二次启动会被当成"重复启动"，直接把已有的球亮出来就退出了。
            if (args != null && args.Length >= 2)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if (string.Equals(args[0], "--render-preview", StringComparison.OrdinalIgnoreCase))
                {
                    SpeedBallForm.SavePreview(args[1]);
                    return;
                }

                if (string.Equals(args[0], "--render-details", StringComparison.OrdinalIgnoreCase))
                {
                    SpeedDetailsForm.SavePreview(args[1]);
                    return;
                }
            }

            bool createdNew;
            using (Mutex singleInstance = new Mutex(true, "NetSpeedBall.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    // 已经有一个实例在跑了：让那个把悬浮球亮出来，别让用户以为双击没反应。
                    uint message = NativeMethods.RegisterWindowMessage("NetSpeedBall.ShowBall");
                    if (message != 0)
                    {
                        // 广播（HWND_BROADCAST）到不了已经被隐藏起来的悬浮球窗口（实测如此），
                        // 所以先按标题把它找出来直接投递；找不到再退回广播。
                        IntPtr ball = NativeMethods.FindWindow(null, SpeedBallForm.WindowTitle);
                        if (ball != IntPtr.Zero)
                        {
                            NativeMethods.PostMessage(ball, message, IntPtr.Zero, IntPtr.Zero);
                        }
                        else
                        {
                            NativeMethods.PostMessage(new IntPtr(NativeMethods.HwndBroadcast), message,
                                IntPtr.Zero, IntPtr.Zero);
                        }
                    }

                    return;
                }

                Application.Run(new SpeedBallApplication());
            }
        }
    }

    internal sealed class SpeedBallApplication : ApplicationContext
    {
        // 必须与合并版设置的启动项名称一致，否则右键菜单会误报“未开启”，
        // 再点一次会写出第二条重复的启动项。
        private const string AppName = "测速浮光";
        private readonly NotifyIcon trayIcon;
        private readonly SpeedBallForm ballForm;
        private readonly ThreadingTimer sampleTimer;
        private readonly ContextMenuStrip menu;
        private readonly ToolStripMenuItem showBallItem;
        private readonly ToolStripMenuItem alwaysOnTopItem;
        private readonly ToolStripMenuItem startupItem;
        private readonly ToolStripMenuItem opacityItem;
        private readonly ToolStripMenuItem megabytesItem;
        private readonly ToolStripMenuItem megabitsItem;
        private readonly NetworkSpeedSampler sampler = new NetworkSpeedSampler();
        private readonly TrafficStore trafficStore = new TrafficStore();
        private readonly AppTrafficMonitor appMonitor;
        private TrafficWindow trafficWindow;
        private int flushCounter;
        private volatile bool disposed;
        private int sampling;
        private bool hasNetworkState;
        private bool lastNetworkState;

        public SpeedBallApplication()
        {
            appMonitor = new AppTrafficMonitor(trafficStore);
            ballForm = new SpeedBallForm(trafficStore);
            ballForm.Show();

            showBallItem = new ToolStripMenuItem("\u663e\u793a\u60ac\u6d6e\u7403", null, OnToggleBall)
            {
                Checked = true,
                CheckOnClick = true
            };
            alwaysOnTopItem = new ToolStripMenuItem("\u7a97\u53e3\u7f6e\u9876", null, OnToggleTopMost)
            {
                Checked = true,
                CheckOnClick = true
            };
            startupItem = new ToolStripMenuItem("\u5f00\u673a\u81ea\u542f\u52a8", null, OnToggleStartup)
            {
                Checked = StartupManager.IsEnabled(AppName),
                CheckOnClick = true
            };
            opacityItem = new ToolStripMenuItem("\u534a\u900f\u660e\u6a21\u5f0f", null, OnToggleOpacity)
            {
                Checked = false,
                CheckOnClick = true
            };
            bool useMegabits = AppSettings.LoadUseMegabits();
            megabytesItem = new ToolStripMenuItem("MB/s", null, OnUseMegabytes) { Checked = !useMegabits };
            megabitsItem = new ToolStripMenuItem("Mbps", null, OnUseMegabits) { Checked = useMegabits };
            ballForm.UseMegabits = useMegabits;

            menu = new ContextMenuStrip();
            // 合并进「浮光」之后的第一入口，放最上面，右键一眼就能看到。
            menu.Items.Add(new ToolStripMenuItem("\u6253\u5f00\u6d6e\u5149\u9762\u677f", null, OnOpenFuguang));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(showBallItem);
            menu.Items.Add(alwaysOnTopItem);
            menu.Items.Add(startupItem);
            menu.Items.Add(opacityItem);
            ToolStripMenuItem unitMenu = new ToolStripMenuItem("\u901f\u5ea6\u5355\u4f4d");
            unitMenu.DropDownItems.Add(megabytesItem);
            unitMenu.DropDownItems.Add(megabitsItem);
            menu.Items.Add(unitMenu);
            menu.Items.Add(new ToolStripMenuItem("\u6253\u5f00\u7f51\u7edc\u8be6\u60c5", null, OnShowDetails));
            menu.Items.Add(new ToolStripMenuItem("\u6d41\u91cf\u7edf\u8ba1", null, OnShowTrafficWindow));
            menu.Items.Add(new ToolStripMenuItem("\u6253\u5f00\u4efb\u52a1\u7ba1\u7406\u5668", null, OnOpenTaskManager));
            menu.Items.Add(new ToolStripMenuItem("\u91cd\u7f6e\u4eca\u65e5\u6d41\u91cf", null, OnResetTraffic));
            menu.Opening += OnMenuOpening;
            if (!AppTrafficMonitor.CanCollectAppTraffic)
            {
                // 逐连接的字节统计只对管理员开放，普通权限下这里给一条现成的通路。
                menu.Items.Add(new ToolStripMenuItem("\u4ee5\u7ba1\u7406\u5458\u8eab\u4efd\u91cd\u542f", null, OnRestartElevated));
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("\u9000\u51fa", null, OnExit));

            // 每次弹出菜单都重新读一遍真实状态：Windows 的"启动应用"列表可能把本程序单独
            // 关掉，那样界面会显示成已勾选、开机却根本不启动。
            menu.Opening += delegate
            {
                try
                {
                    startupItem.Checked = StartupManager.IsEnabled(AppName);
                }
                catch
                {
                }
            };

            Icon icon = IconFactory.CreateIcon(64);
            trayIcon = new NotifyIcon
            {
                Text = "\u7f51\u7edc\u6d4b\u901f\u7403",
                Icon = icon,
                ContextMenuStrip = menu,
                Visible = true
            };
            trayIcon.DoubleClick += delegate { ToggleBallVisible(true); };
            ballForm.ContextMenuStrip = menu;
            ballForm.SetIcon(icon);

            sampleTimer = new ThreadingTimer(OnSampleTimer, null, 0, 1000);
        }

        private void OnSampleTimer(object state)
        {
            if (disposed || System.Threading.Interlocked.Exchange(ref sampling, 1) == 1)
            {
                return;
            }

            try
            {
                SpeedSample sample = sampler.Next();
                // NIC-level deltas feed the persistent traffic history (all protocols).
                trafficStore.RecordNic(DateTime.Today, sample.DownloadBytes, sample.UploadBytes);
                try
                {
                    appMonitor.Sample();
                }
                catch
                {
                    // Per-app attribution is best-effort; keep sampling even if the
                    // TCP tables briefly fail.
                }

                if (++flushCounter >= 60)
                {
                    flushCounter = 0;
                    trafficStore.Flush();
                }

                PostSample(sample);
            }
            catch
            {
                // Keep the animation alive even if Windows briefly fails to read an adapter.
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref sampling, 0);
            }
        }

        private void PostSample(SpeedSample sample)
        {
            if (disposed || ballForm.IsDisposed)
            {
                return;
            }

            try
            {
                ballForm.BeginInvoke(new Action(delegate
                {
                    if (disposed || ballForm.IsDisposed)
                    {
                        return;
                    }

                    ballForm.UpdateSpeed(sample);
                    trayIcon.Text = BuildTrayText(sample);
                    UpdateNetworkNotification(sample.HasAnyAdapter);
                }));
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void UpdateNetworkNotification(bool connected)
        {
            if (!hasNetworkState)
            {
                hasNetworkState = true;
                lastNetworkState = connected;
                return;
            }

            if (connected == lastNetworkState)
            {
                return;
            }

            lastNetworkState = connected;
            trayIcon.BalloonTipTitle = "\u7f51\u7edc\u6d4b\u901f\u7403";
            trayIcon.BalloonTipText = connected
                ? "\u7f51\u7edc\u5df2\u6062\u590d\u8fde\u63a5"
                : "\u7f51\u7edc\u8fde\u63a5\u5df2\u65ad\u5f00";
            trayIcon.BalloonTipIcon = connected ? ToolTipIcon.Info : ToolTipIcon.Warning;
            trayIcon.ShowBalloonTip(3000);
        }

        private static string BuildTrayText(SpeedSample sample)
        {
            string text = "\u7f51\u7edc\u6d4b\u901f\u7403\r\n" +
                          "\u4e0b\u8f7d " + SpeedFormatter.FormatMegabytes(sample.DownloadBytesPerSecond) + "  " +
                          "\u4e0a\u4f20 " + SpeedFormatter.FormatMegabytes(sample.UploadBytesPerSecond);
            return text.Length > 63 ? text.Substring(0, 63) : text;
        }

        private void OnToggleBall(object sender, EventArgs e)
        {
            ToggleBallVisible(showBallItem.Checked);
        }

        private void OnMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 球被 Alt+F4 藏起来时只是隐藏、没有销毁，这里把勾选状态同步成真实可见性，
            // 否则用户会以为"点了没反应"。
            showBallItem.Checked = ballForm.Visible;
        }

        private void ToggleBallVisible(bool visible)
        {
            showBallItem.Checked = visible;
            if (visible)
            {
                ballForm.EnsureVisible();
                ballForm.Show();
                ballForm.BringToFront();
            }
            else
            {
                // 详情窗口是挂在球上的附属窗口：球一收起来，它就再没有"主人"了。
                // 不收的话会留下一个关不掉的浮窗（菜单项在球上，球已经没了）。
                ballForm.HideDetails();
                ballForm.Hide();
            }
        }

        private void OnToggleTopMost(object sender, EventArgs e)
        {
            ballForm.SetTopMost(alwaysOnTopItem.Checked);
            if (alwaysOnTopItem.Checked)
            {
                ballForm.BringToFront();
            }
        }

        private void OnToggleStartup(object sender, EventArgs e)
        {
            try
            {
                StartupManager.SetEnabled(AppName, startupItem.Checked);
            }
            catch (Exception ex)
            {
                startupItem.Checked = StartupManager.IsEnabled(AppName);
                MessageBox.Show("\u8bbe\u7f6e\u5f00\u673a\u81ea\u542f\u52a8\u5931\u8d25\uff1a\r\n" + ex.Message, "\u7f51\u7edc\u6d4b\u901f\u7403",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnToggleOpacity(object sender, EventArgs e)
        {
            ballForm.Opacity = opacityItem.Checked ? 0.82 : 0.96;
        }

        private void OnUseMegabytes(object sender, EventArgs e)
        {
            megabytesItem.Checked = true;
            megabitsItem.Checked = false;
            ballForm.UseMegabits = false;
            AppSettings.SaveUseMegabits(false);
        }

        private void OnUseMegabits(object sender, EventArgs e)
        {
            megabytesItem.Checked = false;
            megabitsItem.Checked = true;
            ballForm.UseMegabits = true;
            AppSettings.SaveUseMegabits(true);
        }

        private void OnShowDetails(object sender, EventArgs e)
        {
            ToggleBallVisible(true);
            ballForm.ShowDetails();
        }

        /// <summary>
        /// 打开「浮光」面板。
        ///
        /// 合并方式：球是本程序（原版一个字节没改，所有功能都在），
        /// 浮光是另一个进程，只提供面板。两者通过命名事件通信 ——
        /// 浮光已经在后台时，Set 一下事件让它把面板显示出来，不再起第二个进程。
        ///
        /// 事件名必须与 Rust 侧 `panel_signal.rs` 的 EVENT_NAME 一字不差。
        /// </summary>
        private void OnOpenFuguang(object sender, EventArgs e)
        {
            try
            {
                try
                {
                    using (EventWaitHandle signal = EventWaitHandle.OpenExisting(@"Local\FuguangShowPanel"))
                    {
                        signal.Set();
                        return;
                    }
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    // 没有实例在跑，往下走正常启动流程
                }

                string exe = LocateFuguangPanel();
                if (exe == null)
                {
                    MessageBox.Show(
                        "找不到「浮光面板.exe」。\r\n请把它和本程序放在同一个文件夹里。",
                        "测速浮光",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                ProcessStartInfo startInfo = new ProcessStartInfo(exe, "--panel-only");
                startInfo.UseShellExecute = true;
                startInfo.WorkingDirectory = Path.GetDirectoryName(exe);
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开浮光面板失败：" + ex.Message, "测速浮光");
            }
        }

        /// <summary>
        /// 找浮光面板程序：优先同目录，其次上一级（开发时两个工程是平级的）。
        /// </summary>
        private static string LocateFuguangPanel()
        {
            string[] names = { "浮光面板.exe", "fuguang.exe" };
            string[] dirs =
            {
                AppDomain.CurrentDomain.BaseDirectory,
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..")),
            };

            foreach (string dir in dirs)
            {
                foreach (string name in names)
                {
                    try
                    {
                        string candidate = Path.Combine(dir, name);
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return null;
        }

        private void OnShowTrafficWindow(object sender, EventArgs e)
        {
            if (trafficWindow == null || trafficWindow.IsDisposed)
            {
                trafficWindow = new TrafficWindow(trafficStore);
            }

            if (!trafficWindow.Visible)
            {
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                trafficWindow.Location = new Point(
                    area.Left + Math.Max(0, (area.Width - trafficWindow.Width) / 2),
                    area.Top + Math.Max(0, (area.Height - trafficWindow.Height) / 2));
                trafficWindow.Show();
            }

            trafficWindow.Activate();
        }

        private void OnOpenTaskManager(object sender, EventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "taskmgr.exe",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("\u6253\u5f00\u4efb\u52a1\u7ba1\u7406\u5668\u5931\u8d25\uff1a\r\n" + ex.Message,
                    "\u7f51\u7edc\u6d4b\u901f\u7403", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnResetTraffic(object sender, EventArgs e)
        {
            trafficStore.ResetToday();
            ballForm.ResetTodayTraffic();
            trayIcon.BalloonTipTitle = "\u7f51\u7edc\u6d4b\u901f\u7403";
            trayIcon.BalloonTipText = "\u4eca\u65e5\u6d41\u91cf\u7edf\u8ba1\u5df2\u91cd\u7f6e";
            trayIcon.BalloonTipIcon = ToolTipIcon.Info;
            trayIcon.ShowBalloonTip(2000);
        }

        private void OnRestartElevated(object sender, EventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("\u63d0\u6743\u5931\u8d25\uff1a\r\n" + ex.Message, "\u7f51\u7edc\u6d4b\u901f\u7403",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ExitThread();
        }

        private void OnExit(object sender, EventArgs e)
        {
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposed)
            {
                base.Dispose(disposing);
                return;
            }

            disposed = true;
            if (disposing && sampleTimer != null)
            {
                sampleTimer.Dispose();
            }

            // Persist whatever is still pending before the UI goes away.
            if (appMonitor != null)
            {
                appMonitor.Dispose();
            }

            if (trafficStore != null)
            {
                trafficStore.Close();
            }

            if (disposing && trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }

            if (disposing && menu != null)
            {
                menu.Dispose();
            }

            if (disposing && ballForm != null)
            {
                ballForm.CloseForExit();
                ballForm.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    internal sealed class SpeedBallForm : Form
    {
        private const int BallSize = 122;
        // 第二个实例靠这个标题找到已经隐藏起来的球，所以标题必须保持稳定。
        public const string WindowTitle = "\u7f51\u7edc\u6d4b\u901f\u7403";
        // 第二个实例启动时会广播这条消息，界面上收到就把悬浮球叫出来。
        private static readonly int ShowBallMessageId =
            (int)NativeMethods.RegisterWindowMessage("NetSpeedBall.ShowBall");
        private SpeedSample currentSample;

        /// <summary>
        /// 实际画在球上的速率（字节/秒），每帧朝 currentSample 逼近。
        ///
        /// 采样是一秒一拍，直接画就会「1 秒跳一下」；这里按帧插值，
        /// 数字就和火箭、光环一样是连续变化的。真正的测量值仍是 currentSample。
        /// </summary>
        private double shownDownload;
        private double shownUpload;
        /// <summary>带死区的目标：采样在 ±25% 内晃动时数字不动。</summary>
        private readonly HeldValue heldDownload = new HeldValue(0.25, 2048.0);
        private readonly HeldValue heldUpload = new HeldValue(0.25, 2048.0);
        private bool shownPrimed;
        private bool useMegabits;
        private Point dragStart;
        private bool dragMoved;
        private bool dragging;
        private readonly FormsTimer animationTimer;
        private readonly LaunchPadForm launchPadForm;
        private readonly SpeedDetailsForm detailsForm;
        private Font labelFont;
        private Font speedFont;
        private Font unitFont;
        private Font statFont;
        private Font statArrowFont;
        private Icon appIcon;
        private bool overLaunchPad;
        private bool launchAnimating;
        private bool launchVisualMode;
        private float rocketAngle = -58.0f;
        private float rocketOrbitSpeed = 34.0f;

        /// <summary>
        /// 空转底座转速（度/秒）。0 流量时也以这个速度慢慢转，约 26 秒一圈。
        /// 原版没有这个（没流量完全静止），是合并后按需求加的。
        /// </summary>
        private const float IdleOrbitSpeed = 14.0f;

        /// <summary>
        /// 空转时的尾焰强度（0~1）。
        ///
        /// 转速和尾焰必须成对：球在转却不喷火，看起来就像动画卡住了。
        /// 这个值刻意很小 —— 只够画一簇小火苗，跟真正下载时那种长尾焰区分得开，
        /// 所以「火很大」依然能反推出「确实在下东西」。
        /// </summary>
        private const float IdleFlameStrength = 0.10f;
        private double launchElapsed;
        private Point dragOriginLocation;
        private Point launchStartLocation;
        private Point launchTargetLocation;
        private Point launchReturnLocation;
        private double launchReturnOpacity;
        private readonly TrafficStore trafficStore;
        private DockEdge dockEdge;
        private bool docked;
        private bool closingForExit;
        private Rectangle dockArea;
        private DateTime lastAnimationFrame = DateTime.UtcNow;
        private static readonly Color LaunchTransparentColor = Color.FromArgb(240, 248, 255);

        private enum DockEdge
        {
            None,
            Left,
            Right,
            Top,
            Bottom
        }

        public SpeedBallForm() : this(null)
        {
        }

        public SpeedBallForm(TrafficStore store)
        {
            trafficStore = store;
            Text = WindowTitle;
            Size = new Size(BallSize, BallSize);
            MinimumSize = Size;
            MaximumSize = Size;
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.White;
            TransparencyKey = Color.Empty;
            Opacity = 0.96;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

            labelFont = new Font("Segoe UI", 6.8f, FontStyle.Bold, GraphicsUnit.Point);
            speedFont = new Font("Segoe UI", 25.0f, FontStyle.Bold, GraphicsUnit.Point);
            unitFont = new Font("Segoe UI", 8.8f, FontStyle.Regular, GraphicsUnit.Point);
            statFont = new Font("Segoe UI", 10.5f, FontStyle.Bold, GraphicsUnit.Point);
            statArrowFont = new Font("Segoe UI", 12.5f, FontStyle.Bold, GraphicsUnit.Point);

            animationTimer = new FormsTimer { Interval = 33 };
            animationTimer.Tick += OnAnimationTick;
            animationTimer.Start();

            launchPadForm = new LaunchPadForm();
            detailsForm = new SpeedDetailsForm(trafficStore);

            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Point savedLocation;
            Location = AppSettings.TryLoadLocation(out savedLocation)
                ? savedLocation
                : new Point(area.Right - Width - 28, area.Bottom - Height - 42);
            UpdateWindowRegion();
            KeepInsideWorkingArea();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        public void SetTopMost(bool value)
        {
            TopMost = value;
            detailsForm.TopMost = value;
        }

        public void EnsureVisible()
        {
            if (docked)
            {
                ExpandFromDock();
            }
            else
            {
                KeepInsideWorkingArea();
            }
            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }
        }

        public bool UseMegabits
        {
            get { return useMegabits; }
            set { useMegabits = value; Invalidate(); }
        }

        public void SetIcon(Icon icon)
        {
            appIcon = icon;
            Icon = icon;
        }

        public void UpdateSpeed(SpeedSample sample)
        {
            currentSample = sample;
            heldDownload.Update(sample.DownloadBytesPerSecond);
            heldUpload.Update(sample.UploadBytesPerSecond);

            if (!shownPrimed)
            {
                // 第一拍没有历史值，直接落位，否则会从 0 慢慢爬上来
                shownDownload = heldDownload.Target;
                shownUpload = heldUpload.Target;
                shownPrimed = true;
            }

            detailsForm.AddSample(sample);
            Invalidate();
        }

        public static void SavePreview(string path)
        {
            using (Bitmap bitmap = new Bitmap(BallSize, BallSize))
            using (Graphics g = Graphics.FromImage(bitmap))
            using (SpeedBallForm preview = new SpeedBallForm())
            {
                g.Clear(Color.Transparent);
                preview.currentSample = new SpeedSample(850.0 * 1024.0, 25.6 * 1024.0, true);
                preview.heldDownload.Snap(850.0 * 1024.0);
                preview.heldUpload.Snap(25.6 * 1024.0);
                preview.shownDownload = preview.heldDownload.Target;
                preview.shownUpload = preview.heldUpload.Target;
                preview.DrawSurface(g);
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        private void OnAnimationTick(object sender, EventArgs e)
        {
            DateTime now = DateTime.UtcNow;
            double elapsed = Math.Min(0.08, Math.Max(0.0, (now - lastAnimationFrame).TotalSeconds));
            lastAnimationFrame = now;

            // 目标来自 HeldValue（带 25% 死区），所以平时它根本不换；
            // 真要换的时候用 0.5 秒平滑过去，数字就不会蹦。
            shownDownload = SmoothValue.Toward(shownDownload, heldDownload.Target, elapsed, 0.5, 256.0);
            shownUpload = SmoothValue.Toward(shownUpload, heldUpload.Target, elapsed, 0.5, 256.0);
            detailsForm.Animate(elapsed);

            float targetSpeed = GetRocketOrbitSpeed();
            float blend = Math.Min(1.0f, (float)(elapsed * 4.5));
            rocketOrbitSpeed += (targetSpeed - rocketOrbitSpeed) * blend;
            rocketAngle += (float)(rocketOrbitSpeed * elapsed);
            if (rocketAngle >= 360.0f)
            {
                rocketAngle -= 360.0f;
            }

            if (launchAnimating)
            {
                UpdateLaunchAnimation(elapsed);
            }

            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && !launchAnimating)
            {
                ExpandFromDock();
                dragging = true;
                dragMoved = false;
                dragStart = e.Location;
                dragOriginLocation = Location;
                Cursor = Cursors.Default;
                Capture = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging)
            {
                if (!docked && dockEdge == DockEdge.None && IsTouchingScreenEdge())
                {
                    SnapToScreenEdgeIfNeeded();
                }
                return;
            }

            Point screenPoint = PointToScreen(e.Location);
            if (Math.Abs(screenPoint.X - (dragOriginLocation.X + dragStart.X)) > SystemInformation.DragSize.Width / 2 ||
                Math.Abs(screenPoint.Y - (dragOriginLocation.Y + dragStart.Y)) > SystemInformation.DragSize.Height / 2)
            {
                if (!dragMoved)
                {
                    dragMoved = true;
                    Cursor = Cursors.SizeAll;
                    ShowLaunchPad();
                }
            }
            if (!dragMoved)
            {
                return;
            }
            Location = new Point(screenPoint.X - dragStart.X, screenPoint.Y - dragStart.Y);
            UpdateLaunchPadHover();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (launchAnimating)
            {
                return;
            }

            bool shouldLaunch = dragging && overLaunchPad;
            bool shouldToggleDetails = dragging && !dragMoved && !shouldLaunch;
            dragging = false;
            Capture = false;
            Cursor = Cursors.Default;
            if (shouldLaunch)
            {
                BeginLaunchAnimation();
            }
            else
            {
                HideLaunchPad();
                if (!SnapToScreenEdgeIfNeeded())
                {
                    KeepInsideWorkingArea();
                }
                AppSettings.SaveLocation(Location);
                overLaunchPad = false;
                SetLaunchVisualMode(false);
                Invalidate();
            }

            if (shouldToggleDetails)
            {
                ToggleDetails();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            ExpandFromDock();
            if (!docked && dockEdge == DockEdge.None && IsTouchingScreenEdge())
            {
                SnapToScreenEdgeIfNeeded();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (dockEdge == DockEdge.None || dragging || launchAnimating)
            {
                return;
            }

            // Defer the check so the rounded region does not flicker during pointer movement.
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (!dragging && !launchAnimating && dockEdge != DockEdge.None &&
                        !ClientRectangle.Contains(PointToClient(Cursor.Position)))
                    {
                        CollapseToDock();
                    }
                }));
            }
            catch (InvalidOperationException)
            {
            }
        }

        /// <summary>
        /// 收起详情窗口（如果开着）。
        ///
        /// 供 `ToggleBallVisible(false)` 调用：球被藏起来时必须连详情一起收，
        /// 否则屏幕上会留下一个没有主人的窗口。
        /// </summary>
        public void HideDetails()
        {
            if (detailsForm != null && detailsForm.Visible)
            {
                detailsForm.Hide();
            }
        }

        public void ShowDetails()
        {
            if (!detailsForm.Visible)
            {
                ToggleDetails();
            }
            else
            {
                detailsForm.BringToFront();
            }
        }

        public void ResetTodayTraffic()
        {
            detailsForm.ResetTodayTraffic();
        }

        private void ToggleDetails()
        {
            if (detailsForm.Visible)
            {
                detailsForm.Hide();
                return;
            }

            Rectangle area = Screen.FromControl(this).WorkingArea;
            int x = Right + 10;
            if (x + detailsForm.Width > area.Right)
            {
                x = Left - detailsForm.Width - 10;
            }

            int y = Math.Max(area.Top, Math.Min(Top - 20, area.Bottom - detailsForm.Height));
            detailsForm.Location = new Point(x, y);
            detailsForm.TopMost = TopMost;
            detailsForm.Show();
        }

        private void ExpandFromDock()
        {
            if (!docked || dockEdge == DockEdge.None)
            {
                return;
            }

            Rectangle area = GetDockArea();
            int x = Left;
            int y = Top;
            switch (dockEdge)
            {
                case DockEdge.Left:
                    x = area.Left;
                    y = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
                    break;
                case DockEdge.Right:
                    x = area.Right - Width;
                    y = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
                    break;
                case DockEdge.Top:
                    x = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
                    y = area.Top;
                    break;
                case DockEdge.Bottom:
                    x = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
                    y = area.Bottom - Height;
                    break;
            }

            docked = false;
            Location = new Point(x, y);
        }

        private void CollapseToDock()
        {
            if (dockEdge == DockEdge.None)
            {
                return;
            }

            Rectangle area = GetDockArea();
            int x = Left;
            int y = Top;
            const int peek = 18;
            switch (dockEdge)
            {
                case DockEdge.Left:
                    x = area.Left - Width + peek;
                    y = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
                    break;
                case DockEdge.Right:
                    x = area.Right - peek;
                    y = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
                    break;
                case DockEdge.Top:
                    x = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
                    y = area.Top - Height + peek;
                    break;
                case DockEdge.Bottom:
                    x = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
                    y = area.Bottom - peek;
                    break;
            }

            docked = true;
            Location = new Point(x, y);
        }

        private bool SnapToScreenEdgeIfNeeded()
        {
            Rectangle area = GetWorkingArea();
            const int threshold = 16;
            // Use the gap that remains inside the work area. When the window has
            // crossed an edge, the gap is zero rather than a large absolute value.
            int leftDistance = Math.Max(0, Left - area.Left);
            int rightDistance = Math.Max(0, area.Right - Right);
            int topDistance = Math.Max(0, Top - area.Top);
            int bottomDistance = Math.Max(0, area.Bottom - Bottom);
            int closest = Math.Min(Math.Min(leftDistance, rightDistance), Math.Min(topDistance, bottomDistance));

            if (closest > threshold)
            {
                dockEdge = DockEdge.None;
                docked = false;
                dockArea = Rectangle.Empty;
                return false;
            }

            dockArea = area;

            if (closest == leftDistance)
            {
                dockEdge = DockEdge.Left;
            }
            else if (closest == rightDistance)
            {
                dockEdge = DockEdge.Right;
            }
            else if (closest == topDistance)
            {
                dockEdge = DockEdge.Top;
            }
            else
            {
                dockEdge = DockEdge.Bottom;
            }

            CollapseToDock();
            return true;
        }

        private bool IsTouchingScreenEdge()
        {
            Rectangle area = GetWorkingArea();
            return Left <= area.Left || Right >= area.Right || Top <= area.Top || Bottom >= area.Bottom;
        }

        private Rectangle GetWorkingArea()
        {
            return Screen.FromControl(this).WorkingArea;
        }

        private Rectangle GetDockArea()
        {
            if (dockArea.Width > 0 && dockArea.Height > 0)
            {
                return dockArea;
            }

            return GetWorkingArea();
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            BringToFront();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg != 0 && m.Msg == ShowBallMessageId)
            {
                Show();
                EnsureVisible();
                BringToFront();
            }

            base.WndProc(ref m);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateWindowRegion();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            DrawSurface(e.Graphics);
        }

        private void DrawSurface(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            Rectangle rect = new Rectangle(5, 5, Width - 11, Height - 11);
            if (launchAnimating || overLaunchPad)
            {
                DrawLaunchRocketMode(g, rect);
                return;
            }

            DrawBall(g, rect);
            DrawSpeedText(g);
        }

        private void DrawBall(Graphics g, Rectangle rect)
        {
            using (GraphicsPath ballPath = new GraphicsPath())
            {
                ballPath.AddEllipse(rect);

                using (PathGradientBrush face = new PathGradientBrush(ballPath))
                {
                    face.CenterPoint = new PointF(rect.X + rect.Width * 0.48f, rect.Y + rect.Height * 0.38f);
                    face.CenterColor = Color.White;
                    face.SurroundColors = new[] { Color.FromArgb(236, 243, 250) };
                    g.FillPath(face, ballPath);
                }

                using (LinearGradientBrush sheen = new LinearGradientBrush(rect,
                           Color.FromArgb(120, 255, 255, 255),
                           Color.FromArgb(0, 255, 255, 255),
                           LinearGradientMode.Vertical))
                {
                    g.FillPath(sheen, ballPath);
                }
            }

            using (GraphicsPath glossPath = new GraphicsPath())
            {
                Rectangle gloss = new Rectangle(rect.X + 15, rect.Y + 8, rect.Width - 30, 34);
                glossPath.AddEllipse(gloss);
                using (PathGradientBrush glow = new PathGradientBrush(glossPath))
                {
                    glow.CenterColor = Color.FromArgb(152, 255, 255, 255);
                    glow.SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) };
                    g.FillPath(glow, glossPath);
                }
            }

            DrawClouds(g, rect);

            Rectangle ringRect = Rectangle.Inflate(rect, -4, -4);
            float activity = GetActivityRatio();
            using (Pen glow = new Pen(Color.FromArgb(118, 70, 174, 232), 5.0f))
            using (Pen outer = new Pen(Color.FromArgb(246, 250, 253, 255), 2.4f))
            using (Pen inner = new Pen(Color.FromArgb(205, 132, 196, 232), 1.1f))
            using (Pen progress = new Pen(currentSample.HasAnyAdapter ? Color.FromArgb(232, 62, 148, 232) : Color.FromArgb(70, 164, 183, 205), 3.2f))
            {
                progress.StartCap = LineCap.Round;
                progress.EndCap = LineCap.Round;

                g.DrawEllipse(glow, ringRect);
                g.DrawEllipse(outer, rect);
                g.DrawEllipse(inner, Rectangle.Inflate(rect, -7, -7));

                // 活跃度弧：从 12 点方向开始顺时针扫。
                //
                // 原来是从 126°（右下）开始画一条 2.3px 的弧，半径还比光环小 2px，
                // 结果看起来像光环内壁上蹭了一道深色划痕，既不像进度也不像装饰。
                // 现在改成：
                //   · 起点 -90° = 12 点，读数方向符合直觉（像仪表盘）
                //   · 半径与光环一致（Inflate -4），压在光晕上形成"光环被填满"的效果
                //   · 加粗到 3.2px，两端圆头
                g.DrawArc(progress, ringRect, -90, Math.Max(24, 58 + activity * 202));
            }

            DrawRocket(g, rect);
        }

        private void DrawClouds(Graphics g, Rectangle rect)
        {
            GraphicsState state = g.Save();
            using (GraphicsPath clipPath = new GraphicsPath())
            {
                clipPath.AddEllipse(rect);
                g.SetClip(clipPath, CombineMode.Intersect);

                using (Brush body = new SolidBrush(Color.FromArgb(246, 251, 255)))
                using (Brush shade = new SolidBrush(Color.FromArgb(221, 235, 244, 250)))
                using (Pen edge = new Pen(Color.FromArgb(142, 198, 217, 232), 1.0f))
                {
                    int y = rect.Bottom - 29;
                    g.FillEllipse(body, rect.X + 7, y + 7, 34, 28);
                    g.FillEllipse(body, rect.X + 25, y, 33, 34);
                    g.FillEllipse(body, rect.X + 48, y + 7, 34, 28);
                    g.FillEllipse(body, rect.X + 72, y + 3, 36, 32);
                    g.FillRectangle(body, rect.X + 11, y + 19, rect.Width - 22, 24);
                    g.FillEllipse(shade, rect.X + 69, y + 18, 36, 19);

                    g.DrawArc(edge, rect.X + 7, y + 7, 34, 28, 194, 126);
                    g.DrawArc(edge, rect.X + 25, y, 33, 34, 190, 128);
                    g.DrawArc(edge, rect.X + 72, y + 3, 36, 32, 210, 112);
                }
            }

            g.Restore(state);
        }

        // rocketAngle 只增不减；在屏幕坐标里，角度增加就是沿圆轨顺时针运动。
        // 不再做上半圈折返，避免回程变成逆时针。
        private float RocketDisplayAngle()
        {
            float angle = (rocketAngle - 90.0f) % 360.0f;
            return angle < 0.0f ? angle + 360.0f : angle;
        }

        private void DrawRocket(Graphics g, Rectangle rect)
        {
            float angle = RocketDisplayAngle();
            double radians = angle * Math.PI / 180.0;
            PointF center = new PointF(rect.X + rect.Width / 2.0f, rect.Y + rect.Height / 2.0f);
            float radius = rect.Width * 0.432f;
            PointF position = new PointF(
                center.X + (float)Math.Cos(radians) * radius,
                center.Y + (float)Math.Sin(radians) * radius);

            float flameStrength = GetRocketFlameStrength();
            if (flameStrength <= 0.0f && GetRocketOrbitSpeed() > 0.0f)
            {
                flameStrength = IdleFlameStrength;
            }

            float visibleFlame = flameStrength > 0.0f ? Math.Max(0.36f, flameStrength) : 0.0f;
            float pulse = 0.92f + 0.08f * (float)Math.Sin(rocketAngle * 0.24f);
            GraphicsState state = g.Save();
            g.TranslateTransform(position.X, position.Y);
            g.RotateTransform(angle + 90.0f);
            g.ScaleTransform(0.84f, 0.84f);
            DrawRocketArtwork(g, visibleFlame, pulse);
            g.Restore(state);
        }

        // 球面小火箭和发射大火箭共用这一套造型，避免两个画面像两个程序。
        private static void DrawRocketArtwork(Graphics g, float flameStrength, float flameScale)
        {
            if (flameStrength > 0.0f)
            {
                float length = (7.0f + 14.0f * flameStrength) * flameScale;
                float width = 2.2f + 1.8f * flameStrength;
                float tail = -10.5f - length;
                using (GraphicsPath outerFlame = new GraphicsPath())
                using (GraphicsPath innerFlame = new GraphicsPath())
                using (Brush orange = new SolidBrush(Color.FromArgb(245, 255, 125, 46)))
                using (Brush yellow = new SolidBrush(Color.FromArgb(255, 255, 225, 128)))
                {
                    outerFlame.AddBezier(-10.0f, -width, -14.0f, -width * 0.8f, tail + 2.0f, -1.2f, tail, 0.0f);
                    outerFlame.AddBezier(tail, 0.0f, tail + 2.0f, 1.2f, -14.0f, width * 0.8f, -10.0f, width);
                    outerFlame.CloseFigure();
                    innerFlame.AddBezier(-10.0f, -width * 0.48f, -13.0f, -1.0f, tail + length * 0.42f, -0.5f, tail + length * 0.30f, 0.0f);
                    innerFlame.AddBezier(tail + length * 0.30f, 0.0f, tail + length * 0.42f, 0.5f, -13.0f, 1.0f, -10.0f, width * 0.48f);
                    innerFlame.CloseFigure();
                    g.FillPath(orange, outerFlame);
                    g.FillPath(yellow, innerFlame);
                }
            }

            using (GraphicsPath upperFin = new GraphicsPath())
            using (GraphicsPath lowerFin = new GraphicsPath())
            using (Brush finBrush = new SolidBrush(Color.FromArgb(255, 31, 145, 211)))
            using (Pen finEdge = new Pen(Color.FromArgb(210, 20, 91, 151), 0.75f))
            {
                upperFin.AddPolygon(new[]
                {
                    new PointF(-6.8f, -3.6f), new PointF(-9.0f, -8.0f),
                    new PointF(-3.0f, -6.0f), new PointF(-0.8f, -4.2f)
                });
                lowerFin.AddPolygon(new[]
                {
                    new PointF(-6.8f, 3.6f), new PointF(-9.0f, 8.0f),
                    new PointF(-3.0f, 6.0f), new PointF(-0.8f, 4.2f)
                });
                g.FillPath(finBrush, upperFin);
                g.FillPath(finBrush, lowerFin);
                g.DrawPath(finEdge, upperFin);
                g.DrawPath(finEdge, lowerFin);
            }

            using (Brush nozzle = new SolidBrush(Color.FromArgb(255, 55, 79, 103)))
            {
                g.FillRectangle(nozzle, -11.2f, -3.1f, 3.0f, 6.2f);
            }

            using (GraphicsPath body = new GraphicsPath())
            using (LinearGradientBrush bodyBrush = new LinearGradientBrush(
                       new RectangleF(-9.0f, -6.0f, 21.0f, 12.0f),
                       Color.White, Color.FromArgb(214, 233, 242), LinearGradientMode.Vertical))
            using (Pen bodyEdge = new Pen(Color.FromArgb(220, 64, 105, 139), 0.85f))
            {
                body.AddBezier(-9.0f, -4.5f, -2.5f, -6.0f, 5.8f, -4.8f, 11.2f, 0.0f);
                body.AddBezier(11.2f, 0.0f, 5.8f, 4.8f, -2.5f, 6.0f, -9.0f, 4.5f);
                body.CloseFigure();
                g.FillPath(bodyBrush, body);
                g.DrawPath(bodyEdge, body);
            }

            using (GraphicsPath nose = new GraphicsPath())
            using (Brush noseBrush = new SolidBrush(Color.FromArgb(255, 24, 143, 211)))
            {
                nose.AddBezier(4.0f, -4.55f, 7.0f, -3.7f, 9.6f, -1.6f, 11.2f, 0.0f);
                nose.AddBezier(11.2f, 0.0f, 9.6f, 1.6f, 7.0f, 3.7f, 4.0f, 4.55f);
                nose.CloseFigure();
                g.FillPath(noseBrush, nose);
            }

            using (Brush glass = new SolidBrush(Color.FromArgb(255, 19, 55, 82)))
            using (Brush shine = new SolidBrush(Color.FromArgb(230, 157, 232, 255)))
            using (Pen windowEdge = new Pen(Color.White, 0.9f))
            {
                g.FillEllipse(glass, -0.4f, -2.65f, 5.3f, 5.3f);
                g.DrawEllipse(windowEdge, -0.4f, -2.65f, 5.3f, 5.3f);
                g.FillEllipse(shine, 0.65f, -1.65f, 1.65f, 1.35f);
            }
        }

        private void DrawLaunchRocketMode(Graphics g, Rectangle rect)
        {
            double clock = launchAnimating ? launchElapsed : rocketAngle / 14.0;
            double pulse = 0.5 + 0.5 * Math.Sin(clock * 16.0);
            float flight = launchAnimating
                ? (float)Math.Max(0.0, Math.Min(1.0, (launchElapsed - 0.36) / 0.95))
                : 0.0f;
            // 窗口只有 122px 高，大火箭加长尾焰会从底边被硬裁掉。
            // 把整枚火箭上移，并把尾焰长度封顶，保证火苗完整。
            float flameScale = launchAnimating
                ? 0.72f + (float)Math.Min(0.15, launchElapsed * 0.4) + (float)pulse * 0.05f
                : 0.50f + (float)pulse * 0.08f;
            float shakeX = launchAnimating && launchElapsed < 0.36
                ? (float)Math.Sin(launchElapsed * 82.0) * 1.5f
                : 0.0f;
            float lift = launchAnimating && launchElapsed < 0.36
                ? (float)(launchElapsed / 0.36 * 4.0)
                : 4.0f;
            PointF center = new PointF(rect.X + rect.Width / 2.0f + shakeX, rect.Y + rect.Height * 0.39f - lift);

            if (flight > 0.0f)
            {
                int trailAlpha = (int)(170 * (1.0f - flight * 0.55f));
                RectangleF trailArea = new RectangleF(center.X - 14, center.Y + 18, 28, 66);
                using (LinearGradientBrush trailBrush = new LinearGradientBrush(
                           trailArea, Color.FromArgb(trailAlpha, 255, 176, 70),
                           Color.FromArgb(0, 85, 190, 235), LinearGradientMode.Vertical))
                using (Pen trail = new Pen(trailBrush, 8.0f))
                using (Pen speed = new Pen(Color.FromArgb(trailAlpha / 2, 86, 181, 224), 1.5f))
                {
                    trail.StartCap = LineCap.Round;
                    trail.EndCap = LineCap.Round;
                    speed.StartCap = LineCap.Round;
                    speed.EndCap = LineCap.Round;
                    g.DrawLine(trail, center.X, center.Y + 22, center.X, center.Y + 72);
                    g.DrawLine(speed, center.X - 22, center.Y + 18, center.X - 22, center.Y + 48 + flight * 18);
                    g.DrawLine(speed, center.X + 22, center.Y + 28, center.X + 22, center.Y + 58 + flight * 18);
                }
            }
            else
            {
                using (Pen chargeRing = new Pen(Color.FromArgb((int)(70 + pulse * 70), 44, 163, 222), 1.4f))
                {
                    float spread = (float)pulse * 8.0f;
                    g.DrawEllipse(chargeRing, center.X - 24 - spread, center.Y + 30 - spread * 0.2f,
                        48 + spread * 2, 10 + spread * 0.4f);
                }
            }

            DrawLaunchRocketBody(g, center, 2.25f, -90.0f, true, flameScale);
        }

        private void DrawLaunchRocketBody(Graphics g, PointF center, float scale, float angle, bool flameOn, float flameScale)
        {
            GraphicsState state = g.Save();
            g.TranslateTransform(center.X, center.Y);
            g.RotateTransform(angle);
            g.ScaleTransform(scale, scale);
            DrawRocketArtwork(g, flameOn ? 1.0f : 0.0f, flameScale);
            g.Restore(state);
        }

        private void DrawSpeedText(Graphics g)
        {
            if (!currentSample.HasAnyAdapter)
            {
                DrawCenteredLine(g, "\u79bb\u7ebf", speedFont, 35, Color.FromArgb(28, 34, 48), Width - 30);
                DrawCenteredLine(g, "WAITING", unitFont, 70, Color.FromArgb(104, 118, 136), Width - 44);
                return;
            }

            RateText main = useMegabits
                ? SpeedFormatter.FormatBits(heldDownload.Target)
                : SpeedFormatter.FormatMegabytesRate(heldDownload.Target);
            string down = SpeedFormatter.FormatCompact(heldDownload.Target);
            string up = SpeedFormatter.FormatCompact(heldUpload.Target);

            // 三行文字上移 5px，给沿外圈顺时针经过下方的火箭留出空间。
            DrawCenteredLine(g, main.Value, speedFont, 28, Color.FromArgb(20, 27, 42), Width - 30);
            DrawCenteredLine(g, main.Unit, unitFont, 61, Color.FromArgb(70, 84, 102), Width - 48);
            DrawStatPair(g, "\u2191", up, "\u2193", down, 79);
        }

        private void DrawStatPair(Graphics g, string upArrow, string up, string downArrow, string down, int y)
        {
            Font valueFont = statFont;
            Font arrowFont = statArrowFont;
            bool disposeFonts = false;
            float arrowGap = 1.5f;
            float pairGap = 7.0f;
            // The ball is a circular window and its bottom edge is much narrower than
            // the form, so keep this row inside the safe chord width of the circle.
            float maxWidth = Width - 34.0f;
            float total = MeasureStatRow(g, upArrow, arrowFont, up, valueFont,
                downArrow, down, arrowGap, pairGap);
            if (total > maxWidth)
            {
                float scale = maxWidth / total;
                valueFont = new Font(statFont.FontFamily, Math.Max(8.0f, statFont.Size * scale),
                    statFont.Style, statFont.Unit);
                arrowFont = new Font(statArrowFont.FontFamily, Math.Max(9.5f, statArrowFont.Size * scale),
                    statArrowFont.Style, statArrowFont.Unit);
                disposeFonts = true;
                total = MeasureStatRow(g, upArrow, arrowFont, up, valueFont,
                    downArrow, down, arrowGap, pairGap);
            }

            float x = (Width - total) / 2.0f;
            float numY = y;
            float arrowY = y - (arrowFont.Height - valueFont.Height) / 2.0f;

            using (Brush upBrush = new SolidBrush(Color.FromArgb(255, 16, 198, 94)))
            using (Brush downBrush = new SolidBrush(Color.FromArgb(255, 35, 119, 238)))
            using (Brush light = new SolidBrush(Color.FromArgb(176, 255, 255, 255)))
            {
                float cx = x;
                DrawStatSegment(g, upArrow, arrowFont, light, ref cx, arrowY + 1);
                DrawStatSegment(g, up, valueFont, light, ref cx, numY + 1);
                cx += pairGap;
                DrawStatSegment(g, downArrow, arrowFont, light, ref cx, arrowY + 1);
                DrawStatSegment(g, down, valueFont, light, ref cx, numY + 1);

                cx = x;
                DrawStatSegment(g, upArrow, arrowFont, upBrush, ref cx, arrowY);
                DrawStatSegment(g, up, valueFont, upBrush, ref cx, numY);
                cx += pairGap;
                DrawStatSegment(g, downArrow, arrowFont, downBrush, ref cx, arrowY);
                DrawStatSegment(g, down, valueFont, downBrush, ref cx, numY);
            }

            if (disposeFonts)
            {
                valueFont.Dispose();
                arrowFont.Dispose();
            }
        }

        private static float MeasureStatRow(Graphics g, string upArrow, Font arrowFont, string up, Font valueFont,
            string downArrow, string down, float arrowGap, float pairGap)
        {
            float width = g.MeasureString(upArrow, arrowFont).Width + arrowGap +
                g.MeasureString(up, valueFont).Width + pairGap +
                g.MeasureString(downArrow, arrowFont).Width + arrowGap +
                g.MeasureString(down, valueFont).Width;
            return width;
        }

        private static void DrawStatSegment(Graphics g, string text, Font font, Brush brush, ref float x, float y)
        {
            g.DrawString(text, font, brush, x, y);
            x += g.MeasureString(text, font).Width;
        }

        private float GetActivityRatio()
        {
            if (!currentSample.HasAnyAdapter)
            {
                return 0.0f;
            }

            double bytes = Math.Max(shownDownload, shownUpload);
            double ratio = Math.Log(bytes + 1.0) / Math.Log((2.0 * 1024.0 * 1024.0) + 1.0);
            if (bytes > 0 && ratio < 0.08)
            {
                ratio = 0.08;
            }

            if (ratio > 1.0)
            {
                ratio = 1.0;
            }

            return (float)ratio;
        }

        // 低于这个下载速率的流量（后台心跳、NTP 校时、推送保活）一律视为空闲：
        // 网卡在线不等于有流量，火箭静止必须能反推出"真的没流量"。
        private const double RocketIdleThresholdBytesPerSecond = 8.0 * 1024.0;

        private float GetRocketOrbitSpeed()
        {
            // 没有底座转速：静止 = 空闲，转动幅度 = 真实下载速率。网卡在线但零流量时
            // 火箭必须完全停住，否则用户无法从动画区分"在传数据"和"只是网卡插着"。
            float strength = GetRocketFlameStrength();

            // 原版这里直接返回 strength * 156，也就是「没流量完全静止」。
            // 实测用起来会觉得球像卡死了，所以加一个很慢的空转底座：
            // 0 流量时也以 IdleOrbitSpeed 转，有流量时按真实速率叠加。
            return Math.Max(strength * 156.0f, IdleOrbitSpeed);
        }

        private float GetRocketFlameStrength()
        {
            if (!currentSample.HasAnyAdapter)
            {
                return 0.0f;
            }

            double download = shownDownload;
            if (double.IsNaN(download) || download <= 0.0)
            {
                // NaN 会穿过 Min/Max 污染 rocketAngle，让它永远卡在 NaN 上不再转动；按空闲处理。
                return 0.0f;
            }

            const double referenceSpeed = 8.0 * 1024.0 * 1024.0;
            double strength = Math.Log(1.0 + download / 8192.0) / Math.Log(1.0 + referenceSpeed / 8192.0);
            double threshold = Math.Log(1.0 + RocketIdleThresholdBytesPerSecond / 8192.0)
                               / Math.Log(1.0 + referenceSpeed / 8192.0);
            strength = (strength - threshold) / (1.0 - threshold);
            return (float)Math.Max(0.0, Math.Min(1.0, strength));
        }

        private void DrawCenteredLine(Graphics g, string text, Font font, int y, Color color, float maxWidth)
        {
            Font drawFont = font;
            bool disposeFont = false;
            SizeF size = g.MeasureString(text, drawFont);
            if (size.Width > maxWidth && maxWidth > 10)
            {
                float fittedSize = Math.Max(6.6f, font.Size * maxWidth / size.Width);
                drawFont = new Font(font.FontFamily, fittedSize, font.Style, font.Unit);
                disposeFont = true;
                size = g.MeasureString(text, drawFont);
            }

            Color shadowColor = color.R + color.G + color.B < 420
                ? Color.FromArgb(92, 255, 255, 255)
                : Color.FromArgb(72, 34, 67, 115);

            using (Brush shadow = new SolidBrush(shadowColor))
            using (Brush foreground = new SolidBrush(color))
            {
                float x = (Width - size.Width) / 2.0f;
                g.DrawString(text, drawFont, shadow, x + 1, y + 1);
                g.DrawString(text, drawFont, foreground, x, y);
            }

            if (disposeFont)
            {
                drawFont.Dispose();
            }
        }

        private void ShowLaunchPad()
        {
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int x = area.Left + (area.Width - launchPadForm.Width) / 2;
            int y = area.Top + (int)(area.Height * 0.67f) - launchPadForm.Height / 2;
            launchPadForm.Location = new Point(x, Math.Max(area.Top, Math.Min(y, area.Bottom - launchPadForm.Height)));
            launchPadForm.SetActive(false);
            if (!launchPadForm.Visible)
            {
                launchPadForm.Show();
            }

            launchPadForm.TopMost = true;
            BringToFront();
            overLaunchPad = false;
        }

        private void HideLaunchPad()
        {
            if (launchPadForm.Visible)
            {
                launchPadForm.Hide();
            }

            launchPadForm.SetActive(false);
        }

        private void UpdateLaunchPadHover()
        {
            bool active = launchPadForm.Visible && launchPadForm.AcceptsBall(GetBallCenterScreen());
            if (active == overLaunchPad)
            {
                return;
            }

            overLaunchPad = active;
            launchPadForm.SetActive(active);
            SetLaunchVisualMode(active);
            Invalidate();
        }

        private Point GetBallCenterScreen()
        {
            return new Point(Left + Width / 2, Top + Height / 2);
        }

        private void BeginLaunchAnimation()
        {
            dragging = false;
            Capture = false;
            Cursor = Cursors.Default;
            launchAnimating = true;
            launchElapsed = 0.0;
            launchStartLocation = Location;
            launchReturnLocation = dragOriginLocation;
            launchReturnOpacity = Opacity;

            Rectangle area = Screen.FromControl(this).WorkingArea;
            int targetX = launchStartLocation.X + 18;
            int targetY = area.Top - Height - 40;
            launchTargetLocation = new Point(targetX, targetY);

            HideLaunchPad();
            overLaunchPad = true;
            SetLaunchVisualMode(true);
            Invalidate();
        }

        private void UpdateLaunchAnimation(double elapsed)
        {
            const double chargeDuration = 0.36;
            const double flightDuration = 0.95;
            const double totalDuration = chargeDuration + flightDuration;
            launchElapsed += elapsed;

            if (launchElapsed < chargeDuration)
            {
                double charge = launchElapsed / chargeDuration;
                int shakeX = (int)Math.Round(Math.Sin(launchElapsed * 82.0) * (1.0 + charge * 2.2));
                int compression = (int)Math.Round(Math.Sin(charge * Math.PI) * 5.0);
                Location = new Point(launchStartLocation.X + shakeX, launchStartLocation.Y + compression);
                return;
            }

            double t = Math.Min(1.0, (launchElapsed - chargeDuration) / flightDuration);
            // 先离台、再持续加速，不再先猛窜后减速。
            double accelerated = Math.Pow(t, 1.7);
            Point next = Lerp(launchStartLocation, launchTargetLocation, accelerated);
            double sway = Math.Sin(t * Math.PI) * (1.0 - t) * 12.0;
            next.X += (int)Math.Round(sway);
            Location = next;
            Opacity = Math.Max(0.18, 0.96 - Math.Max(0.0, t - 0.72) / 0.28 * 0.78);

            if (launchElapsed >= totalDuration)
            {
                launchAnimating = false;
                overLaunchPad = false;
                launchElapsed = 0.0;
                Location = launchReturnLocation;
                Opacity = launchReturnOpacity;
                SetLaunchVisualMode(false);
                KeepInsideWorkingArea();
                Invalidate();
            }
        }

        private void SetLaunchVisualMode(bool active)
        {
            if (launchVisualMode == active)
            {
                return;
            }

            launchVisualMode = active;
            if (active)
            {
                BackColor = LaunchTransparentColor;
                TransparencyKey = LaunchTransparentColor;
            }
            else
            {
                TransparencyKey = Color.Empty;
                BackColor = Color.White;
            }

            UpdateWindowRegion();
        }

        private static double EaseOutCubic(double t)
        {
            double inverse = 1.0 - t;
            return 1.0 - inverse * inverse * inverse;
        }

        private static Point Lerp(Point start, Point end, double t)
        {
            return new Point(
                start.X + (int)Math.Round((end.X - start.X) * t),
                start.Y + (int)Math.Round((end.Y - start.Y) * t));
        }

        private void UpdateWindowRegion()
        {
            if (Width <= 0 || Height <= 0)
            {
                return;
            }

            if (launchVisualMode)
            {
                Region oldRegion = Region;
                Region = null;
                if (oldRegion != null)
                {
                    oldRegion.Dispose();
                }

                return;
            }

            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddEllipse(new Rectangle(4, 4, Width - 9, Height - 9));
                Region oldRegion = Region;
                Region = new Region(path);
                if (oldRegion != null)
                {
                    oldRegion.Dispose();
                }
            }
        }

        private void KeepInsideWorkingArea()
        {
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int x = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
            int y = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
            Location = new Point(x, y);
        }

        // 只有程序退出这条路才允许真正销毁窗体；用户按 Alt+F4 只会把它藏起来。
        public void CloseForExit()
        {
            closingForExit = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 用户用 Alt+F4 或系统菜单关掉球时，只隐藏，不销毁。窗体一旦被销毁，
            // 托盘菜单里的每一项都会抛 ObjectDisposedException，球就再也叫不回来了。
            if (!closingForExit)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (animationTimer != null) animationTimer.Dispose();
                if (labelFont != null) labelFont.Dispose();
                if (speedFont != null) speedFont.Dispose();
                if (unitFont != null) unitFont.Dispose();
                if (statFont != null) statFont.Dispose();
                if (statArrowFont != null) statArrowFont.Dispose();
                if (appIcon != null) appIcon.Dispose();
                if (launchPadForm != null) launchPadForm.Dispose();
                if (detailsForm != null) detailsForm.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    internal sealed class SpeedDetailsForm : Form
    {
        private const int MaxSamples = 60;
        /// <summary>资源面板最多画几块磁盘。 Dock 里插一排 U 盘也不会把窗口撑到屏幕外。</summary>
        private const int MaxDiskRows = 8;
        /// <summary>CPU/内存那一行的高度。</summary>
        private const int MetricRowHeight = 44;
        /// <summary>每块磁盘一行的高度。</summary>
        private const int DiskRowHeight = 24;
        /// <summary>CPU/内存两列的宽度。</summary>
        private const int MetricWidth = 128;
        private readonly double[] downloadHistory = new double[MaxSamples];
        private readonly double[] uploadHistory = new double[MaxSamples];
        private readonly double[] smoothBuffer = new double[MaxSamples];
        private int sampleCount;
        /// <summary>这 60 秒里出现过的最高速率，画在曲线右上角当比例尺的说明。</summary>
        private double historyPeak;
        private readonly TrafficStore trafficStore;
        private SpeedSample currentSample;
        private readonly SystemResourceMonitor resourceMonitor = new SystemResourceMonitor();
        private SystemResourceSnapshot resourceSnapshot;

        // 面板上真正画出来的数字，每帧朝采样值逼近，避免"一秒跳一下"。
        private double shownDownload;
        private double shownUpload;
        private double shownCpu;
        private double shownMemory;
        private double[] shownDisks = new double[0];
        private bool shownPrimed;

        // 带死区的目标：采样在阈值内晃就不换目标，界面上的数字才不会一直蹦。
        private readonly HeldValue heldDownload = new HeldValue(0.25, 2048.0);
        private readonly HeldValue heldUpload = new HeldValue(0.25, 2048.0);
        private readonly HeldValue heldCpu = new HeldValue(0.12, 1.0);
        private readonly HeldValue heldMemory = new HeldValue(0.06, 0.5);
        private readonly List<HeldValue> heldDisks = new List<HeldValue>();
        private readonly Font titleFont = new Font("Segoe UI", 12.0f, FontStyle.Bold);
        private readonly Font valueFont = new Font("Segoe UI", 10.0f, FontStyle.Bold);
        private readonly Font smallFont = new Font("Segoe UI", 8.2f, FontStyle.Regular);

        /// <summary>鼠标是否停在关闭按钮上。用来画悬停反馈。</summary>
        private bool closeHover;

        /// <summary>
        /// 右上角关闭按钮的点击区域。
        ///
        /// 这个窗口是无边框的（FormBorderStyle.None），系统不给画关闭按钮，
        /// 所以只能自己画一个并自己判断点击 —— 否则窗口一旦显示出来，
        /// 用户除了再点一次悬浮球之外没有任何办法关掉它。
        /// 尺寸 26×26 是按「手指/鼠标容易命中」定的，比 ✕ 图形本身大一圈。
        /// </summary>
        private Rectangle CloseButtonBounds
        {
            get { return new Rectangle(Width - 34, 8, 26, 26); }
        }

        public SpeedDetailsForm(TrafficStore store)
        {
            trafficStore = store;
            Text = "Network details";
            Size = new Size(330, 372);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(248, 252, 255);
            DoubleBuffered = true;
            Padding = new Padding(12);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        public void ResetTodayTraffic()
        {
            // The store itself is reset by SpeedBallApplication; just repaint.
            Invalidate();
        }

        public void AddSample(SpeedSample sample)
        {
            currentSample = sample;
            resourceSnapshot = resourceMonitor.Read();
            SyncResourcePanelHeight();

            heldDownload.Update(sample.DownloadBytesPerSecond);
            heldUpload.Update(sample.UploadBytesPerSecond);
            heldCpu.Update(resourceSnapshot.CpuPercent);
            heldMemory.Update(resourceSnapshot.MemoryPercent);

            DiskActivityRow[] disks = resourceSnapshot.Disks;
            int diskCount = disks == null ? 0 : Math.Min(disks.Length, MaxDiskRows);
            while (heldDisks.Count < diskCount)
            {
                heldDisks.Add(new HeldValue(0.2, 1.0));
            }

            for (int i = 0; i < diskCount; i++)
            {
                heldDisks[i].Update(disks[i].Percent);
            }

            if (sampleCount < MaxSamples)
            {
                downloadHistory[sampleCount] = sample.DownloadBytesPerSecond;
                uploadHistory[sampleCount] = sample.UploadBytesPerSecond;
                sampleCount++;
            }
            else
            {
                Array.Copy(downloadHistory, 1, downloadHistory, 0, MaxSamples - 1);
                Array.Copy(uploadHistory, 1, uploadHistory, 0, MaxSamples - 1);
                downloadHistory[MaxSamples - 1] = sample.DownloadBytesPerSecond;
                uploadHistory[MaxSamples - 1] = sample.UploadBytesPerSecond;
            }

            double peak = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                double value = downloadHistory[i] > uploadHistory[i]
                    ? downloadHistory[i]
                    : uploadHistory[i];
                if (value > peak)
                {
                    peak = value;
                }
            }

            historyPeak = peak;

            if (!shownPrimed)
            {
                // 第一拍直接落位，否则一开面板就是从 0 往上爬
                SnapShownValues();
                shownPrimed = true;
            }

            Invalidate();
        }

        /// <summary>
        /// 每帧调一次：把面板上的数字朝最新采样值挪一小步。
        ///
        /// 采样是一秒一拍，直接画就是「这一秒 0.1、下一秒 2.3」的硬跳；
        /// 按帧插值之后，数字、百分比和进度条都是连续爬过去的，跟火箭动画一个节奏。
        /// </summary>
        public void Animate(double elapsed)
        {
            if (!Visible)
            {
                // 没显示就不做动画，直接落位，免得下次打开时从几分钟前的旧数字爬过来
                SnapShownValues();
                return;
            }

            bool moved = false;

            double down = SmoothValue.Toward(shownDownload, heldDownload.Target, elapsed, 0.5, 256.0);
            if (down != shownDownload)
            {
                shownDownload = down;
                moved = true;
            }

            double up = SmoothValue.Toward(shownUpload, heldUpload.Target, elapsed, 0.5, 256.0);
            if (up != shownUpload)
            {
                shownUpload = up;
                moved = true;
            }

            double cpu = SmoothValue.Toward(shownCpu, heldCpu.Target, elapsed, 0.5, 0.1);
            if (cpu != shownCpu)
            {
                shownCpu = cpu;
                moved = true;
            }

            double memory = SmoothValue.Toward(shownMemory, heldMemory.Target,
                elapsed, 0.5, 0.05);
            if (memory != shownMemory)
            {
                shownMemory = memory;
                moved = true;
            }

            DiskActivityRow[] disks = resourceSnapshot.Disks;
            int count = disks == null ? 0 : Math.Min(disks.Length, MaxDiskRows);
            if (shownDisks.Length != count)
            {
                shownDisks = new double[count];
                for (int i = 0; i < count; i++)
                {
                    shownDisks[i] = disks[i].Percent;
                }

                moved = true;
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    double target = i < heldDisks.Count ? heldDisks[i].Target : disks[i].Percent;
                    double value = SmoothValue.Toward(shownDisks[i], target, elapsed, 0.5, 0.1);
                    if (value != shownDisks[i])
                    {
                        shownDisks[i] = value;
                        moved = true;
                    }
                }
            }

            if (moved)
            {
                Invalidate();
            }
        }

        private void SnapShownValues()
        {
            heldDownload.Snap(currentSample.DownloadBytesPerSecond);
            heldUpload.Snap(currentSample.UploadBytesPerSecond);
            heldCpu.Snap(resourceSnapshot.CpuPercent);
            heldMemory.Snap(resourceSnapshot.MemoryPercent);

            shownDownload = heldDownload.Target;
            shownUpload = heldUpload.Target;
            shownCpu = heldCpu.Target;
            shownMemory = heldMemory.Target;

            DiskActivityRow[] disks = resourceSnapshot.Disks;
            int count = disks == null ? 0 : Math.Min(disks.Length, MaxDiskRows);
            if (shownDisks.Length != count)
            {
                shownDisks = new double[count];
            }

            while (heldDisks.Count < count)
            {
                heldDisks.Add(new HeldValue(0.2, 1.0));
            }

            for (int i = 0; i < count; i++)
            {
                heldDisks[i].Snap(disks[i].Percent);
                shownDisks[i] = heldDisks[i].Target;
            }
        }

        /// <summary>
        /// 磁盘数量一变就把窗口高度跟上：插个 U 盘当场多一行，拔掉当场少一行。
        /// 变高之后底边可能已经出屏了，所以顺手按回工作区内。
        /// </summary>
        private void SyncResourcePanelHeight()
        {
            DiskActivityRow[] disks = resourceSnapshot.Disks;
            int rows = disks == null ? 0 : Math.Min(disks.Length, MaxDiskRows);
            int height = 275 + (8 + MetricRowHeight + rows * DiskRowHeight + 8) + 12;
            if (height == Height)
            {
                return;
            }

            Height = height;

            try
            {
                Rectangle area = Screen.FromControl(this).WorkingArea;
                if (Bottom > area.Bottom)
                {
                    Top = Math.Max(area.Top, area.Bottom - Height);
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// 把详情面板原样渲染成 PNG，只给 <c>--render-details 路径</c> 自检用。
        /// 假数据按日常待机那种"20~50 KB/s 抖 + 偶尔一下 190 KB/s"来造，
        /// 这样能看出真实场景下曲线是什么样，而不是一排理想化的正弦波。
        /// </summary>
        public static void SavePreview(string path)
        {
            TrafficStore store = new TrafficStore();
            using (SpeedDetailsForm form = new SpeedDetailsForm(store))
            {
                // 逼出窗口句柄，否则 DrawToBitmap 画出来是一张白图。
                if (form.Handle == IntPtr.Zero)
                {
                    return;
                }

                // 磁盘活动率是"两次读数之间的差值"，必须真的隔一秒取两次，
                // 连着循环喂 60 条假数据只会算出垃圾值（间隔≈0）。
                form.resourceSnapshot = form.resourceMonitor.Read();
                System.Threading.Thread.Sleep(1100);
                form.resourceSnapshot = form.resourceMonitor.Read();

                // 曲线历史直接写数组，不走 AddSample —— 否则每写一个点都会再读一次计数器。
                double peak = 0;
                for (int i = 0; i < MaxSamples; i++)
                {
                    double down = (28 + 16 * Math.Sin(i * 1.13) + 10 * Math.Sin(i * 2.71)) * 1024.0;
                    if (i == 19 || i == 20)
                    {
                        down = 190 * 1024.0;
                    }

                    double up = (16 + 9 * Math.Sin(i * 1.7 + 1.2)) * 1024.0;

                    form.downloadHistory[i] = down;
                    form.uploadHistory[i] = up;
                    peak = Math.Max(peak, Math.Max(down, up));
                }

                form.sampleCount = MaxSamples;
                form.historyPeak = peak;
                form.currentSample = new SpeedSample(form.downloadHistory[MaxSamples - 1],
                    form.uploadHistory[MaxSamples - 1], true, 1.0, 0, 0);
                form.SnapShownValues();
                form.SyncResourcePanelHeight();

                using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        }

        /// <summary>
        /// 画右上角的关闭按钮。
        ///
        /// 用两条线段而不是字体里的 "✕" 字符：字号一变，字符的位置和粗细就跟着变，
        /// 两条线可以精确控制，也不会因为系统字体缺字变成方块。
        /// </summary>
        private void DrawCloseButton(Graphics g)
        {
            Rectangle box = CloseButtonBounds;
            if (closeHover)
            {
                using (Brush hover = new SolidBrush(Color.FromArgb(28, 60, 110, 180)))
                {
                    g.FillEllipse(hover, box);
                }
            }

            float cx = box.X + box.Width / 2f;
            float cy = box.Y + box.Height / 2f;
            const float arm = 4.2f;

            Color stroke = closeHover
                ? Color.FromArgb(36, 54, 78)
                : Color.FromArgb(132, 148, 168);
            using (Pen pen = new Pen(stroke, 1.6f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, cx - arm, cy - arm, cx + arm, cy + arm);
                g.DrawLine(pen, cx + arm, cy - arm, cx - arm, cy + arm);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hover = CloseButtonBounds.Contains(e.Location);
            if (hover != closeHover)
            {
                closeHover = hover;
                Cursor = hover ? Cursors.Hand : Cursors.Default;
                Invalidate(CloseButtonBounds);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (closeHover)
            {
                closeHover = false;
                Cursor = Cursors.Default;
                Invalidate(CloseButtonBounds);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && CloseButtonBounds.Contains(e.Location))
            {
                closeHover = false;
                Cursor = Cursors.Default;
                Hide();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Rectangle body = new Rectangle(1, 1, Width - 3, Height - 3);
            using (GraphicsPath path = CreateRoundedRectangle(body, 18))
            using (LinearGradientBrush background = new LinearGradientBrush(body, Color.White, Color.FromArgb(235, 247, 255), LinearGradientMode.Vertical))
            using (Pen border = new Pen(Color.FromArgb(120, 71, 164, 226), 1.2f))
            {
                g.FillPath(background, path);
                g.DrawPath(border, path);
            }

            using (Brush dark = new SolidBrush(Color.FromArgb(28, 38, 56)))
            using (Brush muted = new SolidBrush(Color.FromArgb(96, 112, 132)))
            using (Brush downBrush = new SolidBrush(Color.FromArgb(35, 119, 238)))
            using (Brush upBrush = new SolidBrush(Color.FromArgb(16, 178, 92)))
            {
                g.DrawString("\u7f51\u7edc\u8be6\u60c5", titleFont, dark, 16, 13);
                g.DrawString(currentSample.HasAnyAdapter ? "\u8fde\u63a5\u6b63\u5e38" : "\u7f51\u7edc\u79bb\u7ebf", smallFont, muted, 236, 17);
                g.DrawString("\u4e0b\u8f7d  " + SpeedFormatter.FormatMegabytes(heldDownload.Target), valueFont, downBrush, 16, 43);
                g.DrawString("\u4e0a\u4f20  " + SpeedFormatter.FormatMegabytes(heldUpload.Target), valueFont, upBrush, 168, 43);
                g.DrawString("\u6700\u8fd1 60 \u79d2", smallFont, muted, 16, 72);

                // 曲线是自动缩放的，不标出峰值的话，"一根尖刺"就永远看不出实际量级。
                if (historyPeak > 0)
                {
                    string peakText = "\u5cf0\u503c " + SpeedFormatter.Format(historyPeak) + "/s";
                    SizeF peakSize = g.MeasureString(peakText, smallFont);
                    g.DrawString(peakText, smallFont, muted, Width - 16 - peakSize.Width, 72);
                }
            }

            DrawCloseButton(g);

            Rectangle graph = new Rectangle(16, 91, Width - 32, 82);
            DrawGraph(g, graph);

            long todayDate = TrafficStore.ToDateInt(DateTime.Today);
            long[] todayTotals = trafficStore != null
                ? trafficStore.GetDayTotals(todayDate)
                : new long[] { 0, 0 };
            DateTime monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            long[] monthTotals = trafficStore != null
                ? trafficStore.GetRangeTotals(TrafficStore.ToDateInt(monthStart), todayDate)
                : new long[] { 0, 0 };

            using (Brush muted = new SolidBrush(Color.FromArgb(90, 108, 128)))
            using (Brush dark = new SolidBrush(Color.FromArgb(32, 44, 62)))
            {
                g.DrawString("\u4eca\u65e5\u4e0b\u8f7d", smallFont, muted, 18, 184);
                g.DrawString(SpeedFormatter.FormatTraffic(todayTotals[0]), valueFont, dark, 18, 201);
                g.DrawString("\u4eca\u65e5\u4e0a\u4f20", smallFont, muted, 174, 184);
                g.DrawString(SpeedFormatter.FormatTraffic(todayTotals[1]), valueFont, dark, 174, 201);
                g.DrawString("\u672c\u6708\u4e0b\u8f7d", smallFont, muted, 18, 224);
                g.DrawString(SpeedFormatter.FormatTraffic(monthTotals[0]), valueFont, dark, 18, 241);
                g.DrawString("\u672c\u6708\u4e0a\u4f20", smallFont, muted, 174, 224);
                g.DrawString(SpeedFormatter.FormatTraffic(monthTotals[1]), valueFont, dark, 174, 241);
            }

            DrawResourcePanel(g);
        }

        private void DrawResourcePanel(Graphics g)
        {
            DiskActivityRow[] disks = resourceSnapshot.Disks;
            int diskCount = disks == null ? 0 : Math.Min(disks.Length, MaxDiskRows);

            // 有几块磁盘就画几行：U 盘插上来当场多一行，拔掉当场少一行。
            int panelHeight = 8 + MetricRowHeight + diskCount * DiskRowHeight + 8;
            Rectangle panel = new Rectangle(16, 275, Width - 32, panelHeight);
            using (Brush fill = new SolidBrush(Color.FromArgb(235, 245, 252)))
            using (Pen border = new Pen(Color.FromArgb(150, 190, 218), 1.0f))
            {
                g.FillRectangle(fill, panel);
                g.DrawRectangle(border, panel);
            }

            int left = panel.Left + 8;
            DrawResourceMetric(g, "CPU", FormatPercent(heldCpu.Target),
                heldCpu.Target, left, panel.Top + 6, MetricWidth,
                Color.FromArgb(242, 126, 42));
            DrawResourceMetric(g, "\u5185\u5b58", FormatPercent(heldMemory.Target),
                heldMemory.Target, left + MetricWidth + 20, panel.Top + 6, MetricWidth,
                Color.FromArgb(35, 119, 238));

            int y = panel.Top + 6 + MetricRowHeight;
            for (int i = 0; i < diskCount; i++)
            {
                double percent = i < heldDisks.Count ? heldDisks[i].Target : disks[i].Percent;
                DrawDiskRow(g, disks[i].Label, percent, left, y, panel.Width - 16, i);
                y += DiskRowHeight;
            }
        }

        /// <summary>CPU / 内存这种"一格一值"的指标：标签在上、数字在中、进度条在下。</summary>
        private void DrawResourceMetric(Graphics g, string label, string value, double percent,
            int x, int y, int width, Color color)
        {
            using (Brush muted = new SolidBrush(Color.FromArgb(90, 108, 128)))
            using (Brush dark = new SolidBrush(Color.FromArgb(32, 44, 62)))
            using (Brush track = new SolidBrush(Color.FromArgb(210, 224, 235)))
            using (Brush bar = new SolidBrush(color))
            {
                g.DrawString(label, smallFont, muted, x, y);
                g.DrawString(value, valueFont, dark, x, y + 13);
                g.FillRectangle(track, x, y + 32, width, 6);
                g.FillRectangle(bar, x, y + 32,
                    (int)Math.Round(width * Math.Min(100.0, Math.Max(0.0, percent)) / 100.0), 6);
            }
        }

        /// <summary>
        /// 一块物理磁盘占一行：左边"磁盘 0  C:"，右边活动百分比，下面一条通栏进度条。
        /// 口径跟任务管理器那一列一样 —— 磁盘有多忙，不是磁盘用了多少空间。
        /// </summary>
        private void DrawDiskRow(Graphics g, string label, double percent, int x, int y, int width, int index)
        {
            Color color = index % 2 == 0
                ? Color.FromArgb(16, 178, 92)
                : Color.FromArgb(64, 148, 232);

            using (Brush muted = new SolidBrush(Color.FromArgb(90, 108, 128)))
            using (Brush dark = new SolidBrush(Color.FromArgb(32, 44, 62)))
            using (Brush track = new SolidBrush(Color.FromArgb(210, 224, 235)))
            using (Brush bar = new SolidBrush(color))
            {
                g.DrawString(label, smallFont, muted, x, y);

                string value = FormatPercent(percent);
                SizeF size = g.MeasureString(value, smallFont);
                g.DrawString(value, smallFont, dark, x + width - size.Width, y);

                g.FillRectangle(track, x, y + 15, width, 5);
                g.FillRectangle(bar, x, y + 15,
                    (int)Math.Round(width * Math.Min(100.0, Math.Max(0.0, percent)) / 100.0), 5);
            }
        }

        private static string FormatPercent(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                value = 0;
            }

            return Math.Max(0.0, Math.Min(100.0, value)).ToString("0.#") + "%";
        }

        private void DrawGraph(Graphics g, Rectangle graph)
        {
            // 底：圆角卡片 + 极淡的竖向渐变，比一块生硬的浅蓝方块耐看
            using (GraphicsPath shape = CreateRoundedRectangle(graph, 7))
            using (LinearGradientBrush background = new LinearGradientBrush(
                new Rectangle(graph.Left, graph.Top, Math.Max(1, graph.Width), Math.Max(1, graph.Height * 2)),
                Color.FromArgb(253, 254, 255), Color.FromArgb(231, 242, 253),
                LinearGradientMode.Vertical))
            {
                g.FillPath(background, shape);
            }

            if (sampleCount < 2)
            {
                DrawGraphBorder(g, graph);
                return;
            }

            // 比例尺以窗口内的最高值为准（下面再兜一个 512 KB/s 的地板），
            // 这样 5 MB/s 的大下载能顶满，日常零碎流量也不会被放大成假起伏。
            double peak = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                double value = downloadHistory[i] > uploadHistory[i]
                    ? downloadHistory[i]
                    : uploadHistory[i];
                if (value > peak)
                {
                    peak = value;
                }
            }

            // 比例尺用线性，但设一个 512 KB/s 的地板：
            // 后台心跳、网页聊天这种 20~50 KB/s 的流量本来就不该占满整张图，
            // 地板把它们压成贴着底边的细线，真有下载时才会顶上去。
            double ceiling = Math.Max(peak, 512.0 * 1024.0);

            // 画线区域往里缩一圈，端点的圆点才不会压在边框上
            Rectangle plot = new Rectangle(graph.Left + 5, graph.Top + 6,
                graph.Width - 10, graph.Height - 12);

            Color downColor = Color.FromArgb(35, 119, 238);
            Color upColor = Color.FromArgb(16, 178, 92);

            GraphicsState state = g.Save();
            try
            {
                using (GraphicsPath clip = CreateRoundedRectangle(graph, 7))
                {
                    g.SetClip(clip);

                    using (Pen grid = new Pen(Color.FromArgb(40, 110, 150, 190), 1.0f))
                    {
                        grid.DashStyle = DashStyle.Dash;
                        grid.DashPattern = new float[] { 3.0f, 5.0f };
                        for (int row = 1; row <= 2; row++)
                        {
                            int y = plot.Top + plot.Height * row / 3;
                            g.DrawLine(grid, graph.Left + 1, y, graph.Right - 1, y);
                        }
                    }

                    DrawHistoryArea(g, plot, downloadHistory, ceiling, downColor, 120);
                    DrawHistoryArea(g, plot, uploadHistory, ceiling, upColor, 90);
                    DrawHistoryLine(g, plot, downloadHistory, ceiling, downColor, 2.4f);
                    DrawHistoryLine(g, plot, uploadHistory, ceiling, upColor, 1.9f);
                }
            }
            finally
            {
                // 裁剪必须还原，否则后面的流量数字和磁盘行全被裁掉
                g.Restore(state);
            }

            DrawHistoryDot(g, plot, downloadHistory, ceiling, downColor);
            DrawHistoryDot(g, plot, uploadHistory, ceiling, upColor);

            DrawGraphBorder(g, graph);
        }

        private static void DrawGraphBorder(Graphics g, Rectangle graph)
        {
            using (GraphicsPath shape = CreateRoundedRectangle(graph, 7))
            using (Pen border = new Pen(Color.FromArgb(64, 148, 190, 226), 1.0f))
            {
                g.DrawPath(border, shape);
            }
        }

        /// <summary>
        /// 把采样值换算成坐标。纵向是线性的（地板 512 KB/s，见 DrawGraph），
        /// 横向按当前点数铺满整条宽度 —— 刚打开面板时也不会只画左半截。
        ///
        /// 画之前做一次三点加权平滑：采样一秒一个，20~50 KB/s 的日常流量本身就在抖，
        /// 直接连点会画出一排锯齿。真正的测量值没动，只是画出来的线不跟着抖。
        /// </summary>
        private PointF[] BuildPoints(Rectangle plot, double[] values, double ceiling)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                double previous = values[i > 0 ? i - 1 : i];
                double next = values[i < sampleCount - 1 ? i + 1 : i];
                smoothBuffer[i] = (previous + values[i] * 2.0 + next) / 4.0;
            }

            PointF[] points = new PointF[sampleCount];
            float span = sampleCount > 1 ? sampleCount - 1 : 1;
            for (int i = 0; i < sampleCount; i++)
            {
                float x = plot.Left + (plot.Width - 1) * i / span;
                float ratio = (float)Math.Min(1.0, Math.Max(0.0, smoothBuffer[i]) / ceiling);
                points[i] = new PointF(x, plot.Bottom - ratio * (plot.Height - 1));
            }

            return points;
        }

        /// <summary>曲线下面的渐变填充，让"有流量"这件事一眼看得出来。</summary>
        private void DrawHistoryArea(Graphics g, Rectangle plot, double[] values, double ceiling,
            Color color, int alpha)
        {
            PointF[] points = BuildPoints(plot, values, ceiling);
            using (GraphicsPath path = new GraphicsPath())
            {
                // 0.45 的张力 = 轻微平滑，不会把尖锐的突发抹成圆包
                path.AddCurve(points, 0.45f);
                path.AddLine(points[sampleCount - 1].X, plot.Bottom, points[0].X, plot.Bottom);
                path.CloseFigure();

                using (LinearGradientBrush fill = new LinearGradientBrush(
                    new Rectangle(plot.Left, plot.Top - 2, Math.Max(1, plot.Width), Math.Max(2, plot.Height + 2)),
                    Color.FromArgb(alpha, color), Color.FromArgb(8, color),
                    LinearGradientMode.Vertical))
                {
                    g.FillPath(fill, path);
                }
            }
        }

        private void DrawHistoryLine(Graphics g, Rectangle plot, double[] values, double ceiling,
            Color color, float width)
        {
            PointF[] points = BuildPoints(plot, values, ceiling);
            using (Pen pen = new Pen(color, width))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawCurve(pen, points, 0.45f);
            }
        }

        /// <summary>最右端点一个小圆点，表示"这条线现在在这里"。</summary>
        private void DrawHistoryDot(Graphics g, Rectangle plot, double[] values, double ceiling, Color color)
        {
            PointF last = BuildPoints(plot, values, ceiling)[sampleCount - 1];
            using (Brush halo = new SolidBrush(Color.FromArgb(230, 255, 255, 255)))
            {
                g.FillEllipse(halo, last.X - 4.0f, last.Y - 4.0f, 8.0f, 8.0f);
            }

            using (Brush dot = new SolidBrush(color))
            {
                g.FillEllipse(dot, last.X - 2.6f, last.Y - 2.6f, 5.2f, 5.2f);
            }
        }

        private static GraphicsPath CreateRoundedRectangle(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;
            Rectangle arc = new Rectangle(rect.Location, new Size(diameter, diameter));
            path.AddArc(arc, 180, 90);
            arc.X = rect.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = rect.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = rect.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                titleFont.Dispose();
                valueFont.Dispose();
                smallFont.Dispose();
                resourceMonitor.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// 一块物理磁盘的实时活动情况。
    /// </summary>
    internal struct DiskActivityRow
    {
        /// <summary>显示用名字，形如「磁盘 0  C: D:」。</summary>
        public readonly string Label;
        /// <summary>活动时间百分比（0~100），跟任务管理器「磁盘」那一列同口径。</summary>
        public readonly double Percent;

        public DiskActivityRow(string label, double percent)
        {
            Label = label;
            Percent = percent;
        }
    }

    internal struct SystemResourceSnapshot
    {
        public readonly double CpuPercent;
        public readonly double MemoryPercent;
        public readonly ulong MemoryUsedBytes;
        public readonly ulong MemoryTotalBytes;
        /// <summary>每块物理磁盘一行，插拔 U 盘时这个数组会当场增减。</summary>
        public readonly DiskActivityRow[] Disks;

        public SystemResourceSnapshot(double cpuPercent, double memoryPercent,
            ulong memoryUsedBytes, ulong memoryTotalBytes, DiskActivityRow[] disks)
        {
            CpuPercent = cpuPercent;
            MemoryPercent = memoryPercent;
            MemoryUsedBytes = memoryUsedBytes;
            MemoryTotalBytes = memoryTotalBytes;
            Disks = disks;
        }
    }

    internal sealed class SystemResourceMonitor : IDisposable
    {
        /// <summary>性能计数器里的物理磁盘分类。实例名形如「0 C:」「1 D: E:」。</summary>
        private const string DiskCategory = "PhysicalDisk";

        /// <summary>
        /// 每隔几次采样重扫一遍磁盘列表。
        /// 每次重扫要花约 45ms（读注册表里的实例名），放在 UI 线程上每秒扫一次会卡动画，
        /// 所以 5 秒扫一次 —— 插上 U 盘最多 5 秒内出现在面板里。
        /// </summary>
        private const int DiskRescanInterval = 5;

        private sealed class DiskCounter
        {
            public PerformanceCounter Idle;
            public bool Primed;
        }

        private readonly Dictionary<string, DiskCounter> diskCounters =
            new Dictionary<string, DiskCounter>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> diskInstances = new List<string>();
        private readonly List<DiskActivityRow> diskRows = new List<DiskActivityRow>();

        private bool hasCpuBaseline;
        private long lastIdle;
        private long lastKernel;
        private long lastUser;
        private int rescanCountdown;

        public void Dispose()
        {
            foreach (DiskCounter counter in diskCounters.Values)
            {
                try
                {
                    counter.Idle.Dispose();
                }
                catch
                {
                }
            }

            diskCounters.Clear();
            diskInstances.Clear();
        }

        public SystemResourceSnapshot Read()
        {
            double cpu = ReadCpuPercent();
            ulong totalMemory = 0;
            ulong availableMemory = 0;
            double memory = ReadMemoryPercent(ref totalMemory, ref availableMemory);
            ulong usedMemory = totalMemory >= availableMemory ? totalMemory - availableMemory : 0;
            return new SystemResourceSnapshot(cpu, memory, usedMemory, totalMemory, ReadDisks());
        }

        private double ReadCpuPercent()
        {
            NativeMethods.FileTime idle;
            NativeMethods.FileTime kernel;
            NativeMethods.FileTime user;
            if (!NativeMethods.GetSystemTimes(out idle, out kernel, out user))
            {
                return 0;
            }

            long idleValue = idle.ToInt64();
            long kernelValue = kernel.ToInt64();
            long userValue = user.ToInt64();
            if (!hasCpuBaseline)
            {
                hasCpuBaseline = true;
                lastIdle = idleValue;
                lastKernel = kernelValue;
                lastUser = userValue;
                return 0;
            }

            long idleDelta = Math.Max(0, idleValue - lastIdle);
            long totalDelta = Math.Max(0, kernelValue - lastKernel) + Math.Max(0, userValue - lastUser);
            lastIdle = idleValue;
            lastKernel = kernelValue;
            lastUser = userValue;
            if (totalDelta <= 0)
            {
                return 0;
            }

            return Math.Max(0.0, Math.Min(100.0, 100.0 * (1.0 - (double)idleDelta / totalDelta)));
        }

        private static double ReadMemoryPercent(ref ulong totalMemory, ref ulong availableMemory)
        {
            NativeMethods.MemoryStatusEx status = new NativeMethods.MemoryStatusEx();
            status.Length = (uint)Marshal.SizeOf(typeof(NativeMethods.MemoryStatusEx));
            if (!NativeMethods.GlobalMemoryStatusEx(ref status) || status.TotalPhysicalMemory == 0)
            {
                return 0;
            }

            totalMemory = status.TotalPhysicalMemory;
            availableMemory = status.AvailablePhysicalMemory;
            return Math.Max(0.0, Math.Min(100.0, status.MemoryLoad));
        }

        /// <summary>
        /// 读出每块物理磁盘的「活动时间」，口径就是任务管理器那一列：
        /// 活动时间 = 100 − 空闲时间，不是磁盘用了多少空间。
        ///
        /// 磁盘列表每秒重扫一次，所以插上 U 盘（或者拔掉）这里会当场多一行、少一行，
        /// 不用重启程序，也不需要知道盘符 —— 计数器实例名里自带盘符。
        /// </summary>
        private DiskActivityRow[] ReadDisks()
        {
            if (--rescanCountdown <= 0)
            {
                rescanCountdown = DiskRescanInterval;
                RescanDisks();
            }

            diskRows.Clear();
            for (int i = 0; i < diskInstances.Count; i++)
            {
                string instance = diskInstances[i];
                DiskCounter counter;
                if (!diskCounters.TryGetValue(instance, out counter))
                {
                    continue;
                }

                double percent = 0;
                try
                {
                    float idle = counter.Idle.NextValue();
                    // 新建的计数器第一次调用没有时间差可算，返回的是 0，
                    // 直接当"空闲 0%"会显示成 100% 活动，所以第一拍先跳过。
                    if (counter.Primed && !float.IsNaN(idle) && !float.IsInfinity(idle))
                    {
                        percent = Math.Max(0.0, Math.Min(100.0, 100.0 - idle));
                    }

                    counter.Primed = true;
                }
                catch
                {
                    // 磁盘正好被拔掉时计数器会失效；这一拍按 0 显示，下一轮重扫会清掉它。
                    percent = 0;
                }

                diskRows.Add(new DiskActivityRow(FriendlyDiskName(instance), percent));
            }

            return diskRows.ToArray();
        }

        /// <summary>重扫物理磁盘列表：新增的建计数器，消失的释放掉。</summary>
        private void RescanDisks()
        {
            string[] names;
            try
            {
                names = new PerformanceCounterCategory(DiskCategory).GetInstanceNames();
            }
            catch
            {
                // 拿不到分类（极少数精简系统）就保持上一轮的样子，不清空。
                return;
            }

            List<string> found = new List<string>();
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                if (string.IsNullOrEmpty(name) ||
                    string.Equals(name, "_Total", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                found.Add(name);
            }

            // 按磁盘号排，插拔 U 盘时行序不会乱跳
            found.Sort(CompareDiskInstances);

            List<string> stale = new List<string>();
            foreach (string key in diskCounters.Keys)
            {
                if (!found.Contains(key))
                {
                    stale.Add(key);
                }
            }

            for (int i = 0; i < stale.Count; i++)
            {
                try
                {
                    diskCounters[stale[i]].Idle.Dispose();
                }
                catch
                {
                }

                diskCounters.Remove(stale[i]);
            }

            for (int i = 0; i < found.Count; i++)
            {
                if (diskCounters.ContainsKey(found[i]))
                {
                    continue;
                }

                try
                {
                    PerformanceCounter counter = new PerformanceCounter(
                        DiskCategory, "% Idle Time", found[i], true);
                    diskCounters[found[i]] = new DiskCounter { Idle = counter };
                }
                catch
                {
                    // 这块盘的活动时间读不到就跳过它，别把整个面板拖垮
                }
            }

            diskInstances.Clear();
            for (int i = 0; i < found.Count; i++)
            {
                if (diskCounters.ContainsKey(found[i]))
                {
                    diskInstances.Add(found[i]);
                }
            }
        }

        private static int CompareDiskInstances(string a, string b)
        {
            int na = DiskNumber(a);
            int nb = DiskNumber(b);
            if (na != nb)
            {
                return na.CompareTo(nb);
            }

            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static int DiskNumber(string instance)
        {
            int space = instance.IndexOf(' ');
            string head = space > 0 ? instance.Substring(0, space) : instance;
            int value;
            return int.TryParse(head, out value) ? value : int.MaxValue;
        }

        /// <summary>
        /// 把「0 C: D:」这种实例名翻成「磁盘 0（C: D:）」。
        /// 括号是刻意加的：C: 和 D: 是同一块硬盘上的两个分区，任务管理器里也是一行，
        /// 写成括号一眼就知道"这两个盘符属于同一块物理盘"。
        /// </summary>
        private static string FriendlyDiskName(string instance)
        {
            int space = instance.IndexOf(' ');
            if (space <= 0)
            {
                return instance;
            }

            string number = instance.Substring(0, space);
            string volumes = instance.Substring(space + 1).Trim();
            return volumes.Length > 0
                ? "\u78c1\u76d8 " + number + "\uff08" + volumes + "\uff09"
                : "\u78c1\u76d8 " + number;
        }
    }

    internal sealed class TrafficWindow : Form
    {
        private const int RankRows = 12;
        private readonly TrafficStore store;
        private readonly Font sectionFont = new Font("Segoe UI", 9.0f, FontStyle.Bold);
        private readonly Font valueFont = new Font("Segoe UI", 10.0f, FontStyle.Bold);
        private readonly Font smallFont = new Font("Segoe UI", 8.0f, FontStyle.Regular);
        private readonly Font tinyFont = new Font("Segoe UI", 7.4f, FontStyle.Regular);
        private readonly DateTimePicker fromPicker;
        private readonly DateTimePicker toPicker;
        private readonly ToolTip toolTip = new ToolTip();
        private readonly FormsTimer refreshTimer;
        private long rangeFrom;
        private long rangeTo;
        private long[] rangeTotals = new long[] { 0, 0 };
        private List<DayPoint> rangeDays = new List<DayPoint>();
        private List<AppRankRow> rangeRanks = new List<AppRankRow>();
        private Rectangle chartPlot;
        private int chartSlot;
        private int hoverDayIndex = -1;

        public TrafficWindow(TrafficStore trafficStore)
        {
            store = trafficStore;
            Text = "流量统计";
            ClientSize = new Size(700, 700);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.White;
            DoubleBuffered = true;

            AddPresetButton("今天", "today", 16);
            AddPresetButton("昨天", "yesterday", 62);
            AddPresetButton("本月", "month", 108);
            AddPresetButton("上月", "lastmonth", 154);
            AddPresetButton("今年", "year", 200);

            Label fromLabel = new Label();
            fromLabel.Text = "从";
            fromLabel.AutoSize = true;
            fromLabel.Location = new Point(254, 16);
            fromLabel.Font = smallFont;
            Controls.Add(fromLabel);

            fromPicker = new DateTimePicker();
            fromPicker.Format = DateTimePickerFormat.Short;
            fromPicker.Font = smallFont;
            fromPicker.Location = new Point(272, 12);
            fromPicker.Width = 96;
            Controls.Add(fromPicker);

            Label toLabel = new Label();
            toLabel.Text = "到";
            toLabel.AutoSize = true;
            toLabel.Location = new Point(378, 16);
            toLabel.Font = smallFont;
            Controls.Add(toLabel);

            toPicker = new DateTimePicker();
            toPicker.Format = DateTimePickerFormat.Short;
            toPicker.Font = smallFont;
            toPicker.Location = new Point(396, 12);
            toPicker.Width = 96;
            Controls.Add(toPicker);

            Button applyButton = new Button();
            applyButton.Text = "查询";
            applyButton.Font = smallFont;
            applyButton.Location = new Point(500, 11);
            applyButton.Size = new Size(56, 25);
            applyButton.UseVisualStyleBackColor = true;
            applyButton.Click += OnApplyClick;
            Controls.Add(applyButton);

            DateTime today = DateTime.Today;
            fromPicker.Value = new DateTime(today.Year, today.Month, 1);
            toPicker.Value = today;
            rangeFrom = TrafficStore.ToDateInt(fromPicker.Value.Date);
            rangeTo = TrafficStore.ToDateInt(toPicker.Value.Date);
            RefreshData();

            refreshTimer = new FormsTimer();
            refreshTimer.Interval = 2000;
            refreshTimer.Tick += OnRefreshTick;
            refreshTimer.Start();
        }

        private void AddPresetButton(string text, string tag, int x)
        {
            Button button = new Button();
            button.Text = text;
            button.Tag = tag;
            button.Font = smallFont;
            button.Location = new Point(x, 11);
            button.Size = new Size(42, 25);
            button.UseVisualStyleBackColor = true;
            button.Click += OnPresetClick;
            Controls.Add(button);
        }

        private void OnPresetClick(object sender, EventArgs e)
        {
            Button button = sender as Button;
            string tag = button != null ? button.Tag as string : null;
            DateTime today = DateTime.Today;
            if (tag == "today")
            {
                SetRange(today, today);
            }
            else if (tag == "yesterday")
            {
                DateTime day = today.AddDays(-1);
                SetRange(day, day);
            }
            else if (tag == "month")
            {
                SetRange(new DateTime(today.Year, today.Month, 1), today);
            }
            else if (tag == "lastmonth")
            {
                DateTime first = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
                SetRange(first, first.AddMonths(1).AddDays(-1));
            }
            else if (tag == "year")
            {
                SetRange(new DateTime(today.Year, 1, 1), today);
            }
        }

        private void OnApplyClick(object sender, EventArgs e)
        {
            DateTime from = fromPicker.Value.Date;
            DateTime to = toPicker.Value.Date;
            if (to < from)
            {
                DateTime swap = from;
                from = to;
                to = swap;
            }

            SetRange(from, to);
        }

        private void SetRange(DateTime from, DateTime to)
        {
            fromPicker.Value = from;
            toPicker.Value = to;
            rangeFrom = TrafficStore.ToDateInt(from);
            rangeTo = TrafficStore.ToDateInt(to);
            hoverDayIndex = -1;
            RefreshData();
            Invalidate();
        }

        private void OnRefreshTick(object sender, EventArgs e)
        {
            if (!Visible || Disposing || IsDisposed)
            {
                return;
            }

            if (rangeTo >= TrafficStore.ToDateInt(DateTime.Today))
            {
                RefreshData();
                Invalidate();
            }
        }

        private void RefreshData()
        {
            rangeTotals = store.GetRangeTotals(rangeFrom, rangeTo);
            rangeDays = store.GetRangeDays(rangeFrom, rangeTo);
            rangeRanks = store.GetRangeRanks(rangeFrom, rangeTo, RankRows);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = -1;
            if (rangeDays.Count > 0 && chartPlot.Width > 0 && chartSlot > 0 &&
                e.X >= chartPlot.Left && e.X <= chartPlot.Right &&
                e.Y >= chartPlot.Top - 24 && e.Y <= chartPlot.Bottom)
            {
                index = (e.X - chartPlot.Left) / chartSlot;
                if (index < 0 || index >= rangeDays.Count)
                {
                    index = -1;
                }
            }

            if (index == hoverDayIndex)
            {
                return;
            }

            hoverDayIndex = index;
            if (index >= 0)
            {
                DayPoint point = rangeDays[index];
                DateTime day = TrafficStore.FromDateInt(point.Date);
                toolTip.SetToolTip(this, day.ToString("M月d日 ddd") + "\r\n下载 " +
                    SpeedFormatter.Format(point.Rx) + "\r\n上传 " + SpeedFormatter.Format(point.Tx));
            }
            else
            {
                toolTip.SetToolTip(this, string.Empty);
            }

            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            DateTime from = TrafficStore.FromDateInt(rangeFrom);
            DateTime to = TrafficStore.FromDateInt(rangeTo);
            string scopeText = rangeFrom == rangeTo
                ? from.ToString("yyyy年M月d日")
                : from.ToString("yyyy年M月d日") + "  -  " + to.ToString("yyyy年M月d日");
            int dayCount = Math.Max(1, (int)TrafficStore.DaysBetween(rangeFrom, rangeTo) + 1);

            using (Brush muted = new SolidBrush(Color.FromArgb(96, 110, 130)))
            {
                // The window caption already shows the title; painting another one here
                // used to bleed through the 4px gaps between the preset buttons.
                g.DrawString(scopeText + "  ·  共 " + dayCount + " 天", smallFont, muted, 16, 40);
            }

            DrawSummaryCards(g, dayCount);
            DrawDailyChart(g);
            DrawRankList(g);

            using (Brush note = new SolidBrush(Color.FromArgb(128, 118, 132, 150)))
            {
                g.DrawString("排行榜按 TCP 连接统计，不含 UDP/QUIC 流量与协议头开销，故各项之和略小于总流量。" +
                    "数据保存在本机 %LOCALAPPDATA%\\NetSpeedBall，年占用不足 1 MB。",
                    tinyFont, note, 16, ClientSize.Height - 20);
            }
        }

        private void DrawSummaryCards(Graphics g, int dayCount)
        {
            long down = rangeTotals[0];
            long up = rangeTotals[1];
            long total = down + up;
            long perDay = total / dayCount;
            int width = (ClientSize.Width - 32 - 30) / 4;
            string[] labels = { "下载", "上传", "合计", "日均" };
            string[] values =
            {
                SpeedFormatter.Format(down),
                SpeedFormatter.Format(up),
                SpeedFormatter.Format(total),
                SpeedFormatter.Format(perDay)
            };
            Color[] colors =
            {
                Color.FromArgb(35, 119, 238),
                Color.FromArgb(16, 178, 92),
                Color.FromArgb(58, 70, 92),
                Color.FromArgb(242, 126, 42)
            };

            for (int i = 0; i < 4; i++)
            {
                Rectangle card = new Rectangle(16 + i * (width + 10), 64, width, 52);
                using (GraphicsPath path = CreateRoundedRectangle(card, 10))
                using (Brush fill = new SolidBrush(Color.FromArgb(244, 249, 254)))
                using (Pen border = new Pen(Color.FromArgb(200, 205, 225, 240)))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(border, path);
                }

                using (Brush label = new SolidBrush(Color.FromArgb(110, 122, 142)))
                using (Brush value = new SolidBrush(colors[i]))
                {
                    g.DrawString(labels[i], smallFont, label, card.X + 12, card.Y + 7);
                    g.DrawString(values[i], valueFont, value, card.X + 12, card.Y + 24);
                }
            }
        }

        private void DrawDailyChart(Graphics g)
        {
            Rectangle panel = new Rectangle(16, 128, ClientSize.Width - 32, 236);
            using (GraphicsPath path = CreateRoundedRectangle(panel, 12))
            using (Brush fill = new SolidBrush(Color.FromArgb(247, 251, 255)))
            using (Pen border = new Pen(Color.FromArgb(205, 208, 226, 242)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            using (Brush heading = new SolidBrush(Color.FromArgb(70, 84, 104)))
            {
                g.DrawString("每日流量", sectionFont, heading, panel.X + 12, panel.Y + 8);
            }

            chartPlot = new Rectangle(panel.Left + 14, panel.Top + 34,
                panel.Width - 28, panel.Height - 66);
            chartSlot = rangeDays.Count > 0 ? Math.Max(1, chartPlot.Width / rangeDays.Count) : 0;

            double maximum = 1.0;
            for (int i = 0; i < rangeDays.Count; i++)
            {
                maximum = Math.Max(maximum, (double)(rangeDays[i].Rx + rangeDays[i].Tx));
            }

            using (Pen grid = new Pen(Color.FromArgb(70, 178, 200, 220)))
            using (Brush axisText = new SolidBrush(Color.FromArgb(130, 120, 136, 154)))
            {
                for (int row = 0; row <= 4; row++)
                {
                    int y = chartPlot.Bottom - chartPlot.Height * row / 4;
                    g.DrawLine(grid, chartPlot.Left, y, chartPlot.Right, y);
                    if (row == 4)
                    {
                        string label = SpeedFormatter.Format(maximum);
                        g.DrawString(label, tinyFont, axisText, chartPlot.Left + 2, chartPlot.Top - 16);
                    }
                }
            }

            if (rangeDays.Count == 0)
            {
                return;
            }

            int slot = chartSlot;
            int barWidth = Math.Max(1, (int)(slot * 0.34));
            for (int i = 0; i < rangeDays.Count; i++)
            {
                DayPoint point = rangeDays[i];
                int x = chartPlot.Left + i * slot + (slot - barWidth) / 2;
                if (i == hoverDayIndex)
                {
                    using (Brush hover = new SolidBrush(Color.FromArgb(40, 255, 214, 120)))
                    {
                        g.FillRectangle(hover, chartPlot.Left + i * slot, chartPlot.Top, slot, chartPlot.Height);
                    }
                }

                double downRatio = Math.Min(1.0, point.Rx / maximum);
                double upRatio = Math.Min(1.0, point.Tx / maximum);
                int downHeight = (int)Math.Round(downRatio * (chartPlot.Height - 2));
                int upHeight = (int)Math.Round(upRatio * (chartPlot.Height - 2));
                if (downHeight > 0)
                {
                    using (Brush down = new SolidBrush(Color.FromArgb(200, 62, 138, 245)))
                    {
                        g.FillRectangle(down, x, chartPlot.Bottom - downHeight, barWidth, downHeight);
                    }
                }

                if (upHeight > 0)
                {
                    using (Brush up = new SolidBrush(Color.FromArgb(210, 34, 197, 110)))
                    {
                        g.FillRectangle(up, x + barWidth, chartPlot.Bottom - upHeight, barWidth, upHeight);
                    }
                }
            }

            DrawDateAxis(g, panel);
        }

        private void DrawDateAxis(Graphics g, Rectangle panel)
        {
            using (Brush axisText = new SolidBrush(Color.FromArgb(140, 110, 124, 144)))
            {
                int count = rangeDays.Count;
                if (count == 0)
                {
                    return;
                }

                int step = count <= 16 ? 1 : (count <= 62 ? 7 : 30);
                int y = panel.Bottom - 16;
                for (int i = 0; i < count; i += step)
                {
                    if (i > 0 && chartPlot.Left + i * chartSlot + 30 > chartPlot.Right)
                    {
                        break;
                    }

                    DateTime day = TrafficStore.FromDateInt(rangeDays[i].Date);
                    string label;
                    if (count > 62)
                    {
                        if (day.Day != 1)
                        {
                            continue;
                        }

                        label = day.Month + "月";
                    }
                    else if (count > 16)
                    {
                        label = day.Day + "日";
                    }
                    else
                    {
                        label = day.Month + "/" + day.Day;
                    }

                    g.DrawString(label, tinyFont, axisText, chartPlot.Left + i * chartSlot, y);
                }
            }
        }

        private void DrawRankList(Graphics g)
        {
            using (Brush heading = new SolidBrush(Color.FromArgb(70, 84, 104)))
            {
                g.DrawString("应用排行（Top " + RankRows + "）", sectionFont, heading, 16, 378);
            }

            int rowHeight = 22;
            int y = 404;
            int nameWidth = 168;
            int barLeft = 196;
            int barRight = ClientSize.Width - 250;
            int valueLeft = ClientSize.Width - 240;

            double maxTotal = 1.0;
            for (int i = 0; i < rangeRanks.Count; i++)
            {
                maxTotal = Math.Max(maxTotal, (double)rangeRanks[i].Total);
            }

            using (Brush evenRow = new SolidBrush(Color.FromArgb(248, 251, 254)))
            using (Brush track = new SolidBrush(Color.FromArgb(235, 228, 238, 246)))
            using (Brush nameText = new SolidBrush(Color.FromArgb(52, 64, 84)))
            using (Brush valueText = new SolidBrush(Color.FromArgb(90, 104, 124)))
            using (Brush downBar = new SolidBrush(Color.FromArgb(214, 62, 138, 245)))
            using (Brush upBar = new SolidBrush(Color.FromArgb(214, 34, 197, 110)))
            {
                for (int i = 0; i < rangeRanks.Count; i++)
                {
                    AppRankRow row = rangeRanks[i];
                    if (i % 2 == 0)
                    {
                        g.FillRectangle(evenRow, 12, y, ClientSize.Width - 24, rowHeight);
                    }

                    string name = TruncateName(row.Name, nameWidth - 12, g, smallFont);
                    g.DrawString((i + 1) + ". " + name, smallFont, nameText, 16, y + 4);

                    int barWidth = barRight - barLeft;
                    int barY = y + (rowHeight - 10) / 2;
                    g.FillRectangle(track, barLeft, barY, barWidth, 10);
                    long total = row.Total;
                    if (total > 0)
                    {
                        int filled = (int)Math.Round(barWidth * total / maxTotal);
                        int downWidth = (int)Math.Round(filled * (double)row.Rx / total);
                        if (downWidth > 0)
                        {
                            g.FillRectangle(downBar, barLeft, barY, downWidth, 10);
                        }

                        if (filled - downWidth > 0)
                        {
                            g.FillRectangle(upBar, barLeft + downWidth, barY, filled - downWidth, 10);
                        }
                    }

                    string value = "\u2193 " + SpeedFormatter.Format(row.Rx) +
                                   "   \u2191 " + SpeedFormatter.Format(row.Tx);
                    SizeF size = g.MeasureString(value, smallFont);
                    g.DrawString(value, smallFont, valueText, valueLeft + (236 - size.Width) / 2, y + 4);

                    y += rowHeight;
                }
            }

            if (rangeRanks.Count == 0)
            {
                using (Brush empty = new SolidBrush(Color.FromArgb(150, 120, 134, 154)))
                {
                    // 空列表有两种原因，必须说清楚，否则用户只会以为功能坏了。
                    if (AppTrafficMonitor.CanCollectAppTraffic)
                    {
                        g.DrawString("所选范围内暂无应用流量记录", smallFont, empty, 16, y + 6);
                    }
                    else
                    {
                        g.DrawString("应用流量排行需要管理员权限", smallFont, empty, 16, y + 6);
                        g.DrawString("右键托盘图标 → 以管理员身份重启", smallFont, empty, 16, y + 24);
                    }
                }
            }
        }

        private static string TruncateName(string name, int maxWidth, Graphics g, Font font)
        {
            if (string.IsNullOrEmpty(name))
            {
                return name;
            }

            if (g.MeasureString(name, font).Width <= maxWidth)
            {
                return name;
            }

            string trimmed = name;
            while (trimmed.Length > 1 && g.MeasureString(trimmed + "…", font).Width > maxWidth)
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            }

            return trimmed + "…";
        }

        private static GraphicsPath CreateRoundedRectangle(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;
            Rectangle arc = new Rectangle(rect.Location, new Size(diameter, diameter));
            path.AddArc(arc, 180, 90);
            arc.X = rect.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = rect.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = rect.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (refreshTimer != null) refreshTimer.Dispose();
                if (toolTip != null) toolTip.Dispose();
                if (sectionFont != null) sectionFont.Dispose();
                if (valueFont != null) valueFont.Dispose();
                if (smallFont != null) smallFont.Dispose();
                if (tinyFont != null) tinyFont.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    internal sealed class LaunchPadForm : Form
    {
        private bool active;

        public LaunchPadForm()
        {
            Text = "LaunchPad";
            Size = new Size(258, 148);
            MinimumSize = Size;
            MaximumSize = Size;
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(248, 252, 255);
            Opacity = 0.9;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            UpdateWindowRegion();
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW |
                              NativeMethods.WS_EX_NOACTIVATE |
                              NativeMethods.WS_EX_TRANSPARENT;
                return cp;
            }
        }

        public void SetActive(bool value)
        {
            if (active == value)
            {
                return;
            }

            active = value;
            Invalidate();
        }

        public bool AcceptsBall(Point screenCenter)
        {
            Rectangle bounds = Bounds;
            double dx = (screenCenter.X - (bounds.Left + bounds.Width / 2.0)) / (bounds.Width * 0.42);
            double dy = (screenCenter.Y - (bounds.Top + bounds.Height / 2.0)) / (bounds.Height * 0.42);
            return dx * dx + dy * dy <= 1.0;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateWindowRegion();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle rect = new Rectangle(3, 3, Width - 7, Height - 7);
            using (GraphicsPath shell = CreateRoundedRectangle(rect, 28))
            using (LinearGradientBrush background = new LinearGradientBrush(rect,
                       active ? Color.FromArgb(252, 255, 255) : Color.FromArgb(252, 253, 255),
                       active ? Color.FromArgb(218, 243, 251) : Color.FromArgb(232, 242, 247),
                       LinearGradientMode.Vertical))
            using (Pen edge = new Pen(active ? Color.FromArgb(220, 31, 158, 214) : Color.FromArgb(145, 139, 184, 202), 1.2f))
            {
                g.FillPath(background, shell);
                g.DrawPath(edge, shell);
            }

            PointF center = new PointF(Width / 2.0f, 76.0f);
            DrawEnergyRings(g, center);
            DrawPadBase(g);
        }

        private void DrawPadBase(Graphics g)
        {
            RectangleF baseRect = new RectangleF(57, 89, 144, 31);
            using (Brush shadow = new SolidBrush(Color.FromArgb(32, 39, 110, 145)))
            using (LinearGradientBrush baseBrush = new LinearGradientBrush(
                       baseRect, active ? Color.FromArgb(235, 72, 190, 225) : Color.FromArgb(210, 137, 193, 211),
                       active ? Color.FromArgb(245, 24, 130, 190) : Color.FromArgb(225, 86, 145, 177),
                       LinearGradientMode.Vertical))
            using (Brush inner = new SolidBrush(active ? Color.FromArgb(245, 222, 250, 255) : Color.FromArgb(235, 237, 248, 251)))
            using (Pen baseEdge = new Pen(Color.FromArgb(active ? 220 : 150, 45, 128, 169), 1.0f))
            {
                g.FillEllipse(shadow, 51, 94, 156, 31);
                g.FillEllipse(baseBrush, baseRect);
                g.DrawEllipse(baseEdge, baseRect);
                g.FillEllipse(inner, 76, 94, 106, 17);
                g.DrawEllipse(baseEdge, 76, 94, 106, 17);
            }

            using (Pen slot = new Pen(Color.FromArgb(active ? 230 : 130, 24, 145, 196), 2.2f))
            {
                slot.StartCap = LineCap.Round;
                slot.EndCap = LineCap.Round;
                g.DrawLine(slot, 103, 102, 155, 102);
            }
        }

        private void DrawEnergyRings(Graphics g, PointF center)
        {
            RectangleF beamArea = new RectangleF(center.X - 34, 24, 68, 75);
            using (LinearGradientBrush beam = new LinearGradientBrush(
                       beamArea, Color.FromArgb(0, 65, 183, 222),
                       Color.FromArgb(active ? 38 : 20, 65, 183, 222), LinearGradientMode.Vertical))
            {
                g.FillRectangle(beam, beamArea);
            }

            RectangleF outer = new RectangleF(center.X - 78, center.Y - 35, 156, 66);
            RectangleF inner = new RectangleF(center.X - 49, center.Y - 21, 98, 42);
            using (Pen ring = new Pen(Color.FromArgb(active ? 220 : 130, 38, 157, 205), active ? 2.2f : 1.5f))
            using (Pen fine = new Pen(Color.FromArgb(active ? 180 : 100, 85, 174, 199), 1.0f))
            using (Pen chevron = new Pen(Color.FromArgb(active ? 220 : 120, 42, 150, 190), 1.8f))
            {
                ring.StartCap = LineCap.Round;
                ring.EndCap = LineCap.Round;
                chevron.StartCap = LineCap.Round;
                chevron.EndCap = LineCap.Round;
                g.DrawArc(ring, outer, 190, 72);
                g.DrawArc(ring, outer, 278, 72);
                g.DrawArc(ring, outer, 10, 72);
                g.DrawArc(ring, outer, 98, 72);
                g.DrawEllipse(fine, inner);
                g.DrawLine(chevron, center.X - 8, 42, center.X, 34);
                g.DrawLine(chevron, center.X, 34, center.X + 8, 42);
                g.DrawLine(chevron, center.X - 5, 51, center.X, 46);
                g.DrawLine(chevron, center.X, 46, center.X + 5, 51);
            }
        }

        private void DrawPadClouds(Graphics g)
        {
            int y = Height - 35;
            using (Brush cloud = new SolidBrush(Color.FromArgb(238, 250, 255)))
            using (Brush shade = new SolidBrush(Color.FromArgb(210, 229, 242, 251)))
            {
                g.FillEllipse(cloud, 43, y + 8, 52, 28);
                g.FillEllipse(cloud, 82, y, 56, 36);
                g.FillEllipse(cloud, 127, y + 7, 62, 30);
                g.FillRectangle(cloud, 48, y + 20, 142, 30);
                g.FillEllipse(shade, 131, y + 20, 58, 18);
            }
        }

        private void UpdateWindowRegion()
        {
            if (Width <= 0 || Height <= 0)
            {
                return;
            }

            using (GraphicsPath path = CreateRoundedRectangle(new Rectangle(0, 0, Width, Height), 46))
            {
                Region oldRegion = Region;
                Region = new Region(path);
                if (oldRegion != null)
                {
                    oldRegion.Dispose();
                }
            }
        }

        private static GraphicsPath CreateRoundedRectangle(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;
            Rectangle arc = new Rectangle(rect.Location, new Size(diameter, diameter));
            path.AddArc(arc, 180, 90);
            arc.X = rect.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = rect.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = rect.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class DayAppRow
    {
        public long Rx;
        public long Tx;
    }

    internal sealed class DayEntry
    {
        public long NicRx;
        public long NicTx;
        public readonly Dictionary<string, DayAppRow> Apps = new Dictionary<string, DayAppRow>();
    }

    internal struct DayPoint
    {
        public readonly long Date;
        public readonly long Rx;
        public readonly long Tx;

        public DayPoint(long date, long rx, long tx)
        {
            Date = date;
            Rx = rx;
            Tx = tx;
        }
    }

    internal sealed class AppRankRow
    {
        public readonly string Name;
        public readonly long Rx;
        public readonly long Tx;

        public AppRankRow(string name, long rx, long tx)
        {
            Name = name;
            Rx = rx;
            Tx = tx;
        }

        public long Total
        {
            get { return Rx + Tx; }
        }
    }

    // Keeps every per-day traffic counter in memory and mirrors it to two tiny
    // append-only files under %LOCALAPPDATA%\NetSpeedBall:
    //   traffic-archive.bin  - compacted per-day records, written once a day on rollover
    //   traffic-pending.bin  - recent deltas, rewritten (compacted) on every rollover/exit
    // A full year of heavy use stays well under 1 MB on disk.
    internal sealed class TrafficStore
    {
        private const int ArchiveMagic = 0x4142534E; // "NSBA"
        private const int PendingMagic = 0x5042534E; // "NSBP"
        private const short FormatVersion = 1;
        private const byte RecordKindNic = 0;
        private const byte RecordKindApp = 1;
        private const int MaxNameBytes = 512;

        /// <summary>
        /// 单条历史记录允许的最大字节数（1 PB）。
        /// 只是用来挡住"把未初始化内存当成流量"写进来的垃圾记录 —— 见 AppTrafficMonitor。
        /// </summary>
        private const long MaxPlausibleRecord = 1L << 50;

        private readonly object gate = new object();
        private readonly string archivePath;
        private readonly string pendingPath;
        private readonly Dictionary<long, DayEntry> days = new Dictionary<long, DayEntry>();
        // Flushed net totals: "date\x01app" -> {rx, tx}. Empty app name = NIC-level total.
        private readonly Dictionary<string, long[]> flushed = new Dictionary<string, long[]>();
        private long currentDate;

        public TrafficStore()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NetSpeedBall");
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch
            {
            }

            archivePath = Path.Combine(dir, "traffic-archive.bin");
            pendingPath = Path.Combine(dir, "traffic-pending.bin");
            Load();
        }

        public static long ToDateInt(DateTime day)
        {
            return day.Year * 10000L + day.Month * 100L + day.Day;
        }

        public static DateTime FromDateInt(long value)
        {
            return new DateTime((int)(value / 10000L), (int)(value / 100L % 100L), (int)(value % 100L));
        }

        public static long DaysBetween(long fromInt, long toInt)
        {
            return (long)(FromDateInt(toInt).Date - FromDateInt(fromInt).Date).TotalDays;
        }

        public void RecordNic(DateTime day, long rx, long tx)
        {
            if (rx <= 0 && tx <= 0)
            {
                return;
            }

            lock (gate)
            {
                DayEntry entry = GetOrCreateLocked(ToDateInt(day));
                entry.NicRx += Math.Max(0, rx);
                entry.NicTx += Math.Max(0, tx);
            }
        }

        public void RecordApp(DateTime day, string app, long rx, long tx)
        {
            if (string.IsNullOrEmpty(app) || (rx <= 0 && tx <= 0))
            {
                return;
            }

            lock (gate)
            {
                DayEntry entry = GetOrCreateLocked(ToDateInt(day));
                DayAppRow row;
                if (!entry.Apps.TryGetValue(app, out row))
                {
                    row = new DayAppRow();
                    entry.Apps.Add(app, row);
                }

                row.Rx += Math.Max(0, rx);
                row.Tx += Math.Max(0, tx);
            }
        }

        // Must be called once per sampling tick; archives yesterday's data the first
        // time a new calendar day is seen (00:00 rollover).
        public void CheckDayRoll()
        {
            long today = ToDateInt(DateTime.Today);
            lock (gate)
            {
                if (today == currentDate)
                {
                    return;
                }

                FlushLocked();
                ArchivePendingLocked();
                currentDate = today;
            }
        }

        public void Flush()
        {
            lock (gate)
            {
                FlushLocked();
            }
        }

        public void Close()
        {
            lock (gate)
            {
                FlushLocked();
                ArchivePendingLocked();
            }
        }

        public void ResetToday()
        {
            lock (gate)
            {
                long today = ToDateInt(DateTime.Today);
                // 先把文件改好，成功了再动内存。反过来做的话，一旦文件那一步失败（被别的
                // 程序占着、磁盘满、权限变了），内存已经清空而文件还留着今天的数据，
                // 重启后今天的流量会从文件里复活，而内存里已经查不到了。
                if (!RewritePendingWithoutDayLocked(today))
                {
                    return;
                }

                days.Remove(today);
                flushed.Remove(KeyOf(today, string.Empty));
                List<string> stale = new List<string>();
                foreach (KeyValuePair<string, long[]> pair in flushed)
                {
                    if (ExtractDate(pair.Key) == today)
                    {
                        stale.Add(pair.Key);
                    }
                }

                for (int i = 0; i < stale.Count; i++)
                {
                    flushed.Remove(stale[i]);
                }
            }
        }

        public long[] GetDayTotals(long dateInt)
        {
            lock (gate)
            {
                DayEntry entry;
                if (!days.TryGetValue(dateInt, out entry))
                {
                    return new long[] { 0, 0 };
                }

                return new long[] { entry.NicRx, entry.NicTx };
            }
        }

        public long[] GetRangeTotals(long fromInt, long toInt)
        {
            lock (gate)
            {
                long rx = 0;
                long tx = 0;
                foreach (KeyValuePair<long, DayEntry> pair in days)
                {
                    if (pair.Key >= fromInt && pair.Key <= toInt)
                    {
                        rx += pair.Value.NicRx;
                        tx += pair.Value.NicTx;
                    }
                }

                return new long[] { rx, tx };
            }
        }

        public List<DayPoint> GetRangeDays(long fromInt, long toInt)
        {
            List<DayPoint> result = new List<DayPoint>();
            if (toInt < fromInt)
            {
                return result;
            }

            int count = (int)DaysBetween(fromInt, toInt) + 1;
            if (count > 4000)
            {
                count = 4000;
                toInt = ToDateInt(FromDateInt(fromInt).AddDays(count - 1));
            }

            lock (gate)
            {
                for (int i = 0; i < count; i++)
                {
                    long dateInt = ToDateInt(FromDateInt(fromInt).AddDays(i));
                    DayEntry entry;
                    long rx = 0;
                    long tx = 0;
                    if (days.TryGetValue(dateInt, out entry))
                    {
                        rx = entry.NicRx;
                        tx = entry.NicTx;
                    }

                    result.Add(new DayPoint(dateInt, rx, tx));
                }
            }

            return result;
        }

        public List<AppRankRow> GetRangeRanks(long fromInt, long toInt, int maxRows)
        {
            Dictionary<string, long[]> totals = new Dictionary<string, long[]>();
            lock (gate)
            {
                foreach (KeyValuePair<long, DayEntry> pair in days)
                {
                    if (pair.Key < fromInt || pair.Key > toInt)
                    {
                        continue;
                    }

                    foreach (KeyValuePair<string, DayAppRow> app in pair.Value.Apps)
                    {
                        long[] agg;
                        if (!totals.TryGetValue(app.Key, out agg))
                        {
                            agg = new long[2];
                            totals.Add(app.Key, agg);
                        }

                        agg[0] += app.Value.Rx;
                        agg[1] += app.Value.Tx;
                    }
                }
            }

            List<AppRankRow> rows = new List<AppRankRow>(totals.Count);
            foreach (KeyValuePair<string, long[]> pair in totals)
            {
                rows.Add(new AppRankRow(pair.Key, pair.Value[0], pair.Value[1]));
            }

            rows.Sort(delegate(AppRankRow a, AppRankRow b) { return b.Total.CompareTo(a.Total); });
            if (rows.Count > maxRows)
            {
                rows.RemoveRange(maxRows, rows.Count - maxRows);
            }

            return rows;
        }

        private DayEntry GetOrCreateLocked(long dateInt)
        {
            DayEntry entry;
            if (!days.TryGetValue(dateInt, out entry))
            {
                entry = new DayEntry();
                days.Add(dateInt, entry);
            }

            return entry;
        }

        private static string KeyOf(long dateInt, string app)
        {
            return dateInt.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\u0001" + app;
        }

        private static long ExtractDate(string key)
        {
            int sep = key.IndexOf('\u0001');
            long value;
            if (sep > 0 && long.TryParse(key.Substring(0, sep),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                return value;
            }

            return 0;
        }

        private long GetFlushedLocked(long dateInt, string app, int index)
        {
            long[] values;
            if (flushed.TryGetValue(KeyOf(dateInt, app), out values) && values.Length == 2)
            {
                return values[index];
            }

            return 0;
        }

        private void SetFlushedLocked(long dateInt, string app, long rx, long tx)
        {
            flushed[KeyOf(dateInt, app)] = new long[] { rx, tx };
        }

        private struct PendingRecord
        {
            public byte Kind;
            public long Date;
            public string Name;
            public long Rx;
            public long Tx;
        }

        private void FlushLocked()
        {
            List<PendingRecord> diffs = new List<PendingRecord>();
            foreach (KeyValuePair<long, DayEntry> pair in days)
            {
                long dateInt = pair.Key;
                DayEntry entry = pair.Value;
                long nicRx = GetFlushedLocked(dateInt, string.Empty, 0);
                long nicTx = GetFlushedLocked(dateInt, string.Empty, 1);
                if (entry.NicRx > nicRx || entry.NicTx > nicTx)
                {
                    PendingRecord record = new PendingRecord();
                    record.Kind = RecordKindNic;
                    record.Date = dateInt;
                    record.Name = string.Empty;
                    record.Rx = Math.Max(0, entry.NicRx - nicRx);
                    record.Tx = Math.Max(0, entry.NicTx - nicTx);
                    diffs.Add(record);
                }

                foreach (KeyValuePair<string, DayAppRow> app in entry.Apps)
                {
                    long appRx = GetFlushedLocked(dateInt, app.Key, 0);
                    long appTx = GetFlushedLocked(dateInt, app.Key, 1);
                    if (app.Value.Rx > appRx || app.Value.Tx > appTx)
                    {
                        PendingRecord record = new PendingRecord();
                        record.Kind = RecordKindApp;
                        record.Date = dateInt;
                        record.Name = app.Key;
                        record.Rx = Math.Max(0, app.Value.Rx - appRx);
                        record.Tx = Math.Max(0, app.Value.Tx - appTx);
                        diffs.Add(record);
                    }
                }
            }

            if (diffs.Count == 0)
            {
                return;
            }

            try
            {
                // 上一次崩溃可能在文件尾部留下半个记录。读侧遇到坏记录就停，如果直接
                // 往后追加，坏尾之后写入的记录会永远读不出来，所以先把坏尾切掉。
                TrimBadTail(pendingPath, PendingMagic);

                using (FileStream stream = new FileStream(pendingPath, FileMode.Append,
                    FileAccess.Write, FileShare.Read))
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    if (stream.Length == 0)
                    {
                        writer.Write(PendingMagic);
                        writer.Write(FormatVersion);
                    }

                    for (int i = 0; i < diffs.Count; i++)
                    {
                        WriteRecord(writer, diffs[i]);
                    }

                    writer.Flush();
                    // Flush(true) 才会真正落到磁盘；只调 BinaryWriter.Flush 是留在系统缓存里，
                    // 断电就会丢掉已写入的记录。
                    stream.Flush(true);
                }
            }
            catch
            {
                // Disk issues must never take the floating ball down.
                return;
            }

            for (int i = 0; i < diffs.Count; i++)
            {
                PendingRecord record = diffs[i];
                DayEntry entry;
                long rx = 0;
                long tx = 0;
                if (record.Kind == RecordKindNic)
                {
                    if (days.TryGetValue(record.Date, out entry))
                    {
                        rx = entry.NicRx;
                        tx = entry.NicTx;
                    }
                }
                else
                {
                    if (days.TryGetValue(record.Date, out entry))
                    {
                        DayAppRow row;
                        if (entry.Apps.TryGetValue(record.Name, out row))
                        {
                            rx = row.Rx;
                            tx = row.Tx;
                        }
                    }
                }

                SetFlushedLocked(record.Date, record.Name, rx, tx);
            }
        }

        // Folds the pending log into the archive file, then compacts the pending log
        // back to an empty header. Keeps both files small forever.
        private void ArchivePendingLocked()
        {
            Dictionary<string, PendingRecord> sums = new Dictionary<string, PendingRecord>();
            if (!ReadPendingInto(sums))
            {
                // pending 读不出来（文件头不对，或者有坏记录）：绝不能在这里把它清空 ——
                // 那会把坏记录之后所有完好记录一起删掉。原样留着，本轮不归档。
                return;
            }

            try
            {
                if (sums.Count > 0)
                {
                    TrimBadTail(archivePath, ArchiveMagic);
                    using (FileStream stream = new FileStream(archivePath, FileMode.Append,
                        FileAccess.Write, FileShare.Read))
                    using (BinaryWriter writer = new BinaryWriter(stream))
                    {
                        if (stream.Length == 0)
                        {
                            writer.Write(ArchiveMagic);
                            writer.Write(FormatVersion);
                        }

                        foreach (KeyValuePair<string, PendingRecord> pair in sums)
                        {
                            WriteRecord(writer, pair.Value);
                        }

                        writer.Flush();
                        stream.Flush(true);
                    }
                }

                using (FileStream stream = new FileStream(pendingPath, FileMode.Create,
                    FileAccess.Write, FileShare.Read))
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    writer.Write(PendingMagic);
                    writer.Write(FormatVersion);
                    writer.Flush();
                    stream.Flush(true);
                }
            }
            catch
            {
                return;
            }

            RebuildFlushedLocked();
        }

        // 返回 true 表示 pending 被完整读出来了（或者本来就不存在），调用方可以安全清空它；
        // 返回 false 表示文件头不对或有坏记录，调用方绝不能清空它。
        private bool ReadPendingInto(Dictionary<string, PendingRecord> sums)
        {
            if (!File.Exists(pendingPath))
            {
                return true;
            }

            try
            {
                using (FileStream stream = new FileStream(pendingPath, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite))
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    if (stream.Length < 6)
                    {
                        // 空文件或半个文件头，没有可挽救的内容。
                        return true;
                    }

                    if (reader.ReadInt32() != PendingMagic || reader.ReadInt16() != FormatVersion)
                    {
                        return false;
                    }

                    // 最小记录是 1+4+2+0+8+8 = 23 字节；原来写的是 15，会让循环去读一条
                    // 根本装不下的记录。
                    while (stream.Position + 23 <= stream.Length)
                    {
                        PendingRecord record;
                        if (!TryReadRecord(reader, out record))
                        {
                            return false;
                        }

                        // 垃圾记录（未初始化内存当流量写进来的）不再折进归档，免得永远留在历史里
                        if (record.Rx > MaxPlausibleRecord || record.Tx > MaxPlausibleRecord)
                        {
                            continue;
                        }

                        string key = record.Kind.ToString(
                            System.Globalization.CultureInfo.InvariantCulture) + "\u0001" +
                            record.Date.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                            "\u0001" + record.Name;
                        PendingRecord sum;
                        if (sums.TryGetValue(key, out sum))
                        {
                            sum.Rx += record.Rx;
                            sum.Tx += record.Tx;
                            sums[key] = sum;
                        }
                        else
                        {
                            sums.Add(key, record);
                        }
                    }
                }

                return true;
            }
            catch
            {
                // A partially written tail is ignored; the loader reads complete records only.
                return false;
            }
        }

        // 把 pending 里属于 removedDay 的记录去掉，其余原样保留。
        // 成功返回 true；只要有任何不确定就返回 false，并且一个字节都不改。
        private bool RewritePendingWithoutDayLocked(long removedDay)
        {
            List<PendingRecord> keep = new List<PendingRecord>();
            try
            {
                if (File.Exists(pendingPath))
                {
                    using (FileStream stream = new FileStream(pendingPath, FileMode.Open,
                        FileAccess.Read, FileShare.ReadWrite))
                    using (BinaryReader reader = new BinaryReader(stream))
                    {
                        if (stream.Length < 6 ||
                            reader.ReadInt32() != PendingMagic ||
                            reader.ReadInt16() != FormatVersion)
                        {
                            // 文件头不对：宁可什么都不做，也不要把它整个覆盖掉。
                            return false;
                        }

                        while (stream.Position + 23 <= stream.Length)
                        {
                            PendingRecord record;
                            if (!TryReadRecord(reader, out record))
                            {
                                // 有坏记录：同样不做任何改动。
                                return false;
                            }

                            if (record.Date != removedDay)
                            {
                                keep.Add(record);
                            }
                        }
                    }
                }

                // 先写临时文件、再原子替换：避免写到一半崩溃把 pending 弄坏。
                string tempPath = pendingPath + ".tmp";
                using (FileStream stream = new FileStream(tempPath, FileMode.Create,
                    FileAccess.Write, FileShare.Read))
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    writer.Write(PendingMagic);
                    writer.Write(FormatVersion);
                    for (int i = 0; i < keep.Count; i++)
                    {
                        WriteRecord(writer, keep[i]);
                    }

                    writer.Flush();
                    stream.Flush(true);
                }

                if (File.Exists(pendingPath))
                {
                    File.Replace(tempPath, pendingPath, null);
                }
                else
                {
                    File.Move(tempPath, pendingPath);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void RebuildFlushedLocked()
        {
            flushed.Clear();
            foreach (KeyValuePair<long, DayEntry> pair in days)
            {
                SetFlushedLocked(pair.Key, string.Empty, pair.Value.NicRx, pair.Value.NicTx);
                foreach (KeyValuePair<string, DayAppRow> app in pair.Value.Apps)
                {
                    SetFlushedLocked(pair.Key, app.Key, app.Value.Rx, app.Value.Tx);
                }
            }
        }

        private void Load()
        {
            LoadFile(archivePath, ArchiveMagic);
            LoadFile(pendingPath, PendingMagic);
            currentDate = ToDateInt(DateTime.Today);
            RebuildFlushedLocked();
        }

        private void LoadFile(string path, int expectedMagic)
        {
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite))
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    if (stream.Length < 6)
                    {
                        return;
                    }

                    if (reader.ReadInt32() != expectedMagic || reader.ReadInt16() != FormatVersion)
                    {
                        return;
                    }

                    while (stream.Position + 23 <= stream.Length)
                    {
                        PendingRecord record;
                        if (!TryReadRecord(reader, out record))
                        {
                            return;
                        }

                        // 垃圾记录（见 MaxPlausibleRecord）读进来只会污染排行榜，跳过它继续读下一条
                        if (record.Rx > MaxPlausibleRecord || record.Tx > MaxPlausibleRecord)
                        {
                            continue;
                        }

                        DayEntry entry = GetOrCreateLocked(record.Date);
                        if (record.Kind == RecordKindNic)
                        {
                            entry.NicRx += record.Rx;
                            entry.NicTx += record.Tx;
                        }
                        else
                        {
                            DayAppRow row;
                            if (!entry.Apps.TryGetValue(record.Name, out row))
                            {
                                row = new DayAppRow();
                                entry.Apps.Add(record.Name, row);
                            }

                            row.Rx += record.Rx;
                            row.Tx += record.Tx;
                        }
                    }
                }
            }
            catch
            {
                // Corrupt or locked files degrade to whatever was loaded so far.
            }
        }

        private static bool TryReadRecord(BinaryReader reader, out PendingRecord record)
        {
            record = new PendingRecord();
            // 最小记录 1+4+2+0+8+8 = 23 字节。先确认文件里还剩得下，避免读到文件尾时
            // 抛异常——异常会把调用方整个 try 打断，后面本来能读的记录也一起丢掉。
            if (reader.BaseStream.Length - reader.BaseStream.Position < 23)
            {
                return false;
            }

            record.Kind = reader.ReadByte();
            record.Date = reader.ReadInt32();
            int nameLength = reader.ReadUInt16();
            if (record.Date <= 19000101 || record.Date >= 22000101 || nameLength > MaxNameBytes)
            {
                return false;
            }

            // 声明的名字长度超出了文件尾，说明这是个被截断的记录。
            if (reader.BaseStream.Length - reader.BaseStream.Position < nameLength + 16)
            {
                return false;
            }

            record.Name = nameLength > 0 ? Encoding.UTF8.GetString(reader.ReadBytes(nameLength)) : string.Empty;
            record.Rx = reader.ReadInt64();
            record.Tx = reader.ReadInt64();
            if (record.Rx < 0 || record.Tx < 0)
            {
                return false;
            }

            return true;
        }

        // 从头解析，返回最后一个完整记录结束的偏移（含 6 字节文件头）。
        // 遇到第一个不完整的记录就停下 —— 它后面的内容按现有读法本来就读不出来。
        private static long ScanGoodLength(byte[] data, int magic)
        {
            if (data.Length < 6)
            {
                return 0;
            }

            if (BitConverter.ToInt32(data, 0) != magic || BitConverter.ToInt16(data, 4) != FormatVersion)
            {
                return 0;
            }

            int offset = 6;
            while (offset + 23 <= data.Length)
            {
                int date = BitConverter.ToInt32(data, offset + 1);
                int nameLength = BitConverter.ToUInt16(data, offset + 5);
                if (date <= 19000101 || date >= 22000101 || nameLength > MaxNameBytes)
                {
                    break;
                }

                int end = offset + 7 + nameLength + 16;
                if (end > data.Length)
                {
                    break;
                }

                if (BitConverter.ToInt64(data, offset + 7 + nameLength) < 0 ||
                    BitConverter.ToInt64(data, offset + 7 + nameLength + 8) < 0)
                {
                    break;
                }

                offset = end;
            }

            return offset;
        }

        // 把文件尾部那个"写了一半"的记录切掉。不切的话，之后追加的记录会排在坏记录
        // 后面，而读侧遇到坏记录就停 —— 那些新记录将永远读不出来（这正是"一次损坏
        // 永久毒化整个文件"的原因）。
        // 只在"文件头完好、且确实存在坏尾"时才动手，其余情况一个字节都不碰。
        private static void TrimBadTail(string path, int magic)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                byte[] data = File.ReadAllBytes(path);
                long good = ScanGoodLength(data, magic);
                if (good < 6 || good >= data.Length)
                {
                    return;
                }

                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    stream.SetLength(good);
                    stream.Flush(true);
                }
            }
            catch
            {
                // 切坏尾失败不影响主流程。
            }
        }

        private static void WriteRecord(BinaryWriter writer, PendingRecord record)
        {
            byte[] nameBytes = string.IsNullOrEmpty(record.Name)
                ? new byte[0]
                : Encoding.UTF8.GetBytes(record.Name);
            if (nameBytes.Length > MaxNameBytes)
            {
                byte[] trimmed = new byte[MaxNameBytes];
                Array.Copy(nameBytes, trimmed, MaxNameBytes);
                nameBytes = trimmed;
            }

            writer.Write(record.Kind);
            writer.Write((int)record.Date);
            writer.Write((ushort)nameBytes.Length);
            if (nameBytes.Length > 0)
            {
                writer.Write(nameBytes);
            }

            writer.Write(record.Rx);
            writer.Write(record.Tx);
        }
    }

    // Attributes per-connection TCP byte deltas to the owning process, once per
    // second, using GetExtendedTcpTable + GetPerTcpConnectionEStats (the same
    // mechanism Resource Monitor uses; no admin rights required).
    internal sealed class AppTrafficMonitor
    {
        // 连接记录只在闲置超时后清理：如果每轮都重建，某轮没读到某条连接就会被
        // 当成"新连接"重新整笔计入，流量会翻倍。
        private static readonly TimeSpan FlowIdleTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan NameCacheLifetime = TimeSpan.FromSeconds(60);

        private readonly TrafficStore store;
        private readonly Dictionary<TcpFlowKey, FlowState> flows = new Dictionary<TcpFlowKey, FlowState>();
        private readonly Dictionary<int, ProcessNameEntry> processNames = new Dictionary<int, ProcessNameEntry>();
        private byte[] v4Buffer = new byte[0];
        private byte[] v6Buffer = new byte[0];
        private IntPtr statsBuffer;
        private IntPtr enableBuffer;
        private readonly byte[] zeroStats = new byte[NativeMethods.TcpEstatsDataSize];
        private int ticksSincePrune;

        /// <summary>
        /// 单条连接/单条记录能有的最大字节数（1 PB）。
        ///
        /// 超过这个量级只可能有一个来源：GetPerTcpConnectionEStats 没有真正写入缓冲区，
        /// 我们从 AllocHGlobal 里读到了残留的堆内存 —— 曾经因此把 4.3×10^18 字节
        /// 当成 node 的下载量写进了历史（排行榜里显示成 4060405727 GB）。
        /// </summary>
        private const long MaxPlausibleBytes = 1L << 50;

        private sealed class FlowState
        {
            public long InBytes;
            public long OutBytes;
            public DateTime LastSeenUtc;
            public bool Enabled;
            public bool HasBaseline;
        }

        private sealed class ProcessNameEntry
        {
            public string Name;
            public DateTime ExpiresUtc;
        }

        public AppTrafficMonitor(TrafficStore trafficStore)
        {
            store = trafficStore;
            statsBuffer = Marshal.AllocHGlobal(NativeMethods.TcpEstatsDataSize);
            enableBuffer = Marshal.AllocHGlobal(NativeMethods.TcpEstatsDataRwSize);
            Marshal.WriteByte(enableBuffer, 1); // EnableCollection = TRUE
        }

        // 每条连接的字节统计默认关闭，必须先向系统申请启用，而该申请只对管理员放行。
        // 界面用它来决定是显示排行榜还是显示"需要管理员权限"的说明。
        public static bool CanCollectAppTraffic
        {
            get { return NativeMethods.IsUserAnAdmin(); }
        }

        public void Sample()
        {
            SampleFamily(NativeMethods.AddressFamilyInet);
            SampleFamily(NativeMethods.AddressFamilyInet6);

            if (++ticksSincePrune >= 60)
            {
                ticksSincePrune = 0;
                PruneFlows();
            }

            store.CheckDayRoll();
        }

        // 连接结束后它的记录会一直留着，这里定期回收，防止长跑之后字典无限膨胀。
        private void PruneFlows()
        {
            DateTime flowDeadline = DateTime.UtcNow - FlowIdleTimeout;
            List<TcpFlowKey> staleFlows = new List<TcpFlowKey>();
            foreach (KeyValuePair<TcpFlowKey, FlowState> pair in flows)
            {
                if (pair.Value.LastSeenUtc < flowDeadline)
                {
                    staleFlows.Add(pair.Key);
                }
            }

            for (int i = 0; i < staleFlows.Count; i++)
            {
                flows.Remove(staleFlows[i]);
            }

            DateTime nameDeadline = DateTime.UtcNow;
            List<int> staleNames = new List<int>();
            foreach (KeyValuePair<int, ProcessNameEntry> pair in processNames)
            {
                if (pair.Value.ExpiresUtc <= nameDeadline)
                {
                    staleNames.Add(pair.Key);
                }
            }

            for (int i = 0; i < staleNames.Count; i++)
            {
                processNames.Remove(staleNames[i]);
            }
        }

        private void SampleFamily(int family)
        {
            int size = 0;
            uint ret = NativeMethods.GetExtendedTcpTable(null, ref size, false, family,
                NativeMethods.TcpTableOwnerPidAll, 0);
            if (ret != NativeMethods.ErrorInsufficientBuffer || size <= 4)
            {
                return;
            }

            byte[] buffer = family == NativeMethods.AddressFamilyInet
                ? EnsureBuffer(ref v4Buffer, size)
                : EnsureBuffer(ref v6Buffer, size);
            if (buffer.Length < size)
            {
                return;
            }

            ret = NativeMethods.GetExtendedTcpTable(buffer, ref size, false, family,
                NativeMethods.TcpTableOwnerPidAll, 0);
            if (ret != 0 || size < 4 || size > buffer.Length)
            {
                return;
            }

            int rowSize = family == NativeMethods.AddressFamilyInet ? 24 : 56;
            int count = BitConverter.ToInt32(buffer, 0);
            int maxRows = (size - 4) / rowSize;
            if (count > maxRows)
            {
                count = maxRows;
            }

            DateTime now = DateTime.UtcNow;
            // 表里的行是 MIB_TCPROW_OWNER_PID / MIB_TCP6ROW_OWNER_PID。
            // v4 的 OWNER_PID 行前 20 字节与 EStats 要的 MIB_TCPROW 完全一致，可以直接传；
            // v6 的 OWNER_PID 行（State 在行尾）与 EStats 要的 MIB_TCP6ROW（State 在行首）
            // 不前缀兼容，必须先搬进一个 52 字节的 MIB_TCP6ROW，否则 API 认不出这条连接。
            int pidOffset = family == NativeMethods.AddressFamilyInet ? 20 : 52;

            GCHandle pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            IntPtr eStatsRow = IntPtr.Zero;
            try
            {
                IntPtr basePointer = pinned.AddrOfPinnedObject();
                if (family != NativeMethods.AddressFamilyInet)
                {
                    eStatsRow = Marshal.AllocHGlobal(NativeMethods.Tcp6RowSize);
                }

                for (int i = 0; i < count; i++)
                {
                    int offset = 4 + i * rowSize;
                    TcpFlowKey key = family == NativeMethods.AddressFamilyInet
                        ? ReadV4Key(buffer, offset)
                        : ReadV6Key(buffer, offset);

                    IntPtr rowPointer;
                    if (family == NativeMethods.AddressFamilyInet)
                    {
                        rowPointer = new IntPtr(basePointer.ToInt64() + offset);
                    }
                    else
                    {
                        CopyV6Row(buffer, offset, eStatsRow);
                        rowPointer = eStatsRow;
                    }

                    FlowState state;
                    if (!flows.TryGetValue(key, out state))
                    {
                        state = new FlowState();
                        state.Enabled = TryEnableStats(rowPointer, family);
                        flows.Add(key, state);
                    }

                    state.LastSeenUtc = now;
                    if (!state.Enabled)
                    {
                        // 申请启用被系统拒绝（普通权限），这条连接读不到字节数。
                        continue;
                    }

                    // 每次调用前把缓冲区清零：API 返回成功但没写满时，读到的就是 0 而不是旧堆数据。
                    Marshal.Copy(zeroStats, 0, statsBuffer, NativeMethods.TcpEstatsDataSize);

                    uint status = family == NativeMethods.AddressFamilyInet
                        ? NativeMethods.GetPerTcpConnectionEStats(rowPointer, NativeMethods.TcpEstatsTypeData,
                            IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0, statsBuffer, 0, NativeMethods.TcpEstatsDataSize)
                        : NativeMethods.GetPerTcp6ConnectionEStats(rowPointer, NativeMethods.TcpEstatsTypeData,
                            IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0, statsBuffer, 0, NativeMethods.TcpEstatsDataSize);
                    if (status != 0)
                    {
                        continue;
                    }

                    long inBytes = Marshal.ReadInt64(statsBuffer, 16);  // DataBytesIn
                    long outBytes = Marshal.ReadInt64(statsBuffer, 0);  // DataBytesOut

                    // 这两个数只要有一个不像话，说明这一拍根本没读到真数据（见 MaxPlausibleBytes），
                    // 丢掉它并重建基线 —— 宁可少记一拍，也不能把垃圾写进历史。
                    if (inBytes < 0 || outBytes < 0 ||
                        inBytes > MaxPlausibleBytes || outBytes > MaxPlausibleBytes)
                    {
                        state.HasBaseline = false;
                        continue;
                    }

                    long deltaIn;
                    long deltaOut;
                    if (!state.HasBaseline)
                    {
                        // 计数器是从连接建立就开始累计的，所以第一次读到的就是这条连接已经
                        // 发生的全部流量，整笔记下，几秒就结束的短连接才不会被白白丢掉。
                        deltaIn = inBytes;
                        deltaOut = outBytes;
                        state.HasBaseline = true;
                    }
                    else
                    {
                        deltaIn = inBytes >= state.InBytes ? inBytes - state.InBytes : inBytes;
                        deltaOut = outBytes >= state.OutBytes ? outBytes - state.OutBytes : outBytes;
                    }

                    state.InBytes = inBytes;
                    state.OutBytes = outBytes;
                    if (deltaIn <= 0 && deltaOut <= 0)
                    {
                        continue;
                    }

                    int pid = BitConverter.ToInt32(buffer, offset + pidOffset);
                    string app = ResolveName(pid);
                    if (app != null)
                    {
                        store.RecordApp(DateTime.Today, app, deltaIn, deltaOut);
                    }
                }
            }
            finally
            {
                if (eStatsRow != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(eStatsRow);
                }

                pinned.Free();
            }
        }

        private bool TryEnableStats(IntPtr rowPointer, int family)
        {
            uint status = family == NativeMethods.AddressFamilyInet
                ? NativeMethods.SetPerTcpConnectionEStats(rowPointer, NativeMethods.TcpEstatsTypeData,
                    enableBuffer, 0, NativeMethods.TcpEstatsDataRwSize, 0)
                : NativeMethods.SetPerTcp6ConnectionEStats(rowPointer, NativeMethods.TcpEstatsTypeData,
                    enableBuffer, 0, NativeMethods.TcpEstatsDataRwSize, 0);
            return status == 0;
        }

        private static TcpFlowKey ReadV4Key(byte[] buffer, int offset)
        {
            TcpFlowKey key = new TcpFlowKey();
            key.Local1 = BitConverter.ToUInt32(buffer, offset + 4);
            key.Local2 = 0;
            key.Remote1 = BitConverter.ToUInt32(buffer, offset + 12);
            key.Remote2 = 0;
            key.LocalPort = ReadNetworkPort(buffer, offset + 8);
            key.RemotePort = ReadNetworkPort(buffer, offset + 16);
            return key;
        }

        // 表里返回的是 MIB_TCP6ROW_OWNER_PID（State 在行尾），不是 MIB_TCP6ROW：
        //   0 LocalAddr(16) | 16 LocalScopeId(4) | 20 LocalPort | 24 RemoteAddr(16)
        //   | 40 RemoteScopeId(4) | 44 RemotePort | 48 State | 52 OwningPid
        private static TcpFlowKey ReadV6Key(byte[] buffer, int offset)
        {
            TcpFlowKey key = new TcpFlowKey();
            key.Local1 = (ulong)BitConverter.ToInt64(buffer, offset + 0);
            key.Local2 = (ulong)BitConverter.ToInt64(buffer, offset + 8);
            key.Remote1 = (ulong)BitConverter.ToInt64(buffer, offset + 24);
            key.Remote2 = (ulong)BitConverter.ToInt64(buffer, offset + 32);
            key.LocalPort = ReadNetworkPort(buffer, offset + 20);
            key.RemotePort = ReadNetworkPort(buffer, offset + 44);
            return key;
        }

        // 把表里的 MIB_TCP6ROW_OWNER_PID（56 字节，State 在行尾）搬成
        // Get/SetPerTcp6ConnectionEStats 要求的 MIB_TCP6ROW（52 字节，State 在行首）。
        // 源：0 LocalAddr | 16 LocalScopeId | 20 LocalPort | 24 RemoteAddr
        //     | 40 RemoteScopeId | 44 RemotePort | 48 State | 52 OwningPid
        // 目标：0 State | 4 LocalAddr | 20 LocalScopeId | 24 LocalPort
        //       | 28 RemoteAddr | 44 RemoteScopeId | 48 RemotePort
        private static void CopyV6Row(byte[] buffer, int offset, IntPtr destination)
        {
            Marshal.WriteInt32(destination, 0, BitConverter.ToInt32(buffer, offset + 48));
            Marshal.Copy(buffer, offset + 0, new IntPtr(destination.ToInt64() + 4), 16);
            Marshal.WriteInt32(destination, 20, BitConverter.ToInt32(buffer, offset + 16));
            Marshal.WriteInt32(destination, 24, BitConverter.ToInt32(buffer, offset + 20));
            Marshal.Copy(buffer, offset + 24, new IntPtr(destination.ToInt64() + 28), 16);
            Marshal.WriteInt32(destination, 44, BitConverter.ToInt32(buffer, offset + 40));
            Marshal.WriteInt32(destination, 48, BitConverter.ToInt32(buffer, offset + 44));
        }

        private static int ReadNetworkPort(byte[] buffer, int offset)
        {
            return (buffer[offset] << 8) | buffer[offset + 1];
        }

        private static byte[] EnsureBuffer(ref byte[] buffer, int size)
        {
            if (buffer.Length < size)
            {
                int capacity = size + 4096;
                byte[] grown = new byte[capacity];
                buffer = grown;
                return grown;
            }

            return buffer;
        }

        private string ResolveName(int pid)
        {
            if (pid <= 0)
            {
                return null;
            }

            if (pid == 4)
            {
                return "系统";
            }

            ProcessNameEntry entry;
            if (processNames.TryGetValue(pid, out entry) && entry.ExpiresUtc > DateTime.UtcNow)
            {
                return entry.Name.Length > 0 ? entry.Name : null;
            }

            string name;
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    name = process.ProcessName;
                }
            }
            catch
            {
                // 进程已经不在了。这条缓存只留很短时间，否则进程号被新进程复用后
                // 排行榜会一直顶着旧程序的名字。
                CacheName(pid, string.Empty, false);
                return null;
            }

            if (string.IsNullOrEmpty(name))
            {
                CacheName(pid, string.Empty, false);
                return null;
            }

            CacheName(pid, name, true);
            return name;
        }

        private void CacheName(int pid, string name, bool resolved)
        {
            ProcessNameEntry entry = new ProcessNameEntry();
            entry.Name = name;
            entry.ExpiresUtc = DateTime.UtcNow.Add(
                resolved ? NameCacheLifetime : TimeSpan.FromSeconds(20));
            processNames[pid] = entry;
        }

        public void Dispose()
        {
            if (statsBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(statsBuffer);
                statsBuffer = IntPtr.Zero;
            }

            if (enableBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(enableBuffer);
                enableBuffer = IntPtr.Zero;
            }
        }
    }

    internal struct TcpFlowKey : IEquatable<TcpFlowKey>
    {
        public ulong Local1;
        public ulong Local2;
        public ulong Remote1;
        public ulong Remote2;
        public int LocalPort;
        public int RemotePort;

        public bool Equals(TcpFlowKey other)
        {
            return Local1 == other.Local1 && Local2 == other.Local2 &&
                   Remote1 == other.Remote1 && Remote2 == other.Remote2 &&
                   LocalPort == other.LocalPort && RemotePort == other.RemotePort;
        }

        public override bool Equals(object obj)
        {
            return obj is TcpFlowKey && Equals((TcpFlowKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Local1.GetHashCode();
                hash = (hash * 397) ^ Local2.GetHashCode();
                hash = (hash * 397) ^ Remote1.GetHashCode();
                hash = (hash * 397) ^ Remote2.GetHashCode();
                hash = (hash * 397) ^ LocalPort;
                hash = (hash * 397) ^ RemotePort;
                return hash;
            }
        }
    }

    internal sealed class NetworkSpeedSampler
    {
        private long lastReceived;
        private long lastSent;
        private long lastSignature;
        private DateTime lastTime;
        private bool hasBaseline;
        private bool hasSmoothed;
        private double smoothedDownload;
        private double smoothedUpload;

        public void Reset()
        {
            AdapterTotals totals = ReadTotals();
            lastReceived = totals.ReceivedBytes;
            lastSent = totals.SentBytes;
            lastSignature = totals.Signature;
            lastTime = DateTime.UtcNow;
            hasBaseline = true;
            hasSmoothed = false;
            smoothedDownload = 0;
            smoothedUpload = 0;
        }

        public SpeedSample Next()
        {
            AdapterTotals totals = ReadTotals();
            DateTime now = DateTime.UtcNow;

            // 这一拍没读到可信的网卡计数（没有可用网卡，或读回来全是 0）。
            // 绝不能拿它当基线：否则下一拍真实计数回来时，会把网卡自开机以来的
            // 全部累计值当成"这一秒"的流量，凭空写进当日统计并落盘，不可逆。
            if (!totals.HasAnyAdapter || (totals.ReceivedBytes == 0 && totals.SentBytes == 0))
            {
                return new SpeedSample(0, 0, totals.HasAnyAdapter, 0, 0, 0);
            }

            // 选中的网卡集合变了（VPN 断开、有线/无线切换）：两批网卡的计数器不在
            // 同一个数轴上，相减会得到巨额假流量，所以重建基线而不是继续做差。
            if (!hasBaseline || totals.Signature != lastSignature)
            {
                lastReceived = totals.ReceivedBytes;
                lastSent = totals.SentBytes;
                lastSignature = totals.Signature;
                lastTime = now;
                hasBaseline = true;
                hasSmoothed = false;
                return new SpeedSample(0, 0, totals.HasAnyAdapter, 0, 0, 0);
            }

            double elapsed = Math.Max(0.2, (now - lastTime).TotalSeconds);
            long downBytes = totals.ReceivedBytes >= lastReceived ? totals.ReceivedBytes - lastReceived : 0;
            long upBytes = totals.SentBytes >= lastSent ? totals.SentBytes - lastSent : 0;

            lastReceived = totals.ReceivedBytes;
            lastSent = totals.SentBytes;
            lastTime = now;

            double rawDownload = downBytes / elapsed;
            double rawUpload = upBytes / elapsed;
            if (!hasSmoothed)
            {
                smoothedDownload = rawDownload;
                smoothedUpload = rawUpload;
                hasSmoothed = true;
            }
            else
            {
                // A short exponential average removes one-second counter jitter while
                // still converging quickly when traffic starts or stops.
                double alpha = 1.0 - Math.Exp(-elapsed / 2.0);
                smoothedDownload += (rawDownload - smoothedDownload) * alpha;
                smoothedUpload += (rawUpload - smoothedUpload) * alpha;
            }

            return new SpeedSample(smoothedDownload, smoothedUpload, totals.HasAnyAdapter,
                elapsed, downBytes, upBytes);
        }

        private static AdapterTotals ReadTotals()
        {
            long received = 0;
            long sent = 0;
            long signature = 0;
            bool any = false;

            NetworkInterface[] interfaces;
            try
            {
                interfaces = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch
            {
                return new AdapterTotals(0, 0, false);
            }

            // Mutually exclusive selection: when a VPN/tunnel adapter is up, count only
            // it (the same bytes otherwise appear on both the tunnel and the physical
            // NIC and would be double-counted). Without a tunnel, count physical NICs;
            // as a last resort, count anything up with a gateway.
            List<NetworkInterface> tunnelLike = new List<NetworkInterface>();
            List<NetworkInterface> physical = new List<NetworkInterface>();
            List<NetworkInterface> others = new List<NetworkInterface>();

            foreach (NetworkInterface adapter in interfaces)
            {
                if (adapter == null || adapter.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                NetworkInterfaceType type = adapter.NetworkInterfaceType;
                if (type == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                if (type == NetworkInterfaceType.Tunnel || type == NetworkInterfaceType.Ppp)
                {
                    // Tunnels such as WireGuard often publish /1 routes instead of a
                    // plain default gateway, so do not require a gateway here.
                    tunnelLike.Add(adapter);
                    continue;
                }

                if (!HasDefaultGateway(adapter))
                {
                    continue;
                }

                switch (type)
                {
                    case NetworkInterfaceType.Ethernet:
                    case NetworkInterfaceType.FastEthernetT:
                    case NetworkInterfaceType.FastEthernetFx:
                    case NetworkInterfaceType.GigabitEthernet:
                    case NetworkInterfaceType.Wireless80211:
                        physical.Add(adapter);
                        break;
                    default:
                        others.Add(adapter);
                        break;
                }
            }

            List<NetworkInterface> selected = tunnelLike.Count > 0
                ? tunnelLike
                : (physical.Count > 0 ? physical : others);
            any = selected.Count > 0;

            foreach (NetworkInterface adapter in selected)
            {
                // 集合标识在 try 之外累加：读数抛异常的那张网卡仍然属于"选中的集合"，
                // 否则"读失败"会被误判成"换了网卡"。
                signature = unchecked(signature * 31 + (adapter.Id == null ? 0 : adapter.Id.GetHashCode()));
                try
                {
                    IPv4InterfaceStatistics stats = adapter.GetIPv4Statistics();
                    received += stats.BytesReceived;
                    sent += stats.BytesSent;
                }
                catch
                {
                    // Some virtual adapters throw while Windows is changing network state.
                }
            }

            return new AdapterTotals(received, sent, any, signature);
        }

        private static bool HasDefaultGateway(NetworkInterface adapter)
        {
            try
            {
                GatewayIPAddressInformationCollection gateways =
                    adapter.GetIPProperties().GatewayAddresses;
                foreach (GatewayIPAddressInformation gateway in gateways)
                {
                    if (gateway != null && gateway.Address != null &&
                        gateway.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        !gateway.Address.Equals(System.Net.IPAddress.Any))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // The adapter may disappear while Windows refreshes network state.
            }

            return false;
        }
    }

    internal struct AdapterTotals
    {
        public readonly long ReceivedBytes;
        public readonly long SentBytes;
        public readonly bool HasAnyAdapter;
        // 这一拍选中的是哪几张网卡。换网卡后（VPN 断开、有线/无线切换）两批计数器
        // 不在同一个数轴上，不能相减。
        public readonly long Signature;

        public AdapterTotals(long receivedBytes, long sentBytes, bool hasAnyAdapter)
            : this(receivedBytes, sentBytes, hasAnyAdapter, 0)
        {
        }

        public AdapterTotals(long receivedBytes, long sentBytes, bool hasAnyAdapter, long signature)
        {
            ReceivedBytes = receivedBytes;
            SentBytes = sentBytes;
            HasAnyAdapter = hasAnyAdapter;
            Signature = signature;
        }
    }

    internal struct SpeedSample
    {
        public readonly double DownloadBytesPerSecond;
        public readonly double UploadBytesPerSecond;
        public readonly bool HasAnyAdapter;
        public readonly double ElapsedSeconds;
        public readonly long DownloadBytes;
        public readonly long UploadBytes;

        public SpeedSample(double downloadBytesPerSecond, double uploadBytesPerSecond, bool hasAnyAdapter)
            : this(downloadBytesPerSecond, uploadBytesPerSecond, hasAnyAdapter, 1.0,
                ToByteCount(downloadBytesPerSecond), ToByteCount(uploadBytesPerSecond))
        {
        }

        public SpeedSample(double downloadBytesPerSecond, double uploadBytesPerSecond,
            bool hasAnyAdapter, double elapsedSeconds, long downloadBytes, long uploadBytes)
        {
            DownloadBytesPerSecond = downloadBytesPerSecond;
            UploadBytesPerSecond = uploadBytesPerSecond;
            HasAnyAdapter = hasAnyAdapter;
            ElapsedSeconds = Math.Max(0, elapsedSeconds);
            DownloadBytes = Math.Max(0, downloadBytes);
            UploadBytes = Math.Max(0, uploadBytes);
        }

        private static long ToByteCount(double bytesPerSecond)
        {
            if (bytesPerSecond <= 0 || double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond))
            {
                return 0;
            }

            return (long)Math.Min(long.MaxValue, Math.Round(bytesPerSecond));
        }
    }

    internal struct RateText
    {
        public readonly string Value;
        public readonly string Unit;

        public RateText(string value, string unit)
        {
            Value = value;
            Unit = unit;
        }
    }

    /// <summary>
    /// 显示值专用的"目标保持器"。
    ///
    /// 秒级采样天生带噪声：空闲时网速在 20~60 KB/s 之间晃、CPU 在 10~20% 之间晃，
    /// 这些都不是用户关心的变化。把每一拍都原样喂给界面，数字就会一直蹦。
    /// 这里加一条死区：新采样和当前保持值差得不够多（相对 25% 或绝对 2 KB），
    /// 就继续用旧目标，界面上的数字纹丝不动；真有大变化才换目标。
    /// </summary>
    internal sealed class HeldValue
    {
        private readonly double relativeBand;
        private readonly double absoluteBand;
        private double held;
        private bool primed;

        public HeldValue(double relativeBand, double absoluteBand)
        {
            this.relativeBand = relativeBand;
            this.absoluteBand = absoluteBand;
        }

        public double Target
        {
            get { return held; }
        }

        public void Snap(double value)
        {
            held = value;
            primed = true;
        }

        public void Update(double sample)
        {
            if (double.IsNaN(sample) || double.IsInfinity(sample))
            {
                return;
            }

            if (!primed)
            {
                held = sample;
                primed = true;
                return;
            }

            double band = Math.Max(absoluteBand, Math.Abs(held) * relativeBand);
            if (Math.Abs(sample - held) > band)
            {
                held = sample;
            }
        }
    }

    /// <summary>
    /// 把「一秒跳一次」的采样值变成连续变化的动画值。
    ///
    /// 做法是指数趋近：每帧把当前值往目标值挪一小段，越接近目标挪得越慢，
    /// 人眼看着就是平滑加速 / 平滑停下，而不是数字直接跳。差值小到看不见时
    /// 直接吸附到目标，否则会永远差一点点，白白重绘。
    /// </summary>
    internal static class SmoothValue
    {
        /// <param name="current">当前显示值</param>
        /// <param name="target">最新采样值</param>
        /// <param name="elapsed">这一帧过去的秒数</param>
        /// <param name="tau">时间常数，越小追得越快（0.22 ≈ 半秒走完 90%）</param>
        /// <param name="epsilon">小于这个差值就直接落位</param>
        public static double Toward(double current, double target, double elapsed, double tau, double epsilon)
        {
            if (double.IsNaN(target) || double.IsInfinity(target))
            {
                target = 0;
            }

            if (double.IsNaN(current) || double.IsInfinity(current))
            {
                return target;
            }

            if (elapsed <= 0)
            {
                return current;
            }

            double alpha = 1.0 - Math.Exp(-elapsed / tau);
            double next = current + (target - current) * alpha;
            return Math.Abs(target - next) <= epsilon ? target : next;
        }
    }

    internal static class SpeedFormatter
    {
        private static readonly string[] Units = { "B", "KB", "MB", "GB" };

        public static string Format(double bytes)
        {
            if (bytes < 0 || double.IsNaN(bytes) || double.IsInfinity(bytes))
            {
                bytes = 0;
            }

            int unit = 0;
            while (bytes >= 1024 && unit < Units.Length - 1)
            {
                bytes /= 1024;
                unit++;
            }

            string value;
            if (bytes >= 100)
            {
                value = bytes.ToString("0");
            }
            else if (bytes >= 10)
            {
                value = bytes.ToString("0.#");
            }
            else
            {
                value = bytes.ToString("0.##");
            }

            return value + Units[unit];
        }

        public static RateText FormatBits(double bytesPerSecond)
        {
            double bitsPerSecond = Clean(bytesPerSecond) * 8.0;
            string unit = "bps";
            double value = bitsPerSecond;

            if (value >= 1000.0 * 1000.0)
            {
                value /= 1000.0 * 1000.0;
                unit = "Mbps";
            }
            else if (value >= 1000.0)
            {
                value /= 1000.0;
                unit = "Kbps";
            }

            return new RateText(FormatNumber(value), unit);
        }

        public static RateText FormatMegabytesRate(double bytesPerSecond)
        {
            double value = Clean(bytesPerSecond) / (1024.0 * 1024.0);
            return new RateText(FormatNumber(value), "MB/s");
        }

        public static string FormatMegabytes(double bytesPerSecond)
        {
            return FormatNumber(Clean(bytesPerSecond) / (1024.0 * 1024.0)) + " MB/s";
        }

        public static string FormatTraffic(double bytes)
        {
            return Format(bytes);
        }

        public static string FormatCompact(double bytesPerSecond)
        {
            double value = Clean(bytesPerSecond) / 1024.0;
            if (value >= 1024.0)
            {
                return FormatNumber(value / 1024.0) + "M";
            }

            return FormatNumber(value) + "K";
        }

        private static double Clean(double value)
        {
            if (value < 0 || double.IsNaN(value) || double.IsInfinity(value))
            {
                return 0;
            }

            return value;
        }

        private static string FormatNumber(double value)
        {
            if (value >= 100.0)
            {
                return value.ToString("0");
            }

            if (value >= 10.0)
            {
                return value.ToString("0.#");
            }

            return value.ToString("0.##");
        }
    }

    internal static class AppSettings
    {
        private const string SettingsKey = @"Software\NetSpeedBall";

        public static bool LoadUseMegabits()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey, false))
                {
                    return key != null && Convert.ToInt32(key.GetValue("UseMegabits", 0)) == 1;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void SaveUseMegabits(bool value)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                {
                    if (key != null) key.SetValue("UseMegabits", value ? 1 : 0, RegistryValueKind.DWord);
                }
            }
            catch
            {
            }
        }

        public static bool TryLoadLocation(out Point location)
        {
            location = Point.Empty;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey, false))
                {
                    if (key == null) return false;
                    object xValue = key.GetValue("X");
                    object yValue = key.GetValue("Y");
                    if (!(xValue is int) || !(yValue is int)) return false;
                    location = new Point((int)xValue, (int)yValue);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void SaveLocation(Point location)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                {
                    if (key == null) return;
                    key.SetValue("X", location.X, RegistryValueKind.DWord);
                    key.SetValue("Y", location.Y, RegistryValueKind.DWord);
                }
            }
            catch
            {
            }
        }
    }
    internal static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        // Windows 10/11 的"启动应用"列表开关不在 Run 键里，而是单独记在这条键下：
        // 首字节 0x02 = 允许启动，0x03 = 已被禁用（任务管理器里关掉了）。
        // Run 键里即使有本程序，只要这里是 0x03，开机一样不会启动。
        private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const byte ApprovedEnabled = 0x02;
        private const byte ApprovedDisabled = 0x03;

        public static bool IsEnabled(string appName)
        {
            return HasRunEntry(appName) && !IsBlockedByApprovedList(appName);
        }

        public static void SetEnabled(string appName, bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("\u65e0\u6cd5\u6253\u5f00\u5f53\u524d\u7528\u6237\u542f\u52a8\u9879\u6ce8\u518c\u8868\u3002");
                }

                if (enabled)
                {
                    key.SetValue(appName, GetStartupCommand(), RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(appName, false);
                }
            }

            WriteApprovedFlag(appName, enabled);
        }

        /// <summary>
        /// 判断 Run 键里是否已登记本程序，且指向的仍是当前这个 exe。
        /// </summary>
        private static bool HasRunEntry(string appName)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    string value = key.GetValue(appName) as string;
                    if (string.IsNullOrEmpty(value))
                    {
                        return false;
                    }

                    // 允许带引号、不带引号、或后面跟着多余空格，只要路径一致就算已登记。
                    string normalized = value.Trim().Trim('"').Trim();
                    return string.Equals(normalized, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 是否被 Windows"启动应用"列表单独禁用。禁用了就开机不启动，需要清掉这个标记。
        /// </summary>
        private static bool IsBlockedByApprovedList(string appName)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ApprovedKey, false))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    byte[] data = key.GetValue(appName) as byte[];
                    return data != null && data.Length > 0 && data[0] == ApprovedDisabled;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 同步"启动应用"列表的开关标记。勾选时写成允许启动，取消勾选时直接删除标记，
        /// 免得留下禁用记录把下次勾选又挡住。
        /// </summary>
        private static void WriteApprovedFlag(string appName, bool enabled)
        {
            try
            {
                if (!enabled)
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
                    {
                        if (key != null)
                        {
                            key.DeleteValue(appName, false);
                        }
                    }

                    return;
                }

                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(ApprovedKey))
                {
                    if (key == null)
                    {
                        return;
                    }

                    byte[] data = new byte[12];
                    data[0] = ApprovedEnabled;
                    key.SetValue(appName, data, RegistryValueKind.Binary);
                }
            }
            catch
            {
                // 写不上这条标记不影响 Run 键，开机启动多半仍是好的，忽略即可。
            }
        }

        private static string GetStartupCommand()
        {
            return "\"" + Application.ExecutablePath + "\"";
        }
    }

    internal static class IconFactory
    {
        public static Icon CreateIcon(int size)
        {
            using (Bitmap bitmap = new Bitmap(size, size))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                Rectangle rect = new Rectangle(3, 3, size - 7, size - 7);
                using (GraphicsPath ballPath = new GraphicsPath())
                {
                    ballPath.AddEllipse(rect);
                    using (PathGradientBrush face = new PathGradientBrush(ballPath))
                    {
                        face.CenterPoint = new PointF(rect.X + rect.Width * 0.46f, rect.Y + rect.Height * 0.34f);
                        face.CenterColor = Color.White;
                        face.SurroundColors = new[] { Color.FromArgb(232, 243, 251) };
                        g.FillPath(face, ballPath);
                    }

                    using (GraphicsPath glossPath = new GraphicsPath())
                    {
                        Rectangle gloss = new Rectangle(
                            rect.X + size / 5,
                            rect.Y + size / 9,
                            rect.Width - (size * 2 / 5),
                            Math.Max(4, size / 4));
                        glossPath.AddEllipse(gloss);
                        using (PathGradientBrush glossBrush = new PathGradientBrush(glossPath))
                        {
                            glossBrush.CenterColor = Color.FromArgb(148, 255, 255, 255);
                            glossBrush.SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) };
                            g.FillPath(glossBrush, glossPath);
                        }
                    }
                }

                RectangleF ringRect = new RectangleF(
                    rect.X + size * 0.07f,
                    rect.Y + size * 0.07f,
                    rect.Width - size * 0.14f,
                    rect.Height - size * 0.14f);

                using (Pen glow = new Pen(Color.FromArgb(112, 122, 181, 220), Math.Max(1.0f, size / 14.0f)))
                using (Pen outer = new Pen(Color.FromArgb(246, 250, 253, 255), Math.Max(1.0f, size / 25.0f)))
                using (Pen inner = new Pen(Color.FromArgb(190, 176, 211, 232), Math.Max(1.0f, size / 55.0f)))
                using (Pen progress = new Pen(Color.FromArgb(218, 46, 136, 232), Math.Max(1.2f, size / 20.0f)))
                {
                    progress.StartCap = LineCap.Round;
                    progress.EndCap = LineCap.Round;
                    g.DrawEllipse(glow, ringRect);
                    g.DrawEllipse(outer, rect);
                    g.DrawEllipse(inner, RectangleF.Inflate(ringRect, -size * 0.05f, -size * 0.05f));
                    g.DrawArc(progress, RectangleF.Inflate(ringRect, -size * 0.04f, -size * 0.04f), 132, 238);
                }

                DrawIconClouds(g, rect, size);
                DrawIconRocket(g, size * 0.59f, size * 0.35f, size / 64.0f);

                using (Pen edge = new Pen(Color.FromArgb(178, 174, 211, 231), Math.Max(1.0f, size / 58.0f)))
                {
                    g.DrawEllipse(edge, rect);
                }

                IntPtr hIcon = bitmap.GetHicon();
                try
                {
                    Icon icon = (Icon)Icon.FromHandle(hIcon).Clone();
                    return icon;
                }
                finally
                {
                    NativeMethods.DestroyIcon(hIcon);
                }
            }
        }

        private static void DrawIconClouds(Graphics g, Rectangle rect, int size)
        {
            GraphicsState state = g.Save();
            using (GraphicsPath clipPath = new GraphicsPath())
            {
                clipPath.AddEllipse(rect);
                g.SetClip(clipPath, CombineMode.Intersect);

                float scale = size / 64.0f;
                float y = rect.Bottom - 18.0f * scale;
                using (Brush body = new SolidBrush(Color.FromArgb(248, 252, 255)))
                using (Brush shade = new SolidBrush(Color.FromArgb(220, 235, 244, 250)))
                {
                    g.FillEllipse(body, rect.X + 4.0f * scale, y + 5.0f * scale, 19.0f * scale, 15.0f * scale);
                    g.FillEllipse(body, rect.X + 17.0f * scale, y, 21.0f * scale, 19.0f * scale);
                    g.FillEllipse(body, rect.X + 34.0f * scale, y + 4.0f * scale, 24.0f * scale, 17.0f * scale);
                    g.FillRectangle(body, rect.X + 4.0f * scale, y + 11.0f * scale, rect.Width - 8.0f * scale, 19.0f * scale);
                    g.FillEllipse(shade, rect.X + 36.0f * scale, y + 12.0f * scale, 23.0f * scale, 11.0f * scale);
                }
            }

            g.Restore(state);
        }

        private static void DrawIconRocket(Graphics g, float x, float y, float scale)
        {
            GraphicsState state = g.Save();
            g.TranslateTransform(x, y);
            g.RotateTransform(-34.0f);
            g.ScaleTransform(scale, scale);

            using (GraphicsPath flame = new GraphicsPath())
            using (Brush orange = new SolidBrush(Color.FromArgb(236, 255, 151, 54)))
            {
                flame.AddPolygon(new[]
                {
                    new PointF(-10.0f, -3.0f),
                    new PointF(-17.0f, 0.0f),
                    new PointF(-10.0f, 3.0f)
                });
                g.FillPath(orange, flame);
            }

            using (GraphicsPath body = new GraphicsPath())
            using (LinearGradientBrush bodyBrush = new LinearGradientBrush(
                       new RectangleF(-9.0f, -6.0f, 19.0f, 12.0f),
                       Color.White,
                       Color.FromArgb(202, 232, 255),
                       LinearGradientMode.ForwardDiagonal))
            using (Pen edge = new Pen(Color.FromArgb(192, 70, 142, 220), 1.0f))
            using (Brush fin = new SolidBrush(Color.FromArgb(255, 44, 132, 224)))
            {
                g.FillPolygon(fin, new[]
                {
                    new PointF(-5.0f, -3.4f),
                    new PointF(-9.0f, -7.2f),
                    new PointF(-2.0f, -5.0f)
                });
                g.FillPolygon(fin, new[]
                {
                    new PointF(-5.0f, 3.4f),
                    new PointF(-9.0f, 7.2f),
                    new PointF(-2.0f, 5.0f)
                });

                body.AddBezier(-9.0f, -4.8f, -3.5f, -7.2f, 5.0f, -4.8f, 9.0f, 0.0f);
                body.AddBezier(9.0f, 0.0f, 5.0f, 4.8f, -3.5f, 7.2f, -9.0f, 4.8f);
                body.CloseFigure();
                g.FillPath(bodyBrush, body);
                g.DrawPath(edge, body);
            }

            using (Brush window = new SolidBrush(Color.FromArgb(255, 42, 137, 229)))
            {
                g.FillEllipse(window, 0.4f, -2.4f, 4.8f, 4.8f);
            }

            g.Restore(state);
        }
    }

    internal static class NativeMethods
    {
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        public const int AddressFamilyInet = 2;
        public const int AddressFamilyInet6 = 23;
        public const int TcpTableOwnerPidAll = 5;
        public const int TcpEstatsTypeData = 1;
        public const int TcpEstatsDataSize = 96;
        // MIB_TCP6ROW 的真实大小：State(4) + LocalAddr(16) + LocalScopeId(4) + LocalPort(4)
        // + RemoteAddr(16) + RemoteScopeId(4) + RemotePort(4) = 52。
        // 注意它和表里返回的 MIB_TCP6ROW_OWNER_PID（56 字节，State 在行尾）不是同一个结构，
        // 两者不前缀兼容，必须逐字段搬。
        public const int Tcp6RowSize = 52;
        // TCP_ESTATS_DATA_RW_v0 只含一个 EnableCollection 开关，大小正好 1 字节。
        public const int TcpEstatsDataRwSize = 1;
        public const uint ErrorInsufficientBuffer = 122;
        public const uint ErrorNotFound = 1168;
        public const int HwndBroadcast = 0xFFFF;

        [StructLayout(LayoutKind.Sequential)]
        public struct FileTime
        {
            public uint LowDateTime;
            public uint HighDateTime;

            public long ToInt64()
            {
                return ((long)HighDateTime << 32) | LowDateTime;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysicalMemory;
            public ulong AvailablePhysicalMemory;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime,
            out FileTime userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint GetExtendedTcpTable(byte[] tcpTable, ref int size, bool order,
            int addressFamily, int tableClass, int reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint GetPerTcpConnectionEStats(IntPtr row, int estatsType,
            IntPtr rw, int rwVersion, int rwSize,
            IntPtr ros, int rosVersion, int rosSize,
            IntPtr rod, int rodVersion, int rodSize);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint GetPerTcp6ConnectionEStats(IntPtr row, int estatsType,
            IntPtr rw, int rwVersion, int rwSize,
            IntPtr ros, int rosVersion, int rosSize,
            IntPtr rod, int rodVersion, int rodSize);

        // 采集默认是关闭的：只有先用它申请启用，后面的 Get...EStats 才会返回真实字节数。
        // 这一步需要管理员权限，普通权限会返回 ERROR_ACCESS_DENIED(5)。
        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint SetPerTcpConnectionEStats(IntPtr row, int estatsType,
            IntPtr rw, int rwVersion, int rwSize, int offset);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint SetPerTcp6ConnectionEStats(IntPtr row, int estatsType,
            IntPtr rw, int rwVersion, int rwSize, int offset);

        [DllImport("shell32.dll", SetLastError = true)]
        public static extern bool IsUserAnAdmin();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string message);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}

