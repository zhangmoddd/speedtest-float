using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Management;
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
        {            // 截图自检用的两个开关必须在单实例锁之前处理：球开着的时候也要能渲染，
            // 否则第二次启动会被当成"重复启动"，直接把已有的球亮出来就退出了。
            if (args != null && args.Length >= 2)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if (string.Equals(args[0], "--render-preview", StringComparison.OrdinalIgnoreCase))
                {
                    int variant = 0;
                    if (args.Length >= 3)
                    {
                        int.TryParse(args[2], out variant);
                    }

                    // --render-preview 图片路径 [版式] [火箭角度]
                    float rocketAngle = 0.0f;
                    if (args.Length >= 4)
                    {
                        float.TryParse(args[3], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out rocketAngle);
                    }

                    SpeedBallForm.SavePreview(args[1], variant, rocketAngle);
                    return;
                }

                if (string.Equals(args[0], "--diag", StringComparison.OrdinalIgnoreCase))
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    sb.AppendLine("UiScale.Dpi=" + UiScale.DpiValue + " Factor=" + UiScale.Factor);
                    using (SpeedBallForm f = new SpeedBallForm())
                    {
                        sb.AppendLine("DeviceDpi=" + f.DeviceDpi + " AutoScaleMode=" + f.AutoScaleMode
                            + " WinDpi=" + NativeMethods.GetDpiForWindow(f.Handle)
                            + " SysDpi=" + NativeMethods.GetDpiForSystem());
                        sb.AppendLine("ctor     Size=" + f.Width + "x" + f.Height + " Client=" + f.ClientSize.Width + "x" + f.ClientSize.Height);
                        IntPtr probe = f.Handle;
                        sb.AppendLine("handle   Size=" + f.Width + "x" + f.Height + " Client=" + f.ClientSize.Width + "x" + f.ClientSize.Height);
                        f.Show();
                        Application.DoEvents();
                        sb.AppendLine("show     Size=" + f.Width + "x" + f.Height + " Client=" + f.ClientSize.Width + "x" + f.ClientSize.Height);
                        if (f.Region != null)
                        {
                            RectangleF b = f.Region.GetBounds(f.CreateGraphics());
                            sb.AppendLine("Region   " + b.Width + "x" + b.Height + " at " + b.X + "," + b.Y);
                        }
                        sb.AppendLine("Min=" + f.MinimumSize + " Max=" + f.MaximumSize);

                        if (args.Length >= 3)
                        {
                            // 真实屏幕截图：验证的是"窗口自己画在屏幕 DC 上"的效果，
                            // 和离屏出图（Bitmap DC）不是同一条路径。
                            System.Threading.Thread.Sleep(300);
                            Application.DoEvents();
                            using (Bitmap shot = new Bitmap(f.Width, f.Height))
                            using (Graphics sg = Graphics.FromImage(shot))
                            {
                                sg.CopyFromScreen(f.Left, f.Top, 0, 0, new Size(f.Width, f.Height));
                                shot.Save(args[2], System.Drawing.Imaging.ImageFormat.Png);
                            }
                        }
                    }
                    System.IO.File.WriteAllText(args[1], sb.ToString());
                    return;
                }

                if (string.Equals(args[0], "--render-details", StringComparison.OrdinalIgnoreCase))
                {
                    SpeedDetailsForm.SavePreview(args[1]);
                    return;
                }

                if (string.Equals(args[0], "--render-traffic", StringComparison.OrdinalIgnoreCase))
                {
                    TrafficWindow.SavePreview(args[1]);
                    return;
                }

                if (string.Equals(args[0], "--render-power", StringComparison.OrdinalIgnoreCase))
                {
                    PowerScheduleForm.SavePreview(args[1]);
                    return;
                }

                // --render-launchpad 图片路径 [激活0/1] [动画秒数] [点火进度0~1]
                if (string.Equals(args[0], "--render-launchpad", StringComparison.OrdinalIgnoreCase))
                {
                    bool active = args.Length >= 3 && args[2] == "1";
                    double clock = 0.0;
                    double burst = -1.0;
                    if (args.Length >= 4)
                    {
                        double.TryParse(args[3], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out clock);
                    }
                    if (args.Length >= 5)
                    {
                        double.TryParse(args[4], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out burst);
                    }
                    LaunchPadForm.SavePreview(args[1], active, clock, burst);
                    return;
                }

                // --render-launch 图片路径 [发射进行到第几秒]：看发射过程中球窗口里画的是什么
                if (string.Equals(args[0], "--render-launch", StringComparison.OrdinalIgnoreCase))
                {
                    double launchElapsed = 0.0;
                    if (args.Length >= 3)
                    {
                        double.TryParse(args[2], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out launchElapsed);
                    }
                    SpeedBallForm.SaveLaunchPreview(args[1], launchElapsed);
                    return;
                }
            }

            bool enableStartup = false;
            bool parallelTest = false;
            bool selfTestFields = false;
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--enable-startup", StringComparison.OrdinalIgnoreCase))
                    {
                        enableStartup = true;
                    }
                    else if (string.Equals(args[i], "--parallel-test", StringComparison.OrdinalIgnoreCase))
                    {
                        // 自检专用：另开一份互不干扰的实例，不跟用户正在用的那个抢锁
                        parallelTest = true;
                    }
                    else if (string.Equals(args[i], "--self-test-fields", StringComparison.OrdinalIgnoreCase))
                    {
                        // 自检专用：自动开面板、逐项隐藏，把窗口状态写进日志
                        selfTestFields = true;
                    }
                }
            }

            string mutexName = parallelTest
                ? "NetSpeedBall.SingleInstance.Test." + Process.GetCurrentProcess().Id
                : "NetSpeedBall.SingleInstance";

            bool createdNew;
            using (Mutex singleInstance = new Mutex(true, mutexName, out createdNew))
            {
                // 提权重启时同一个 exe 会被拉起第二次，而旧进程要过几百毫秒才退干净。
                // 这段时间里锁还握着：新进程如果像平时那样"看到有人就退出"，结果就是两个都没了。
                // 所以带 --enable-startup 时先等旧进程把锁放掉，最多等 8 秒。
                bool owned = createdNew;
                if (!owned)
                {
                    // createdNew=false 有两种可能：
                    // ① 真有一个实例在跑 → 抢锁会失败，走下面"亮出已有窗口"的分支；
                    // ② 上一个实例被强杀、锁没人释放（句柄还被别的进程继承着）→ 抢得到。
                    // 只认 createdNew 的话，② 会变成"双击毫无反应"，程序再也起不来。
                    try
                    {
                        owned = singleInstance.WaitOne(enableStartup ? 8000 : 0);
                    }
                    catch (AbandonedMutexException)
                    {
                        // 旧进程是被强杀的，锁已经"遗弃"，现在归我们
                        owned = true;
                    }
                }

                if (!owned)
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

                Application.Run(new SpeedBallApplication(enableStartup, selfTestFields && parallelTest));
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

        // 定时关机 / 重启交给系统的 shutdown.exe；睡眠和锁定只能在进程里等时间到了自己执行。
        private readonly FormsTimer powerTimer;
        private DateTime powerDeadline;
        private string powerPendingAction;
        private bool shutdownPending;

        public SpeedBallApplication() : this(false, false)
        {
        }

        /// <param name="enableElevatedStartup">
        /// 由提权重启带过来的开关：这一趟启动后把"登录时以最高权限启动"的计划任务建好。
        /// </param>
        /// <param name="selfTestFields">
        /// 自检用：启动后自动开面板、连续隐藏几项，把每一步的窗口状态写进日志。
        /// 用来复现"取消勾选后面板消失"这类只在真实交互里出现的问题。
        /// </param>
        public SpeedBallApplication(bool enableElevatedStartup, bool selfTestFields)
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
                // 勾选状态等菜单第一次弹出时再查：判断计划任务要调 schtasks.exe，
                // 启动阶段没必要为它多等一次进程创建。
                Checked = false,
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
            menu.Items.Add(new ToolStripMenuItem("\u6253\u5f00\u7cfb\u7edf\u8be6\u60c5", null, OnShowDetails));
            // 面板上的右键菜单是主入口，这里再给一个托盘入口：
            // 面板没开的时候也能先把不想看的项关掉。
            menu.Items.Add(SpeedDetailsForm.BuildFieldMenuItem(delegate
            {
                if (ballForm != null)
                {
                    ballForm.RefreshDetailsLayout();
                }
            }));
            menu.Items.Add(new ToolStripMenuItem("\u6d41\u91cf\u7edf\u8ba1", null, OnShowTrafficWindow));
            menu.Items.Add(BuildPowerMenu());
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

            powerTimer = new FormsTimer { Interval = 1000 };
            powerTimer.Tick += OnPowerTick;
            powerTimer.Start();

            if (enableElevatedStartup)
            {
                if (StartupManager.TryEnableElevated(AppName))
                {
                    startupItem.Checked = true;
                }
                else
                {
                    startupItem.Checked = StartupManager.IsEnabled(AppName);
                    MessageBox.Show(
                        "\u5df2\u4ee5\u7ba1\u7406\u5458\u8eab\u4efd\u542f\u52a8\uff0c\u4f46\u8bbe\u7f6e\u5f00\u673a\u542f\u52a8\u5931\u8d25\u3002\r\n" +
                        "\u53ef\u4ee5\u518d\u53f3\u952e\u6258\u76d8\u56fe\u6807\u91cd\u8bd5\u4e00\u6b21\u3002",
                        "\u7f51\u7edc\u6d4b\u901f\u7403", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            if (selfTestFields)
            {
                FormsTimer testTimer = new FormsTimer { Interval = 1500 };
                testTimer.Tick += delegate
                {
                    testTimer.Stop();
                    RunFieldSelfTest();
                };
                testTimer.Start();
            }
        }

        /// <summary>
        /// 自检：开面板，然后一项一项地隐藏，每一步都记下窗口是不是还活着。
        /// 只有带 --parallel-test --self-test-fields 启动时才会跑，正常使用碰不到。
        /// </summary>
        private void RunFieldSelfTest()
        {
            SpeedDetailsForm.Trace("=== self test: open panel ===");
            ballForm.ShowDetails();
            string[] ids = { "cpu.speed", "net.graph", "nic.card", "cpu.model", "mem.speed" };
            int index = 0;
            FormsTimer step = new FormsTimer { Interval = 1200 };
            step.Tick += delegate
            {
                if (index >= ids.Length)
                {
                    step.Stop();
                    SpeedDetailsForm.Trace("=== self test done ===");
                    return;
                }

                string id = ids[index++];
                PanelFieldSettings.SetVisible(id, false);
                SpeedDetailsForm.Trace("step hide " + id);
                ballForm.RefreshDetailsLayout();
            };
            step.Start();
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
            bool want = startupItem.Checked;
            try
            {
                if (!want)
                {
                    StartupManager.SetEnabled(AppName, false);
                    return;
                }

                if (NativeMethods.IsUserAnAdmin())
                {
                    if (StartupManager.TryEnableElevated(AppName))
                    {
                        return;
                    }

                    // 已经是管理员还建不上：多半是系统策略禁用了计划任务，退回普通启动项
                    StartupManager.SetEnabled(AppName, true);
                    MessageBox.Show(
                        "\u8bbe\u7f6e\u201c\u5f00\u673a\u4ee5\u7ba1\u7406\u5458\u8eab\u4efd\u542f\u52a8\u201d\u5931\u8d25\uff0c" +
                        "\u5df2\u9000\u56de\u666e\u901a\u5f00\u673a\u542f\u52a8\uff08\u6ca1\u6709\u5e94\u7528\u6d41\u91cf\u6392\u884c\uff09\u3002",
                        "\u7f51\u7edc\u6d4b\u901f\u7403", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                DialogResult answer = MessageBox.Show(
                    "\u60f3\u8ba9\u7a0b\u5e8f\u5f00\u673a\u5c31\u662f\u7ba1\u7406\u5458\u6743\u9650\uff08\u5e94\u7528\u6d41\u91cf\u6392\u884c\u8981\u7528\uff09\uff0c\r\n" +
                    "\u9700\u8981\u73b0\u5728\u4ee5\u7ba1\u7406\u5458\u8eab\u4efd\u91cd\u542f\u4e00\u6b21\uff0c\u4e4b\u540e\u5c31\u4e0d\u7528\u518d\u7ba1\u4e86\u3002\r\n\r\n" +
                    "\u662f\uff1a\u7acb\u5373\u63d0\u6743\u91cd\u542f\uff0c\u5e76\u8bbe\u597d\u201c\u5f00\u673a\u4ee5\u7ba1\u7406\u5458\u542f\u52a8\u201d\u3002\r\n" +
                    "\u5426\uff1a\u5148\u7528\u666e\u901a\u6743\u9650\u5f00\u673a\u542f\u52a8\uff08\u6392\u884c\u699c\u6ca1\u6709\u6570\u636e\uff09\u3002",
                    "\u7f51\u7edc\u6d4b\u901f\u7403", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

                if (answer == DialogResult.Cancel)
                {
                    startupItem.Checked = StartupManager.IsEnabled(AppName);
                    return;
                }

                if (answer == DialogResult.Yes)
                {
                    RestartElevated(true);
                    return;
                }

                StartupManager.SetEnabled(AppName, true);
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

                // 把本进程的 PID 交给浮光，让它盯着我们：球一退，面板跟着退。
                //
                // 为什么不是"球退出时去 kill 浮光"：球是普通权限（asInvoker），
                // 浮光一旦曾被以管理员身份启动过，这里就杀不掉它，而且失败是
                // 静默的 —— 用户只会觉得"关了还留着，关不掉"。让浮光自己盯着
                // 父进程，只需要 SYNCHRONIZE 一个访问位，不用任何提权。
                int selfPid = Process.GetCurrentProcess().Id;
                ProcessStartInfo startInfo = new ProcessStartInfo(
                    exe,
                    "--panel-only --parent-pid " + selfPid);
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
        /// 找浮光面板程序：优先「面板」子文件夹，其次同目录，最后上一级（开发时两个工程是平级的）。
        ///
        /// 为什么子文件夹排在最前面：发布包里球和浮光是分开放的 ——
        /// 桌面上并排摆两个 exe，用户第一反应是"我该点哪个"。
        /// 把浮光收进「面板\」之后，用户只看得见球，而球自己知道去哪儿找人。
        /// 同目录仍然保留：拆开发的时候（ball 和 app 各自编各自的）两个 exe 是平级的。
        /// </summary>
        private static string LocateFuguangPanel()
        {
            string[] names = { "浮光面板.exe", "fuguang.exe" };
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] dirs =
            {
                Path.Combine(baseDir, "面板"),
                baseDir,
                Path.GetFullPath(Path.Combine(baseDir, "..")),
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
            RestartElevated(false);
        }

        /// <param name="enableStartup">
        /// true = 提权重启后顺便把"开机以管理员启动"的计划任务建好（用户刚在菜单里勾了这个）。
        /// </param>
        private void RestartElevated(bool enableStartup)
        {
            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                UseShellExecute = true,
                Verb = "runas"
            };

            if (enableStartup)
            {
                info.Arguments = "--enable-startup";
            }

            try
            {
                Process.Start(info);
            }
            catch (Exception ex)
            {
                // 用户在 UAC 弹窗上点“否”也会走到这里。此时绝不能退出，球要留在原地。
                startupItem.Checked = StartupManager.IsEnabled(AppName);
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

        // ===========================================================
        // 定时关机 / 重启 / 睡眠 / 锁定
        // ===========================================================

        private ToolStripMenuItem BuildPowerMenu()
        {
            ToolStripMenuItem root = new ToolStripMenuItem("\u5b9a\u65f6\u5173\u673a");
            AddPowerPreset(root, "15 \u5206\u949f\u540e\u5173\u673a", 15);
            AddPowerPreset(root, "30 \u5206\u949f\u540e\u5173\u673a", 30);
            AddPowerPreset(root, "1 \u5c0f\u65f6\u540e\u5173\u673a", 60);
            AddPowerPreset(root, "2 \u5c0f\u65f6\u540e\u5173\u673a", 120);
            root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add(new ToolStripMenuItem(
                "\u81ea\u5b9a\u4e49\uff08\u53ef\u9009\u65e5\u671f\u65f6\u95f4\uff09\u2026", null, OnPowerCustom));
            root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add(new ToolStripMenuItem("\u7acb\u5373\u7761\u7720", null, OnSleepNow));
            root.DropDownItems.Add(new ToolStripMenuItem("\u7acb\u5373\u9501\u5b9a", null, OnLockNow));
            root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add(new ToolStripMenuItem("\u53d6\u6d88\u5b9a\u65f6\u4efb\u52a1", null, OnCancelPower));
            return root;
        }

        private void AddPowerPreset(ToolStripMenuItem root, string text, int minutes)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text, null, OnPowerPreset);
            item.Tag = minutes;
            root.DropDownItems.Add(item);
        }

        private void OnPowerPreset(object sender, EventArgs e)
        {
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            int minutes = item != null && item.Tag is int ? (int)item.Tag : 30;
            SchedulePower("shutdown", minutes);
        }

        private void OnPowerCustom(object sender, EventArgs e)
        {
            using (PowerScheduleForm dialog = new PowerScheduleForm())
            {
                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                SchedulePowerAt(dialog.SelectedAction, dialog.When);
            }
        }

        private void OnSleepNow(object sender, EventArgs e)
        {
            SchedulePower("sleep", 0);
        }

        private void OnLockNow(object sender, EventArgs e)
        {
            SchedulePower("lock", 0);
        }

        private void OnCancelPower(object sender, EventArgs e)
        {
            CancelPower(true);
        }

        /// <summary>
        /// 安排一次电源动作。minutes = 0 表示立刻执行。
        ///
        /// 关机和重启直接交给系统的 shutdown.exe（带 /t 倒计时），所以就算本程序退出，
        /// 计划依然有效，`shutdown /a` 也能取消。睡眠和锁定系统没有"定时"接口，
        /// 只能在进程里等到点自己调。
        /// </summary>
        private void SchedulePower(string action, int minutes)
        {
            SchedulePowerAt(action, DateTime.Now.AddMinutes(minutes));
        }

        /// <summary>
        /// 安排一次电源动作，到一个**绝对时刻**执行。
        ///
        /// 预设菜单（「30 分钟后关机」那些）最终也走这里：菜单给的是分钟数，
        /// 换算成时刻之后只剩一条执行路径，不必维护两套逻辑。
        ///
        /// 关机和重启交给 shutdown.exe 的 /t（它接受的最大值是 315360000 秒），
        /// 所以程序退出之后计划依然有效。代价是这个倒计时挂在系统的关机流程上，
        /// 中途重启电脑就没了 —— 对话框里写明了这一条。
        /// </summary>
        private void SchedulePowerAt(string action, DateTime when)
        {
            CancelPower(false);

            double seconds = (when - DateTime.Now).TotalSeconds;
            if (seconds <= 0)
            {
                ExecutePower(action);
                return;
            }

            // 排到一年以外没有意义：没人记得住，中途也一定会重启。
            if (seconds > 365.0 * 24 * 3600)
            {
                MessageBox.Show("只能安排在一年之内。", "网络测速球",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (action == "shutdown" || action == "restart")
            {
                string flag = action == "restart" ? "/r" : "/s";
                string comment = action == "restart"
                    ? "\u7f51\u7edc\u6d4b\u901f\u7403\uff1a\u5b9a\u65f6\u91cd\u542f"
                    : "\u7f51\u7edc\u6d4b\u901f\u7403\uff1a\u5b9a\u65f6\u5173\u673a";
                if (!RunShutdown(flag + " /t " + (int)Math.Ceiling(seconds) + " /c \"" + comment + "\""))
                {
                    MessageBox.Show("\u5b89\u6392\u5931\u8d25\uff1a\u7cfb\u7edf\u62d2\u7edd\u4e86\u5173\u673a\u547d\u4ee4\u3002",
                        "\u7f51\u7edc\u6d4b\u901f\u7403", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                shutdownPending = true;
            }
            else
            {
                powerPendingAction = action;
                powerDeadline = when;
            }

            trayIcon.ShowBalloonTip(4000, "已安排" + PowerActionName(action),
                PowerScheduleForm.DescribeMoment(when) + PowerActionName(action) +
                "\u3002\u8981\u53d6\u6d88\uff1a\u53f3\u952e\u60ac\u6d6e\u7403 \u2192 \u5b9a\u65f6\u5173\u673a \u2192 \u53d6\u6d88\u5b9a\u65f6\u4efb\u52a1\u3002",
                ToolTipIcon.Info);
        }

        private void CancelPower(bool notify)
        {
            bool hadSomething = shutdownPending || powerPendingAction != null;

            if (shutdownPending)
            {
                RunShutdown("/a");
                shutdownPending = false;
            }

            powerPendingAction = null;

            if (!notify)
            {
                return;
            }

            if (hadSomething)
            {
                trayIcon.ShowBalloonTip(3000, "\u5df2\u53d6\u6d88", "\u5b9a\u65f6\u4efb\u52a1\u5df2\u53d6\u6d88\u3002", ToolTipIcon.Info);
            }
            else
            {
                trayIcon.ShowBalloonTip(3000, "\u6ca1\u6709\u5b9a\u65f6\u4efb\u52a1",
                    "\u5f53\u524d\u6ca1\u6709\u5b89\u6392\u5173\u673a / \u91cd\u542f / \u7761\u7720 / \u9501\u5b9a\u3002", ToolTipIcon.Info);
            }
        }

        private void OnPowerTick(object sender, EventArgs e)
        {
            // 用户可能在"设置 → 显示 → 缩放"里改了比例。程序不重启也要跟着变，
            // 所以每秒瞄一眼系统 DPI，变了就把球和面板重新按新比例摆一遍。
            if (UiScale.Refresh())
            {
                try
                {
                    ballForm.ApplyUiScale();
                    if (trafficWindow != null && !trafficWindow.IsDisposed)
                    {
                        trafficWindow.ApplyUiScale();
                    }
                }
                catch
                {
                }
            }

            if (powerPendingAction == null || DateTime.Now < powerDeadline)
            {
                return;
            }

            string action = powerPendingAction;
            powerPendingAction = null;
            ExecutePower(action);
        }

        private static void ExecutePower(string action)
        {
            switch (action)
            {
                case "restart":
                    RunShutdown("/r /t 0");
                    break;
                case "sleep":
                    NativeMethods.SetSuspendState(false, false, false);
                    break;
                case "lock":
                    NativeMethods.LockWorkStation();
                    break;
                default:
                    RunShutdown("/s /t 0");
                    break;
            }
        }

        private static string PowerActionName(string action)
        {
            switch (action)
            {
                case "restart":
                    return "\u91cd\u542f";
                case "sleep":
                    return "\u7761\u7720";
                case "lock":
                    return "\u9501\u5b9a";
                default:
                    return "\u5173\u673a";
            }
        }

        private static bool RunShutdown(string arguments)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo("shutdown.exe", arguments);
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.WindowStyle = ProcessWindowStyle.Hidden;

                using (Process process = Process.Start(info))
                {
                    if (process == null)
                    {
                        return false;
                    }

                    if (!process.WaitForExit(5000))
                    {
                        return false;
                    }

                    return process.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
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
        internal const int BallSize = 122;
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
        /// <summary>球中间那行速率数字和单位的常驻字体（每帧复用，别再 new）。</summary>
        private Font rateFont;
        private Font rateUnitFont;
        private Icon appIcon;
        private bool overLaunchPad;
        private bool launchAnimating;
        /// <summary>发射的三个阶段时长（秒）：原地蓄力抖动 / 飞出屏幕 / 在原位淡入。</summary>
        private const double LaunchChargeSeconds = 0.36;
        private const double LaunchFlightSeconds = 0.95;
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
        /// <summary>松手时球离发射位还差多少：点火的前一小段会先滑到正中（像被夹具拉到位）。</summary>
        private Point launchSnapFrom;
        private bool launchUseDock;
        private double launchReturnOpacity;
        private readonly TrafficStore trafficStore;
        private DockEdge dockEdge;
        private bool docked;
        private bool closingForExit;
        private Rectangle dockArea;
        private DateTime lastAnimationFrame = DateTime.UtcNow;

        /// <summary>
        /// 画球时的缩放比例 —— 不缓存、不查 DPI，直接按「窗口实际边长 ÷ 122」算。
        ///
        /// 之前是缓存一个 drawScale 字段，一旦它和窗口尺寸对不上（DPI 读数有偏差、
        /// 窗口被系统或别的代码改过），内容就会画到球外面。现在两边同源，不可能再错。
        /// </summary>
        private float DrawScale
        {
            get
            {
                int side = Math.Min(Width, Height);
                return side > 0 ? side / (float)BallSize : 1.0f;
            }
        }

        /// <summary>出图时用的版式编号（0 = 正式版式），只给对比图用。</summary>
        private int previewVariant;
        /// <summary>右键菜单关掉的时刻，用来挡掉"菜单刚关、手一抖点到球"的那一下。</summary>
        private DateTime menuClosedUtc = DateTime.MinValue;
        private static readonly TimeSpan MenuClickGuard = TimeSpan.FromMilliseconds(400);
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

            // 这个窗体是一张「固定像素画出来的圆」，尺寸必须自己说了算。
            // 用 WinForms 的字体自动缩放会出事：125% 缩放下它按宋体的字宽比例把宽度
            // 从 122 拉到 170，高度却原地不动 —— 圆就成了 170×122 的扁蛋。
            // 所以关掉自动缩放，DPI 放大自己按比例做：122 → 152，宽高永远相等。
            AutoScaleMode = AutoScaleMode.None;
            int ballPixels = UiScale.Px(BallSize);
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.White;
            TransparencyKey = Color.Empty;
            Opacity = 0.96;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

            // 尺寸最后再定：窗体还带着边框的时候设尺寸，Windows 会把 125% 缩放下的
            // 边框厚度算进去（宽度凭空多 18px），圆就又被拉扁了。
            Size = new Size(ballPixels, ballPixels);
            MinimumSize = Size;
            MaximumSize = Size;

            labelFont = UiScale.Font(6.8f, FontStyle.Bold);
            speedFont = UiScale.Font(25.0f, FontStyle.Bold);
            unitFont = UiScale.Font(8.8f, FontStyle.Regular);
            statFont = UiScale.Font(10.5f, FontStyle.Bold);
            rateFont = UiScale.Font(22.0f, FontStyle.Bold);
            rateUnitFont = UiScale.Font(8.5f, FontStyle.Regular);

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

        /// <summary>
        /// 把 96 DPI 下设计的像素值换算成当前屏幕的实际像素。
        /// 96 DPI → 原样；120 DPI（125%）→ ×1.25；144 DPI（150%）→ ×1.5。
        /// </summary>
        private static int ScaleForDpi(int pixels)
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    return (int)Math.Round(pixels * g.DpiX / 96.0);
                }
            }
            catch
            {
                return pixels;
            }
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

        /// <summary>右键菜单一关就记下时间，后面那一下点击要放过。</summary>
        protected override void OnContextMenuStripChanged(EventArgs e)
        {
            base.OnContextMenuStripChanged(e);
            if (ContextMenuStrip != null)
            {
                ContextMenuStrip.Closed += delegate { menuClosedUtc = DateTime.UtcNow; };
            }
        }

        /// <summary>用户在设置里改了缩放：球按新比例换尺寸，形状必须还是正圆。</summary>
        public void ApplyUiScale()
        {
            int pixels = UiScale.Px(BallSize);
            if (Width != pixels || Height != pixels)
            {
                // 以球心为锚点放大缩小：改缩放之后球停在原地长大，不会突然跳到屏幕另一头。
                Point center = new Point(Left + Width / 2, Top + Height / 2);

                MinimumSize = Size.Empty;
                MaximumSize = Size.Empty;
                Size = new Size(pixels, pixels);
                MinimumSize = Size;
                MaximumSize = Size;

                Left = center.X - Width / 2;
                Top = center.Y - Height / 2;
                UpdateWindowRegion();
            }

            KeepInsideWorkingArea();
            if (detailsForm != null)
            {
                detailsForm.ApplyUiScale();
            }
            if (launchPadForm != null)
            {
                launchPadForm.ApplyUiScale();
            }

            Invalidate();
        }

        /// <summary>显示项改了之后让详情面板重排一次（窗口高度会跟着变）。</summary>
        public void RefreshDetailsLayout()
        {
            SpeedDetailsForm.Trace("tray field change");
            if (detailsForm != null)
            {
                detailsForm.ApplyFieldChange();
            }
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
            SavePreview(path, 0);
        }

        public static void SavePreview(string path, int variant)
        {
            SavePreview(path, variant, 0.0f);
        }

        /// <param name="rocketAngleDeg">火箭在轨道上的角度：0 = 12 点方向，顺时针。自检用。</param>
        public static void SavePreview(string path, int variant, float rocketAngleDeg)
        {
            // 自检时可以指定速度（KB/s），用来看不同位数下排版稳不稳：
            //   NETSPEEDBALL_PREVIEW_DOWN=17.7 NETSPEEDBALL_PREVIEW_UP=7.2
            double downKb = ReadPreviewSpeed("NETSPEEDBALL_PREVIEW_DOWN", 24.7);
            double upKb = ReadPreviewSpeed("NETSPEEDBALL_PREVIEW_UP", 17.7);
            using (SpeedBallForm preview = new SpeedBallForm())
            using (Bitmap bitmap = new Bitmap(preview.Width, preview.Height))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                preview.previewVariant = variant;
                preview.rocketAngle = rocketAngleDeg;
                // 按当前 DPI 出图，跟屏幕上看到的一模一样
                g.Clear(Color.Transparent);
                preview.currentSample = new SpeedSample(downKb * 1024.0, upKb * 1024.0, true);
                preview.heldDownload.Snap(downKb * 1024.0);
                preview.heldUpload.Snap(upKb * 1024.0);
                preview.shownDownload = preview.heldDownload.Target;
                preview.shownUpload = preview.heldUpload.Target;
                preview.DrawSurface(g);
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        private static double ReadPreviewSpeed(string name, double fallback)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            double value;
            if (!string.IsNullOrEmpty(raw) && double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value) && value >= 0.0)
            {
                return value;
            }
            return fallback;
        }

        /// <summary>发射过程中某一帧的球窗口内容，给 <c>--render-launch</c> 自检用。</summary>
        public static void SaveLaunchPreview(string path, double elapsedSeconds)
        {
            using (SpeedBallForm preview = new SpeedBallForm())
            using (Bitmap bitmap = new Bitmap(preview.Width, preview.Height))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                preview.launchAnimating = elapsedSeconds >= 0.0;
                preview.overLaunchPad = elapsedSeconds < 0.0;
                preview.launchElapsed = Math.Max(0.0, elapsedSeconds);
                g.Clear(Color.Transparent);
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
            // 拖到发射台上方时往发射位轻轻吸：火箭的喷口正好落在坪面上，不然它悬在半空像没放好
            if (launchPadForm.Visible && launchPadForm.AcceptsBall(GetBallCenterScreen()))
            {
                Point dock = launchPadForm.GetDockLocation(Size);
                Location = new Point(
                    Location.X + (int)Math.Round((dock.X - Location.X) * 0.35),
                    Location.Y + (int)Math.Round((dock.Y - Location.Y) * 0.35));
            }
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
                // 再点一下球就把面板收起来 —— 跟原版一样，单击开、单击关。
                //
                // 唯一要防的误触：从右键菜单点完菜单项，鼠标就停在球边上，
                // 手稍微一动就点到球上，面板"啪"地消失，用户还以为是菜单干的。
                // 所以菜单刚关掉的那零点几秒内，这一下点击不算数。
                if (DateTime.UtcNow - menuClosedUtc < MenuClickGuard)
                {
                    return;
                }

                detailsForm.Hide();
                return;
            }

            // 先补数据再算位置：磁盘数量变了窗口高度会跟着变，位置得按最终高度摆
            detailsForm.PrimeResources();

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

            // 用户改了缩放 / 把球拖到另一块屏：系统会送来新 DPI，
            // 这里立刻按新比例重排，不用重启程序。
            if (m.Msg == NativeMethods.WM_DPICHANGED)
            {
                UiScale.Adopt((int)(m.WParam.ToInt64() & 0xFFFF));
                ApplyUiScale();
            }
        }

        /// <summary>
        /// 球是「固定像素画出来的圆」，宽高必须永远相等。
        ///
        /// 只要有任意一条路径（系统 DPI 变更、WinForms 自动缩放、外部 SetWindowPos）
        /// 把其中一边改了，这里立刻拉回正方形 —— 从根上杜绝"球被压扁"。
        /// </summary>
        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            if (width > 0 && height > 0 && width != height)
            {
                int side = Math.Max(width, height);
                width = side;
                height = side;
            }

            base.SetBoundsCore(x, y, width, height, specified);
        }

        /// <summary>自绘界面自己按 DPI 重排，不让 WinForms 再按字体比例缩一遍（它只会越缩越歪）。</summary>
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
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
            // 球里所有坐标都按 96 DPI 的 122×122 设计稿写。
            // 缩放比例不缓存、不查 DPI，直接由「窗口实际边长 ÷ 设计稿边长」得出：
            // 这样内容永远和窗口严丝合缝，DPI 读数偏差、系统拉伸都不会让它错位。
            float scale = DrawScale;
            g.ScaleTransform(scale, scale);

            Rectangle rect = new Rectangle(5, 5, BallSize - 11, BallSize - 11);
            // 回位淡入那一段要画普通的球，而不是大火箭 —— 否则用户会先看到一枚
            // 带长尾焰的大火箭在家里淡入，再"啪"地变回球
            bool returnPhase = launchAnimating && launchElapsed >= LaunchChargeSeconds + LaunchFlightSeconds;
            if (!returnPhase && (launchAnimating || overLaunchPad))
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
            // 造型是尖头朝上画的；angle 是轨道位置角（0 = 12 点），机头要指向前进方向（顺时针）
            g.RotateTransform(angle + 180.0f);
            g.ScaleTransform(0.84f, 0.84f);
            DrawRocketArtwork(g, visibleFlame, pulse);
            g.Restore(state);
        }

        // 球面小火箭和发射大火箭共用这一套造型，避免两个画面像两个程序。
        // 造型是**竖着**画的（尖头朝上），坐标原点在箭体中部；一共约 21 个单位高。
        // 从上到下：整流罩 → 黑色级间段 → 白色箭体 → 尾翼 → 发动机段 → 喇叭形喷口 → 喷焰。
        private static void DrawRocketArtwork(Graphics g, float flameStrength, float flameScale)
        {
            const float bodyHalf = 2.6f;
            const float noseTip = -10.5f;
            const float noseBase = -5.6f;
            const float bodyBottom = 7.0f;
            const float nozzleExit = 10.0f;

            // ---- 喷焰（画在箭体下面，所以先画）----
            if (flameStrength > 0.0f)
            {
                float length = (3.5f + 8.5f * flameStrength) * flameScale;
                using (GraphicsPath plume = new GraphicsPath())
                {
                    // 海平面喷焰：出口处比喷口略宽，往下先鼓一下再收成尖
                    plume.AddBezier(-1.9f, nozzleExit - 0.3f, -2.9f, nozzleExit + 1.2f, -2.3f, nozzleExit + length * 0.45f, 0, nozzleExit + length);
                    plume.AddBezier(0, nozzleExit + length, 2.3f, nozzleExit + length * 0.45f, 2.9f, nozzleExit + 1.2f, 1.9f, nozzleExit - 0.3f);
                    plume.CloseFigure();
                    using (Brush orange = new SolidBrush(Color.FromArgb(240, 255, 132, 30)))
                    {
                        g.FillPath(orange, plume);
                    }
                }

                // 内焰：黄白，短一些
                using (GraphicsPath core = new GraphicsPath())
                {
                    float inner = length * 0.62f;
                    core.AddBezier(-1.2f, nozzleExit, -1.6f, nozzleExit + 0.8f, -1.1f, nozzleExit + inner * 0.5f, 0, nozzleExit + inner);
                    core.AddBezier(0, nozzleExit + inner, 1.1f, nozzleExit + inner * 0.5f, 1.6f, nozzleExit + 0.8f, 1.2f, nozzleExit);
                    core.CloseFigure();
                    using (Brush yellow = new SolidBrush(Color.FromArgb(255, 255, 232, 150)))
                    {
                        g.FillPath(yellow, core);
                    }
                }

                // 喷口正下方一点白热
                using (Brush white = new SolidBrush(Color.FromArgb(230, 255, 255, 240)))
                {
                    g.FillEllipse(white, -0.7f, nozzleExit - 0.2f, 1.4f, 2.4f);
                }

                // 很淡的暖光，让喷焰周围的箭体被照亮一点（是照明，不是光圈）
                using (GraphicsPath halo = new GraphicsPath())
                {
                    halo.AddEllipse(-4.5f, nozzleExit - 2.0f, 9.0f, 6.0f + length * 0.5f);
                    using (PathGradientBrush glow = new PathGradientBrush(halo))
                    {
                        glow.CenterColor = Color.FromArgb((int)(60 * flameStrength), 255, 170, 80);
                        glow.SurroundColors = new[] { Color.FromArgb(0, 255, 170, 80) };
                        g.FillPath(glow, halo);
                    }
                }
            }

            // ---- 尾翼 ----
            using (Brush fin = new SolidBrush(Color.FromArgb(255, 46, 54, 66)))
            {
                g.FillPolygon(fin, new[] { new PointF(-bodyHalf, 3.8f), new PointF(-4.2f, 7.2f), new PointF(-bodyHalf, 7.2f) });
                g.FillPolygon(fin, new[] { new PointF(bodyHalf, 3.8f), new PointF(4.2f, 7.2f), new PointF(bodyHalf, 7.2f) });
            }

            // ---- 喷口：喇叭形 ----
            using (GraphicsPath bell = new GraphicsPath())
            using (Brush bellBrush = new SolidBrush(Color.FromArgb(255, 34, 39, 48)))
            {
                bell.AddPolygon(new[]
                {
                    new PointF(-1.4f, 8.0f), new PointF(1.4f, 8.0f), new PointF(2.1f, nozzleExit), new PointF(-2.1f, nozzleExit)
                });
                g.FillPath(bellBrush, bell);
            }

            using (Pen bellLight = new Pen(Color.FromArgb(255, 92, 102, 116), 0.5f))
            {
                g.DrawLine(bellLight, -1.3f, 8.3f, -1.8f, nozzleExit - 0.2f);
            }

            // ---- 发动机段 ----
            using (LinearGradientBrush skirt = new LinearGradientBrush(new RectangleF(-bodyHalf, 7.0f, bodyHalf * 2, 1.2f),
                       Color.FromArgb(255, 72, 80, 92), Color.FromArgb(255, 40, 46, 56), LinearGradientMode.Horizontal))
            {
                g.FillRectangle(skirt, -bodyHalf, 7.0f, bodyHalf * 2, 1.2f);
            }

            // ---- 箭体：左亮右暗，是圆柱不是纸片 ----
            using (LinearGradientBrush bodyBrush = new LinearGradientBrush(
                       new RectangleF(-bodyHalf, noseBase, bodyHalf * 2, bodyBottom - noseBase),
                       Color.FromArgb(255, 253, 254, 255), Color.FromArgb(255, 198, 208, 219), LinearGradientMode.Horizontal))
            using (Pen bodyEdge = new Pen(Color.FromArgb(255, 112, 126, 142), 0.55f))
            {
                g.FillRectangle(bodyBrush, -bodyHalf, noseBase, bodyHalf * 2, bodyBottom - noseBase);
                g.DrawRectangle(bodyEdge, -bodyHalf, noseBase, bodyHalf * 2, bodyBottom - noseBase);
            }

            // 两道面板接缝
            using (Pen seam = new Pen(Color.FromArgb(255, 206, 214, 223), 0.4f))
            {
                g.DrawLine(seam, -bodyHalf + 0.3f, -1.4f, bodyHalf - 0.3f, -1.4f);
                g.DrawLine(seam, -bodyHalf + 0.3f, 2.6f, bodyHalf - 0.3f, 2.6f);
            }

            // ---- 黑色级间段 ----
            using (LinearGradientBrush band = new LinearGradientBrush(new RectangleF(-bodyHalf, noseBase, bodyHalf * 2, 1.3f),
                       Color.FromArgb(255, 62, 68, 78), Color.FromArgb(255, 22, 25, 31), LinearGradientMode.Horizontal))
            {
                g.FillRectangle(band, -bodyHalf, noseBase, bodyHalf * 2, 1.3f);
            }

            // ---- 整流罩 ----
            using (GraphicsPath nose = new GraphicsPath())
            using (LinearGradientBrush noseBrush = new LinearGradientBrush(
                       new RectangleF(-bodyHalf, noseTip, bodyHalf * 2, noseBase - noseTip),
                       Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 206, 216, 226), LinearGradientMode.Horizontal))
            using (Pen noseEdge = new Pen(Color.FromArgb(255, 112, 126, 142), 0.55f))
            {
                nose.AddBezier(0, noseTip, 0.9f, noseTip + 0.3f, 2.2f, noseTip + 2.8f, bodyHalf, noseBase);
                nose.AddLine(bodyHalf, noseBase, -bodyHalf, noseBase);
                nose.AddBezier(-bodyHalf, noseBase, -2.2f, noseTip + 2.8f, -0.9f, noseTip + 0.3f, 0, noseTip);
                nose.CloseFigure();
                g.FillPath(noseBrush, nose);
                g.DrawPath(noseEdge, nose);
            }
        }

        /// <summary>动画时钟的起点，给火焰闪动、火花这类"和发射进度无关"的随机感用。</summary>
        private static readonly DateTime ClockOrigin = DateTime.UtcNow;

        private void DrawLaunchRocketMode(Graphics g, Rectangle rect)
        {
            double now = (DateTime.UtcNow - ClockOrigin).TotalSeconds;
            bool charging = launchAnimating && launchElapsed < LaunchChargeSeconds;
            bool flying = launchAnimating && launchElapsed >= LaunchChargeSeconds;
            float charge = charging ? (float)(launchElapsed / LaunchChargeSeconds) : 0.0f;
            float flight = flying
                ? Math.Min(1.0f, (float)((launchElapsed - LaunchChargeSeconds) / LaunchFlightSeconds))
                : 0.0f;

            // 火焰闪动：两个不成倍数的正弦叠加，看起来像随机
            float flicker = 0.9f + 0.06f * (float)Math.Sin(now * 41.0) + 0.04f * (float)Math.Sin(now * 67.0 + 1.3);

            // flame 同时是"喷焰强度"和"喷焰长度"：0 = 发动机没点着
            float flame;
            float lift;
            float shakeX = 0.0f;
            if (charging)
            {
                // 点火：喷焰从半截长到全开，箭体被推着往下蹲、越抖越厉害
                flame = (0.5f + 0.5f * charge) * flicker;
                lift = 3.0f * (1.0f - charge);
                shakeX = (float)Math.Sin(launchElapsed * 82.0) * (0.6f + charge * 1.4f);
            }
            else if (flying)
            {
                flame = (1.0f + 0.18f * flight) * flicker;
                lift = 0.0f;
            }
            else
            {
                // 悬停在发射台上：发动机没点火，只是被吊着轻轻浮动
                flame = 0.0f;
                lift = (float)Math.Sin(now * 3.2) * 1.0f;
            }

            // 火箭立在窗口中间偏上：喷口大约在 y=77（设计稿），喷焰往下到 y≈118
            PointF center = new PointF(rect.X + rect.Width / 2.0f + shakeX, rect.Y + LaunchRocketCenterY - lift);
            DrawLaunchRocketBody(g, center, LaunchRocketScale, 0.0f, flame > 0.0f, flame);
        }

        /// <summary>发射时大火箭在球窗口里的位置和大小（设计稿坐标）。</summary>
        internal const float LaunchRocketCenterY = 46.0f;
        internal const float LaunchRocketScale = 3.1f;

        /// <summary>喷口出口在设计稿里的 y：火箭中部 + 喷口到中部的距离（10）× 比例。发射台靠它对位。</summary>
        internal static float LaunchNozzleDesignY
        {
            get { return LaunchRocketCenterY + 10.0f * LaunchRocketScale; }
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
                DrawCenteredLine(g, "\u79bb\u7ebf", speedFont, 35, Color.FromArgb(28, 34, 48), BallSize - 30);
                DrawCenteredLine(g, "WAITING", unitFont, 70, Color.FromArgb(104, 118, 136), BallSize - 44);
                return;
            }

            RateText main = useMegabits
                ? SpeedFormatter.FormatBits(heldDownload.Target)
                : SpeedFormatter.FormatMegabytesRate(heldDownload.Target);
            string down = SpeedFormatter.FormatCompact(heldDownload.Target);
            string up = SpeedFormatter.FormatCompact(heldUpload.Target);

            // 数字别太贪大：球是 122 见方，25 磅的数字会把单位挤到身上，
            // 22 磅配上下面留出的空档才是能看的样子。
            //
            // 字体必须是常驻实例：这里每秒要画 30 帧，每帧 new Font 又不 Dispose，
            // GDI 句柄几分钟就耗光，之后文字会突然画不出来 —— 之前就是这个坑。
            Font bigFont = rateFont;
            Font smallUnitFont = rateUnitFont;
            Font variantFont = null;
            int numberY = 27;
            int unitY = 63;
            int statY = 85;
            if (previewVariant == 1)
            {
                variantFont = UiScale.Font(20.0f, FontStyle.Bold);
                bigFont = variantFont;
                numberY = 29;
                unitY = 62;
                statY = 83;
            }
            else if (previewVariant == 2)
            {
                bigFont = speedFont;
                smallUnitFont = unitFont;
                numberY = 28;
                unitY = 61;
                statY = 79;
            }
            else if (previewVariant == 3)
            {
                variantFont = UiScale.Font(18.0f, FontStyle.Bold);
                bigFont = variantFont;
                numberY = 31;
                unitY = 61;
                statY = 82;
            }

            try
            {
                DrawCenteredLine(g, main.Value, bigFont, numberY, Color.FromArgb(20, 27, 42), BallSize - 30);
                DrawCenteredLine(g, main.Unit, smallUnitFont, unitY, Color.FromArgb(70, 84, 102), BallSize - 48);
                DrawStatPair(g, up, down, statY);
            }
            finally
            {
                if (variantFont != null)
                {
                    variantFont.Dispose();
                }
            }
        }

        private void DrawStatPair(Graphics g, string up, string down, int y)
        {
            // 缩放比例只按「最宽可能的情况」算一次，和当前显示的数字无关。
            // 之前是按当前文字量宽度：17.7K 变成 7.2K 时整行就缩一下，连箭头一起忽大忽小，
            // 字体在缩放变化时粗细也会跟着抖 —— 这就是"箭头突然变大变小变粗变细"的原因。
            //
            // 布局（设计稿像素，箭头不缩放，只有文字缩放）：
            //   [箭头] 2.5 [数字格 slot*scale] pairGap [箭头] 2.5 [数字格 slot*scale]
            const float textGap = 2.5f;
            const float pairGap = 8.0f;
            float slot = Math.Max(g.MeasureString("88.8M", statFont).Width, g.MeasureString("1023K", statFont).Width);
            float maxWidth = BallSize - 42.0f;
            float arrowSpan = (ArrowWidth + textGap) * 2.0f;
            float scale = Math.Min(1.0f, (maxWidth - arrowSpan) / (slot * 2.0f + pairGap));
            float rowWidth = arrowSpan + (slot * 2.0f + pairGap) * scale;
            float x = (BallSize - rowWidth) / 2.0f;

            // 这一行正好压在火箭绕圈的轨道上，火箭转到下方就会从文字上碾过去。
            // 给文字垫一块几乎不透明的小牌子，火箭从牌子底下经过 —— 两者不再叠在一起。
            using (GraphicsPath chip = new GraphicsPath())
            {
                RectangleF chipRect = new RectangleF(x - 7.0f, y - 2.0f, rowWidth + 14.0f, 17.0f);
                float d = chipRect.Height;
                chip.AddArc(chipRect.X, chipRect.Y, d, d, 90, 180);
                chip.AddArc(chipRect.Right - d, chipRect.Y, d, d, 270, 180);
                chip.CloseFigure();
                using (GraphicsPath chipShadow = new GraphicsPath())
                {
                    RectangleF s = new RectangleF(chipRect.X, chipRect.Y + 1, chipRect.Width, chipRect.Height);
                    float sd = s.Height;
                    chipShadow.AddArc(s.X, s.Y, sd, sd, 90, 180);
                    chipShadow.AddArc(s.Right - sd, s.Y, sd, sd, 270, 180);
                    chipShadow.CloseFigure();
                    using (Brush sh = new SolidBrush(Color.FromArgb(36, 60, 100, 140)))
                    {
                        g.FillPath(sh, chipShadow);
                    }
                }
                using (Brush chipFill = new SolidBrush(Color.FromArgb(238, 255, 255, 255)))
                using (Pen chipEdge = new Pen(Color.FromArgb(200, 176, 198, 222), 0.8f))
                {
                    g.FillPath(chipFill, chip);
                    g.DrawPath(chipEdge, chip);
                }
            }

            // 文字缩放后字形大约落在 y+2*scale ~ y+12.5*scale，视觉中心 ≈ y + 7.25*scale
            float arrowCy = y + 7.25f * scale;
            float upTextX = x + ArrowWidth + textGap;
            float upWidth = g.MeasureString(up, statFont).Width * scale;
            float downArrowCx = upTextX + slot * scale + pairGap * scale + ArrowWidth / 2.0f;
            float downTextX = downArrowCx + ArrowWidth / 2.0f + textGap;

            using (Brush upBrush = new SolidBrush(Color.FromArgb(255, 12, 160, 82)))
            using (Brush downBrush = new SolidBrush(Color.FromArgb(255, 28, 108, 228)))
            {
                GraphicsState state = g.Save();
                g.ScaleTransform(scale, scale);
                g.DrawString(up, statFont, upBrush, upTextX / scale, y / scale);
                g.DrawString(down, statFont, downBrush, downTextX / scale, y / scale);
                g.Restore(state);

                DrawArrow(g, x + ArrowWidth / 2.0f, arrowCy, true, upBrush);
                DrawArrow(g, downArrowCx, arrowCy, false, downBrush);
            }
        }

        /// <summary>箭头的宽高（设计稿像素，不随文字缩放）。</summary>
        private const float ArrowWidth = 8.5f;
        private const float ArrowHeight = 8.5f;

        /// <summary>
        /// 一支真正的箭：三角头 + 短杆，以 (cx, cy) 为中心。纯三角在 8px 下就是个色点，
        /// 分不清是尖朝上还是尖朝下；带一根杆子，方向一眼就认出来。
        /// </summary>
        private static void DrawArrow(Graphics g, float cx, float cy, bool up, Brush brush)
        {
            float half = ArrowWidth / 2.0f;
            float shaft = 1.5f;
            float head = ArrowHeight * 0.48f;
            float top = cy - ArrowHeight / 2.0f;
            float bottom = cy + ArrowHeight / 2.0f;
            PointF[] points = up
                ? new[]
                {
                    new PointF(cx, top), new PointF(cx + half, top + head), new PointF(cx + shaft, top + head),
                    new PointF(cx + shaft, bottom), new PointF(cx - shaft, bottom), new PointF(cx - shaft, top + head),
                    new PointF(cx - half, top + head)
                }
                : new[]
                {
                    new PointF(cx, bottom), new PointF(cx + half, bottom - head), new PointF(cx + shaft, bottom - head),
                    new PointF(cx + shaft, top), new PointF(cx - shaft, top), new PointF(cx - shaft, bottom - head),
                    new PointF(cx - half, bottom - head)
                };
            g.FillPolygon(brush, points);
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
                // 这里的坐标系是「96 DPI 设计稿」：g 已经被 DrawSurface 整体缩放过，
                // 所以居中要用设计稿宽度 BallSize，绝不能用窗口的真实像素 Width
                // —— 两者在 125%/150% 下差一倍比例，文字会一路往右漂。
                float x = (BallSize - size.Width) / 2.0f;
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
            // 球又被按住了：上一发如果还在播冲击波，停下回到待命
            launchPadForm.CancelBurst();
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
            launchReturnLocation = dragOriginLocation;
            launchReturnOpacity = Opacity;

            // 松手的位置可能离发射位还差几十像素：起飞点按发射位算，
            // 点火的前一小段先从松手处滑到正中（像被夹具拉到位），然后才升空
            launchUseDock = launchPadForm.Visible;
            launchSnapFrom = Location;
            launchStartLocation = launchUseDock ? launchPadForm.GetDockLocation(Size) : Location;

            Rectangle area = Screen.FromControl(this).WorkingArea;
            int targetX = launchStartLocation.X + 18;
            int targetY = area.Top - Height - 40;
            launchTargetLocation = new Point(targetX, targetY);

            // 发射台不跟着消失，而是播一段点火白烟（约 0.55 秒）再自己收起
            launchPadForm.PlayBurst();
            overLaunchPad = true;
            SetLaunchVisualMode(true);
            Invalidate();
        }

        private void UpdateLaunchAnimation(double elapsed)
        {
            const double returnDuration = 0.28;
            const double totalDuration = LaunchChargeSeconds + LaunchFlightSeconds + returnDuration;
            launchElapsed += elapsed;

            if (launchElapsed < LaunchChargeSeconds)
            {
                double charge = launchElapsed / LaunchChargeSeconds;
                int shakeX = (int)Math.Round(Math.Sin(launchElapsed * 82.0) * (1.0 + charge * 2.2));
                int compression = (int)Math.Round(Math.Sin(charge * Math.PI) * 5.0);
                Point baseLocation = launchStartLocation;
                if (launchUseDock)
                {
                    baseLocation = Lerp(launchSnapFrom, launchStartLocation, EaseOutCubic(Math.Min(1.0, charge / 0.4)));
                }
                Location = new Point(baseLocation.X + shakeX, baseLocation.Y + compression);
                return;
            }

            if (launchElapsed < LaunchChargeSeconds + LaunchFlightSeconds)
            {
                double t = Math.Min(1.0, (launchElapsed - LaunchChargeSeconds) / LaunchFlightSeconds);
                // 起步慢、越飞越快：指数 2.0 让前三分之一几乎还在原地蓄力，
                // 后半段一口气冲出去，比匀加速更像"点火"
                double accelerated = Math.Pow(t, 2.0);
                // 不再左右摇晃：直着往上冲才像点火，晃着飞像失控
                Location = Lerp(launchStartLocation, launchTargetLocation, accelerated);
                Opacity = Math.Max(0.0, 0.96 - Math.Max(0.0, t - 0.72) / 0.28 * 0.96);
                return;
            }

            // 飞出屏幕后不是瞬移回来，而是从上方 8px 处落回原位并淡入 —— 瞬移会让人以为球"闪"了一下
            double back = Math.Min(1.0, (launchElapsed - LaunchChargeSeconds - LaunchFlightSeconds) / returnDuration);
            double eased = EaseOutCubic(back);
            Location = new Point(launchReturnLocation.X, launchReturnLocation.Y - (int)Math.Round((1.0 - eased) * 8.0));
            Opacity = launchReturnOpacity * eased;

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
                if (rateFont != null) rateFont.Dispose();
                if (rateUnitFont != null) rateUnitFont.Dispose();
                if (appIcon != null) appIcon.Dispose();
                if (launchPadForm != null) launchPadForm.Dispose();
                if (detailsForm != null) detailsForm.Dispose();
            }

            base.Dispose(disposing);
        }
    }


    /// <summary>
    /// 「系统详情」面板：网络 + CPU + 内存 + 磁盘 + GPU + 网卡。
    ///
    /// 口径一律对齐任务管理器「性能」页：CPU 利用率是全局占用，磁盘是「活动时间」
    /// 而不是「用了多少空间」，内存「已缓存」对应系统缓存。
    /// 目的就一个 —— 不用再开任务管理器，瞄一眼这块卡片就知道机器什么状态。
    /// </summary>
    /// <summary>
    /// 「系统详情」面板：网络 + CPU + 内存 + 磁盘 + GPU + 网卡。
    ///
    /// 口径一律对齐任务管理器「性能」页：CPU 利用率是全局占用，磁盘是「活动时间」
    /// 而不是「用了多少空间」，内存「已缓存」= 待机页 + 已修改页。
    ///
    /// 哪一行显示、哪一行不显示由用户自己定：在面板上点右键，勾选即生效，
    /// 配置存在注册表里，下次打开还是这套。关掉一行之后下面的行会自动补位，
    /// 不会在卡片里留一个空洞。
    /// </summary>
    internal sealed class SpeedDetailsForm : Form
    {
        private const int MaxSamples = 60;
        /// <summary>资源面板最多画几块磁盘。 Dock 里插一排 U 盘也不会把窗口撑出屏幕。</summary>
        private const int MaxDiskRows = 8;
        /// <summary>网卡那一栏最多列几块，虚拟网卡（VMware/Hyper-V）能有一打。</summary>
        private const int MaxLinkRows = 3;
        /// <summary>两张卡片之间的空隙。</summary>
        private const int CardGap = 8;
        /// <summary>左右边距。所有卡片共用同一条竖线，视觉上才整齐。</summary>
        private const int SideMargin = 14;
        /// <summary>一行字段的高度。8.2pt 的字体配 19px 行距，中英文都不挤。</summary>
        private const int TextRow = 19;
        /// <summary>一块磁盘占的高度（标题行 + 进度条 + 读写/容量行）。</summary>
        private const int DiskRowFull = 48;
        /// <summary>只显示活动率时，一块磁盘占的高度。</summary>
        private const int DiskRowTight = 27;
        /// <summary>一行网卡占的高度。</summary>
        private const int LinkRowHeight = 17;

        // ─────────────────────── 可开关的字段 ───────────────────────
        //
        // id 会写进注册表，一旦发布就别改；label 是给用户看的，随便调。
        // 顺序就是卡片里从左到右、从上到下的顺序，关掉中间的项后面的自动往前顶。

        private sealed class FieldDef
        {
            public readonly string Id;
            public readonly string Label;

            public FieldDef(string id, string label)
            {
                Id = id;
                Label = label;
            }
        }

        private static readonly FieldDef[] NetworkFields =
        {
            new FieldDef("net.graph", "60 秒速度曲线"),
            new FieldDef("net.today", "今日下载/上传"),
            new FieldDef("net.month", "本月下载/上传"),
        };

        // 分成几小撮，右键菜单每层就只剩几行，不会撑出屏幕。
        // 合并起来的顺序就是面板上的顺序，所以拆的时候保持原来的先后。
        private static readonly FieldDef[] CpuFreqFields =
        {
            new FieldDef("cpu.speed", "速度"),
            new FieldDef("cpu.base", "基准速度"),
        };

        private static readonly FieldDef[] CpuRunFields =
        {
            new FieldDef("cpu.uptime", "运行时长"),
            new FieldDef("cpu.process", "进程"),
            new FieldDef("cpu.thread", "线程"),
            new FieldDef("cpu.handle", "句柄"),
        };

        private static readonly FieldDef[] CpuCoreFields =
        {
            new FieldDef("cpu.cores", "内核"),
            new FieldDef("cpu.logical", "逻辑处理器"),
            new FieldDef("cpu.socket", "插槽"),
        };

        private static readonly FieldDef[] CpuCacheFields =
        {
            new FieldDef("cpu.l1", "L1 缓存"),
            new FieldDef("cpu.l2", "L2 缓存"),
            new FieldDef("cpu.l3", "L3 缓存"),
        };

        private static readonly FieldDef[] CpuFields = MergeFields(
            CpuFreqFields, CpuRunFields, CpuCoreFields, CpuCacheFields);

        private static readonly FieldDef[] MemUseFields =
        {
            new FieldDef("mem.used", "已用/总量"),
            new FieldDef("mem.available", "可用"),
            new FieldDef("mem.cached", "已缓存"),
        };

        private static readonly FieldDef[] MemSystemFields =
        {
            new FieldDef("mem.commit", "已提交"),
            new FieldDef("mem.paged", "分页缓冲池"),
            new FieldDef("mem.nonpaged", "非分页缓冲池"),
        };

        private static readonly FieldDef[] MemSpecFields =
        {
            new FieldDef("mem.speed", "内存速度"),
            new FieldDef("mem.slot", "插槽占用"),
            new FieldDef("mem.type", "内存类型"),
            new FieldDef("mem.module", "单条容量"),
        };

        private static readonly FieldDef[] MemoryFields = MergeFields(
            MemUseFields, MemSystemFields, MemSpecFields);

        private static readonly FieldDef[] DiskFields =
        {
            new FieldDef("disk.io", "读写速度"),
            new FieldDef("disk.capacity", "容量"),
        };

        private static readonly FieldDef[] GpuFields =
        {
            new FieldDef("gpu.dedicated", "专用显存"),
            new FieldDef("gpu.shared", "共享显存"),
            new FieldDef("gpu.driver", "驱动版本"),
            new FieldDef("gpu.date", "驱动日期"),
        };

        private static readonly FieldDef[] GpuModelField =
        {
            new FieldDef("gpu.model", "显卡型号"),
        };

        private static readonly FieldDef[] CpuModelField =
        {
            new FieldDef("cpu.model", "CPU 型号"),
        };

        private static readonly FieldDef[] NicFields =
        {
            new FieldDef("nic.card", "网络适配器整块"),
        };

        // 型号是卡片标题下面那行小字，跟显存这些是同一张卡上的东西，菜单里放在一组。
        private static readonly FieldDef[] GpuGroupFields = MergeFields(GpuModelField, GpuFields);

        private static FieldDef[] MergeFields(params FieldDef[][] sets)
        {
            List<FieldDef> all = new List<FieldDef>();
            for (int i = 0; i < sets.Length; i++)
            {
                all.AddRange(sets[i]);
            }

            return all.ToArray();
        }

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
        private double shownGpu;
        private double[] shownDisks = new double[0];
        private bool shownPrimed;

        // 带死区的目标：采样在阈值内晃就不换目标，界面上的数字才不会一直蹦。
        private readonly HeldValue heldDownload = new HeldValue(0.25, 2048.0);
        private readonly HeldValue heldUpload = new HeldValue(0.25, 2048.0);
        private readonly HeldValue heldCpu = new HeldValue(0.12, 1.0);
        private readonly HeldValue heldMemory = new HeldValue(0.06, 0.5);
        private readonly HeldValue heldGpu = new HeldValue(0.12, 1.0);
        private readonly List<HeldValue> heldDisks = new List<HeldValue>();

        private Font titleFont;
        private Font cardTitleFont;
        private Font valueFont;
        private Font strongFont;
        private Font smallFont;

        /// <summary>当前 DPI 比例（96 DPI = 1.0）。面板里所有坐标都按 96 DPI 写，画的时候整体乘它。</summary>
        private float uiScale;

        /// <summary>用户设定缩放对应的比例（屏幕装不下时 uiScale 会比它小）。</summary>
        private float targetScale;

        /// <summary>设计稿宽度。窗口实际宽度 = 这个值 × DPI 比例。</summary>
        private const int DesignWidth = 430;

        /// <summary>画图时的逻辑宽度（永远 430），和窗口的真实像素宽度区分开。</summary>
        private int UWidth
        {
            get { return DesignWidth; }
        }

        /// <summary>画图时的逻辑高度，由真实像素高度换算回来。</summary>
        private int UHeight
        {
            get { return uiScale > 0 ? (int)Math.Round(Height / uiScale) : Height; }
        }

        /// <summary>字体一律按 96 DPI 的像素尺寸造，缩放交给画布的整体变换。</summary>
        private void BuildFonts()
        {
            DisposeFont(ref titleFont);
            DisposeFont(ref cardTitleFont);
            DisposeFont(ref valueFont);
            DisposeFont(ref strongFont);
            DisposeFont(ref smallFont);

            titleFont = UiScale.Font(12.0f, FontStyle.Bold);
            cardTitleFont = UiScale.Font(9.5f, FontStyle.Bold);
            valueFont = UiScale.Font(10.0f, FontStyle.Bold);
            strongFont = UiScale.Font(8.2f, FontStyle.Bold);
            smallFont = UiScale.Font(8.2f, FontStyle.Regular);
        }

        private static void DisposeFont(ref Font font)
        {
            if (font != null)
            {
                font.Dispose();
                font = null;
            }
        }

        /// <summary>DPI 变了（用户在设置里改了缩放）：重新算尺寸和字体，面板跟着变。</summary>
        public void ApplyUiScale()
        {
            float next = UiScale.Factor;
            // 和「用户设定的比例」比，不是和 uiScale 比 —— 屏幕装不下时 uiScale
            // 会被主动压低，拿它做比较会让面板每秒重排一次、一直抖。
            if (Math.Abs(next - targetScale) < 0.01f)
            {
                return;
            }

            targetScale = next;
            uiScale = next;
            BuildFonts();
            Width = UiScale.Px(DesignWidth);
            SyncHeight();
        }

        private readonly Brush darkBrush = new SolidBrush(Color.FromArgb(30, 42, 60));
        private readonly Brush mutedBrush = new SolidBrush(Color.FromArgb(94, 112, 134));
        private readonly Brush faintBrush = new SolidBrush(Color.FromArgb(142, 158, 176));
        private readonly Brush downBrush = new SolidBrush(Color.FromArgb(35, 119, 238));
        private readonly Brush upBrush = new SolidBrush(Color.FromArgb(16, 178, 92));

        private static readonly Color CpuColor = Color.FromArgb(242, 126, 42);
        private static readonly Color MemoryColor = Color.FromArgb(35, 119, 238);
        private static readonly Color DiskColor = Color.FromArgb(16, 178, 92);
        private static readonly Color GpuColor = Color.FromArgb(138, 96, 230);
        private static readonly Color LinkColor = Color.FromArgb(22, 160, 170);

        /// <summary>各卡片的位置，每帧按当前可见字段和磁盘/网卡数量重算一次。</summary>
        private Rectangle cpuCard;
        private Rectangle memoryCard;
        private Rectangle diskCard;
        private Rectangle gpuCard;
        private Rectangle linkCard;

        /// <summary>网络区画到哪儿结束，下面的卡片从这里开始往下排。</summary>
        private int networkBottom;
        /// <summary>所有卡片画完之后的位置，也就是窗口应有的高度。</summary>
        private int contentBottom;

        /// <summary>右键菜单，第一次点右键时才建（建菜单要几十毫秒，别放在构造里）。</summary>
        private ContextMenuStrip fieldMenu;

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
            // 逻辑坐标（96 DPI 设计稿），判断点击时要把鼠标坐标换算过来
            get { return new Rectangle(UWidth - 34, 8, 26, 26); }
        }

        /// <summary>鼠标给的是真实像素，先换算回设计稿坐标再判断。</summary>
        private Point ToLogical(Point point)
        {
            if (uiScale <= 0)
            {
                return point;
            }

            return new Point((int)Math.Round(point.X / uiScale), (int)Math.Round(point.Y / uiScale));
        }

        private Rectangle ToPhysical(Rectangle rect)
        {
            // 用面板自己的 uiScale，不用全局 DPI：两者混用的话，
            // 关闭按钮这类"点到才算"的区域会和画出来的位置差几像素。
            return new Rectangle(
                (int)Math.Round(rect.X * uiScale),
                (int)Math.Round(rect.Y * uiScale),
                (int)Math.Round(rect.Width * uiScale),
                (int)Math.Round(rect.Height * uiScale));
        }

        /// <summary>
        /// 把绘制比例对齐到窗口实际宽度 —— 窗口宽度是唯一事实来源。
        ///
        /// 这样即使 DPI 读数、系统缩放、窗体尺寸三者里有一个不对劲，
        /// 面板内容也永远和窗口同宽，不会出现"字画到面板外面"或"下半截被裁掉"。
        /// </summary>
        private void SyncScaleFromWindow()
        {
            int width = ClientSize.Width;
            if (width <= 0)
            {
                return;
            }

            float next = width / (float)DesignWidth;
            if (next < 0.3f || next > 6.0f)
            {
                return;
            }

            uiScale = next;
        }

        public SpeedDetailsForm(TrafficStore store)
        {
            trafficStore = store;
            Text = "System details";
            // 静态信息（CPU 型号、内存条频率、GPU 名字）在后台读一次，读好了自动重绘。
            SystemInfoProvider.EnsureLoading(delegate { Invalidate(); });
            // 性能计数器第一次创建要花好几秒（等系统的计数器注册表），
            // 现在就把这条最贵的路在后台走掉，别等面板打开时再让 UI 线程等。
            resourceMonitor.PrimeAsync();

            // 面板是纯手工画的，尺寸由自己算：设计稿 430 宽，缩放到 125% 就是 537。
            // 绝不能让 WinForms 按字体自动缩放 —— 它只放大宽度、不放大高度，卡片会歪。
            AutoScaleMode = AutoScaleMode.None;
            uiScale = UiScale.Factor;
            targetScale = uiScale;
            BuildFonts();
            Size = new Size(UiScale.Px(DesignWidth), UiScale.Px(780));
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

        /// <summary>
        /// 自绘面板自己按 DPI 重排，不让 WinForms 再缩一遍。
        /// 两套缩放叠在一起时，宽度按字号比例、高度按窗口比例，卡片就会错位。
        /// </summary>
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
        }

        /// <summary>用户在设置里改了缩放：立刻按新 DPI 重排，不用等下一秒的轮询。</summary>
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == NativeMethods.WM_DPICHANGED)
            {
                UiScale.Adopt((int)(m.WParam.ToInt64() & 0xFFFF));
                ApplyUiScale();
            }
        }

        /// <summary>面板一露头就把 GPU 采样线程拉起来，藏起来就停掉。</summary>
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            Trace("visible=" + Visible + " " + BoundsText(this));
            resourceMonitor.SetActive(Visible);
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

        /// <summary>
        /// 诊断日志：只有排查"点菜单后面板消失"这类问题时才需要看。
        /// 写到 %LOCALAPPDATA%\NetSpeedBall\details-debug.log，正常使用时每次操作一行。
        /// </summary>
        public static void Trace(string message)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NetSpeedBall");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "details-debug.log");

                // 诊断日志只在操作时写几行，但也不能让它无限长：超过半兆就重来。
                try
                {
                    FileInfo info = new FileInfo(file);
                    if (info.Exists && info.Length > 512 * 1024)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                }

                File.AppendAllText(file,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch
            {
            }
        }

        private static string BoundsText(Form form)
        {
            return form.Width + "x" + form.Height + "@" + form.Left + "," + form.Top +
                   " visible=" + form.Visible;
        }

        /// <summary>显示项改了之后重排窗口：卡片高度跟着变，窗口也得跟着变。</summary>
        public void ApplyFieldChange()
        {
            bool wasVisible = Visible;
            Trace("apply requested " + BoundsText(this));

            // 不能在菜单的点击回调里直接改窗口尺寸：那一瞬间 ToolStrip 还在处理鼠标消息，
            // owner 窗口突然变高变矮会打乱它关闭流程。等这轮消息处理完再动手。
            try
            {
                BeginInvoke(new Action(delegate
                {
                    Trace("apply before " + BoundsText(this));
                    SyncHeight();
                    Invalidate();

                    // 兜底：改显示项绝不该把面板弄没。真被谁藏了，这里当场请回来，
                    // 日志里会留下是谁藏的（SetVisibleCore 那段会打调用栈）。
                    if (wasVisible && !Visible)
                    {
                        Trace("!!! panel disappeared during field change, restoring");
                        Show();
                    }

                    Trace("apply after " + BoundsText(this));
                }));
            }
            catch (InvalidOperationException)
            {
                // 窗口已经销毁，没什么可重排的
            }
        }

        /// <summary>记下每一次被隐藏的调用栈，用来抓"面板自己消失"的真凶。</summary>
        protected override void SetVisibleCore(bool value)
        {
            if (!value && Visible)
            {
                Trace("hide requested:\r\n" + Environment.StackTrace);
            }

            base.SetVisibleCore(value);
        }

        /// <summary>
        /// 面板刚被打开时先取一次最新数据，别让用户先看到几秒前的旧数字。
        /// 只补资源那几块，流量曲线和今日/本月统计一直是最新的。
        /// </summary>
        public void PrimeResources()
        {
            // 预热已经跑完就同步读一次（只要几毫秒）；没跑完就用后台那份缓存，
            // 一份都没有就先按空数据把窗口摆出来 —— 绝不能在 UI 线程上等那几秒。
            resourceSnapshot = resourceMonitor.IsPrimed || resourceMonitor.PrimeFailed
                ? resourceMonitor.Read()
                : resourceMonitor.WaitForPrime(120);

            SystemResourceSnapshot snapshot = resourceSnapshot ?? SystemResourceSnapshot.Empty;
            heldCpu.Snap(snapshot.CpuPercent);
            heldMemory.Snap(snapshot.MemoryPercent);
            heldGpu.Snap(snapshot.GpuPercent);
            shownCpu = heldCpu.Target;
            shownMemory = heldMemory.Target;
            shownGpu = heldGpu.Target;

            while (heldDisks.Count < DiskRowCount())
            {
                heldDisks.Add(new HeldValue(0.2, 1.0));
            }

            DiskActivityRow[] disks = snapshot.Disks;
            int count = disks == null ? 0 : Math.Min(disks.Length, MaxDiskRows);
            if (shownDisks.Length != count)
            {
                shownDisks = new double[count];
            }

            for (int i = 0; i < count; i++)
            {
                heldDisks[i].Snap(disks[i].Percent);
                shownDisks[i] = disks[i].Percent;
            }

            SyncHeight();
            Invalidate();
        }

        public void AddSample(SpeedSample sample)
        {
            currentSample = sample;

            // 面板没显示的时候没必要每秒去翻性能计数器 —— 那些东西要读注册表、
            // 枚举磁盘和 GPU 实例，白白占 CPU。流量曲线照记，资源数据等打开时再补。
            //
            // 注意这里绝不能为了"填上第一份数据"就在 UI 线程读：性能计数器的第一次
            // 初始化实测要 4.9 秒，正好卡在启动那一刻，球会僵住。预热交给后台线程，
            // 没预热完就先用空数据画，下一拍自然会补上。
            if (Visible && (resourceMonitor.IsPrimed || resourceMonitor.PrimeFailed))
            {
                resourceSnapshot = resourceMonitor.Read();
                SyncHeight();
            }
            else if (resourceSnapshot == null)
            {
                resourceSnapshot = resourceMonitor.CachedSnapshot;
            }

            SystemResourceSnapshot live = resourceSnapshot ?? SystemResourceSnapshot.Empty;

            heldDownload.Update(sample.DownloadBytesPerSecond);
            heldUpload.Update(sample.UploadBytesPerSecond);
            heldCpu.Update(live.CpuPercent);
            heldMemory.Update(live.MemoryPercent);
            heldGpu.Update(live.GpuPercent);

            DiskActivityRow[] disks = live.Disks;
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

            double gpu = SmoothValue.Toward(shownGpu, heldGpu.Target, elapsed, 0.5, 0.1);
            if (gpu != shownGpu)
            {
                shownGpu = gpu;
                moved = true;
            }

            DiskActivityRow[] disks = resourceSnapshot == null ? null : resourceSnapshot.Disks;
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

            SystemResourceSnapshot snapshot = resourceSnapshot ?? SystemResourceSnapshot.Empty;
            heldCpu.Snap(snapshot.CpuPercent);
            heldMemory.Snap(snapshot.MemoryPercent);
            heldGpu.Snap(snapshot.GpuPercent);

            shownDownload = heldDownload.Target;
            shownUpload = heldUpload.Target;
            shownCpu = heldCpu.Target;
            shownMemory = heldMemory.Target;
            shownGpu = heldGpu.Target;

            DiskActivityRow[] disks = snapshot.Disks;
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
        private void SyncHeight()
        {
            if (syncingHeight)
            {
                return;
            }

            syncingHeight = true;
            try
            {
                SyncHeightCore();
            }
            finally
            {
                syncingHeight = false;
            }
        }

        private bool syncingHeight;

        private void SyncHeightCore()
        {
            LayoutCards();
            // contentBottom 是设计稿单位，窗口高度要按面板自己的比例换回真实像素。
            // 这里必须用 uiScale 而不是全局 UiScale.Factor：两者只要有一丝不一致，
            // 窗口就会比内容高一截或矮一截（矮了就是底部被裁、字叠在一起）。
            float scale = uiScale;

            // 屏幕装不下时整体缩一档，而不是让窗口超出屏幕被 Windows 夹住 ——
            // 一旦被夹住，底部那几张卡片就永远看不到了（200% 缩放下必现）。
            int maxHeight = 0;
            try
            {
                maxHeight = Screen.FromControl(this).WorkingArea.Height - 24;
            }
            catch
            {
            }

            if (maxHeight > 0 && contentBottom > 0)
            {
                float fitScale = maxHeight / (float)contentBottom;
                if (fitScale < scale)
                {
                    scale = Math.Max(0.5f, fitScale);
                }
            }

            int width = (int)Math.Round(DesignWidth * scale);
            int height = (int)Math.Round(contentBottom * scale);
            uiScale = scale;

            if (width != Width)
            {
                Width = width;
            }

            if (height == Height)
            {
                return;
            }

            Trace("sync height " + Height + " -> " + height);
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

        // ─────────────────────── 布局 ───────────────────────

        /// <summary>按当前配置挑出要显示的字段，顺序保持原样。</summary>
        private static List<FieldDef> VisibleFields(FieldDef[] defs)
        {
            List<FieldDef> list = new List<FieldDef>(defs.Length);
            for (int i = 0; i < defs.Length; i++)
            {
                if (PanelFieldSettings.IsVisible(defs[i].Id))
                {
                    list.Add(defs[i]);
                }
            }

            return list;
        }

        private static int Rows(int fieldCount, int columns)
        {
            if (fieldCount <= 0)
            {
                return 0;
            }

            return (fieldCount + columns - 1) / columns;
        }

        /// <summary>
        /// 网络区的高度：曲线、今日、本月三块各自可以关掉，关掉下面的就往上顶。
        /// </summary>
        private int MeasureNetwork()
        {
            int y = 72;
            if (PanelFieldSettings.IsVisible("net.graph"))
            {
                y = 91 + 74 + 8;
            }

            if (PanelFieldSettings.IsVisible("net.today"))
            {
                y += 40;
            }

            if (PanelFieldSettings.IsVisible("net.month"))
            {
                y += 40;
            }

            return y + 6;
        }

        /// <summary>一张卡片的高度：标题区 + 字段行数 × 行高 + 底部留白。</summary>
        private static int CardHeight(int headerHeight, int fieldRows)
        {
            return headerHeight + fieldRows * TextRow + 8;
        }

        /// <summary>
        /// 从上往下依次摆卡片，顺便算出总高度。
        ///
        /// 绘制和算窗口高度都走这一个方法 —— 分成两份的话，改个间距就会出现
        /// 「窗口比内容矮 20px，最底下那行被切掉」这种只有用户能看见的错。
        /// </summary>
        private void LayoutCards()
        {
            int width = Math.Max(240, UWidth - SideMargin * 2);
            networkBottom = MeasureNetwork();
            int y = networkBottom;

            bool cpuModel = PanelFieldSettings.IsVisible("cpu.model");
            int cpuHeader = (cpuModel ? 58 : 40);
            cpuCard = new Rectangle(SideMargin, y, width,
                CardHeight(cpuHeader, Rows(VisibleFields(CpuFields).Count, 3)));
            y += cpuCard.Height + CardGap;

            bool memUsed = PanelFieldSettings.IsVisible("mem.used");
            int memHeader = (memUsed ? 58 : 40);
            memoryCard = new Rectangle(SideMargin, y, width,
                CardHeight(memHeader, Rows(VisibleFields(MemoryFields).Count, 3)));
            y += memoryCard.Height + CardGap;

            int disks = DiskRowCount();
            if (disks > 0)
            {
                diskCard = new Rectangle(SideMargin, y, width, 24 + disks * DiskRowHeight());
                y += diskCard.Height + CardGap;
            }
            else
            {
                diskCard = Rectangle.Empty;
            }

            bool gpuModel = PanelFieldSettings.IsVisible("gpu.model");
            int gpuHeader = (gpuModel ? 58 : 40);
            gpuCard = new Rectangle(SideMargin, y, width,
                CardHeight(gpuHeader, Rows(VisibleFields(GpuFields).Count, 2)));
            y += gpuCard.Height + CardGap;

            if (PanelFieldSettings.IsVisible("nic.card"))
            {
                int links = LinkRowCount();
                linkCard = new Rectangle(SideMargin, y, width, 28 + links * LinkRowHeight);
                y += linkCard.Height;
            }
            else
            {
                linkCard = Rectangle.Empty;
            }

            contentBottom = y + 12;
        }

        /// <summary>一块磁盘占多高：读写和容量都关掉时只留标题和进度条。</summary>
        private static int DiskRowHeight()
        {
            bool details = PanelFieldSettings.IsVisible("disk.io") ||
                           PanelFieldSettings.IsVisible("disk.capacity");
            return details ? DiskRowFull : DiskRowTight;
        }

        private int DiskRowCount()
        {
            SystemResourceSnapshot snapshot = resourceSnapshot ?? SystemResourceSnapshot.Empty;
            DiskActivityRow[] disks = snapshot.Disks;
            return disks == null ? 0 : Math.Min(disks.Length, MaxDiskRows);
        }

        private int LinkRowCount()
        {
            SystemResourceSnapshot snapshot = resourceSnapshot ?? SystemResourceSnapshot.Empty;
            NetworkLinkRow[] links = snapshot.Links;
            int count = links == null ? 0 : Math.Min(links.Length, MaxLinkRows);
            return count > 0 ? count : 1;
        }

        /// <summary>
        /// 把详情面板原样渲染成 PNG，只给 <c>--render-details 路径</c> 自检用。
        /// 假数据按日常待机那种"20~50 KB/s 抖 + 偶尔一下 190 KB/s"来造，
        /// 这样能看出真实场景下曲线是什么样，而不是一排理想化的正弦波。
        /// </summary>
        public static void SavePreview(string path)
        {
            TrafficStore store = new TrafficStore();
            SystemInfoProvider.WaitForStaticInfo(3000);
            using (SpeedDetailsForm form = new SpeedDetailsForm(store))
            {
                // 逼出窗口句柄，否则 DrawToBitmap 画出来是一张白图。
                if (form.Handle == IntPtr.Zero)
                {
                    return;
                }

                // 磁盘活动率是"两次读数之间的差值"，必须真的隔一秒取两次，
                // 连着循环喂 60 条假数据只会算出垃圾值（间隔≈0）。
                // GPU 也一个道理，顺手把它的采样倒计时拨到 1，两次都真采一遍。
                form.resourceSnapshot = form.resourceMonitor.Read();
                System.Threading.Thread.Sleep(1100);
                // GPU 利用率是"两次读数之间的差值"，头一次只能建基线，得再采一次才有值
                form.resourceMonitor.ForceGpuSample();
                System.Threading.Thread.Sleep(700);
                form.resourceMonitor.ForceGpuSample();
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
                form.SyncHeight();

                using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        }

        // ─────────────────────── 右键菜单 ───────────────────────

        private void EnsureFieldMenu()
        {
            if (fieldMenu != null)
            {
                return;
            }

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
            BuildFieldMenuItems(menu.Items, delegate
            {
                SyncHeight();
                Invalidate();
            });
            menu.Opened += delegate { AlignGroupDirections(menu); };

            fieldMenu = menu;
        }

        /// <summary>
        /// 「面板显示项」整棵子菜单，托盘和面板右键共用同一份定义。
        /// 传进来的回调在每次勾选变化后执行，调用方拿它去重排窗口。
        /// </summary>
        public static ToolStripMenuItem BuildFieldMenuItem(Action onChanged)
        {
            ToolStripMenuItem root = new ToolStripMenuItem("\u9762\u677f\u663e\u793a\u9879");
            KeepOpenOnItemClick(root.DropDown);
            BuildFieldMenuItems(root.DropDownItems, onChanged);
            // 位置要等展开了才知道，所以挂在 Opened 上，而不是建菜单的时候。
            root.DropDown.Opened += delegate { AlignGroupDirections(root.DropDown); };
            return root;
        }

        /// <summary>
        /// 让「网络 / CPU / 内存 …」这一层的子菜单统一朝「离父菜单更远」的一侧展开。
        ///
        /// Windows 只按屏幕还剩多少地方决定子菜单往哪边开，不管会不会压住父菜单：
        /// 托盘菜单贴着屏幕右边时，「面板显示项」会被挤到左边，它里面的分组却还照样往右开，
        /// 结果正好盖在「显示悬浮球」「窗口置顶」那几行上面 —— 两层菜单叠在一起，
        /// 既难看又挡着点不到。这里把整层方向钉死，一级一级往同一个方向长。
        /// </summary>
        private static void AlignGroupDirections(ToolStripDropDown root)
        {
            if (root == null || root.IsDisposed)
            {
                return;
            }

            bool openLeft;
            ToolStripDropDown owner = root.OwnerItem == null
                ? null
                : root.OwnerItem.Owner as ToolStripDropDown;

            if (owner != null)
            {
                // 自己就是别人的子菜单：父菜单在右边，我就往左长，绝不回头压住它
                openLeft = root.Left < owner.Left;
            }
            else
            {
                // 顶层菜单：右边装不下一个子菜单就往左开
                Rectangle screen = Screen.FromPoint(root.PointToScreen(Point.Empty)).WorkingArea;
                openLeft = root.Right + root.Width > screen.Right;
            }

            ToolStripDropDownDirection direction = openLeft
                ? ToolStripDropDownDirection.Left
                : ToolStripDropDownDirection.Right;

            foreach (ToolStripItem item in root.Items)
            {
                ToolStripMenuItem menuItem = item as ToolStripMenuItem;
                if (menuItem == null || !menuItem.HasDropDownItems)
                {
                    continue;
                }

                menuItem.DropDownDirection = direction;
            }
        }

        /// <summary>
        /// 让这一层菜单在点完菜单项之后不要自动收起来。
        ///
        /// ToolStrip 默认是"点一项就关"，可改显示项的时候往往要连着调好几项，
        /// 每点一次就得重新右键、重新展开子菜单、再一项一项找，非常烦。
        /// 这里只拦「点菜单项」这一种关闭原因：点空白、按 Esc、切窗口照样正常关闭。
        /// </summary>
        private static void KeepOpenOnItemClick(ToolStripDropDown dropDown)
        {
            dropDown.Closing += delegate(object sender, ToolStripDropDownClosingEventArgs e)
            {
                if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
                {
                    e.Cancel = true;
                }
            };
        }

        private static void BuildFieldMenuItems(ToolStripItemCollection items, Action onChanged)
        {
            AddMenuGroup(items, "网络", NetworkFields, onChanged);

            // CPU 十二项一列太长了，靠近屏幕底边只能往上顶，所以再分四小撮。
            ToolStripMenuItem cpu = AddSubMenu(items, "CPU", onChanged);
            AddFieldItems(cpu.DropDownItems, CpuModelField, onChanged);
            AddMenuGroup(cpu.DropDownItems, "频率", CpuFreqFields, onChanged);
            AddMenuGroup(cpu.DropDownItems, "运行", CpuRunFields, onChanged);
            AddMenuGroup(cpu.DropDownItems, "核心", CpuCoreFields, onChanged);
            AddMenuGroup(cpu.DropDownItems, "缓存", CpuCacheFields, onChanged);

            ToolStripMenuItem memory = AddSubMenu(items, "内存", onChanged);
            AddMenuGroup(memory.DropDownItems, "占用", MemUseFields, onChanged);
            AddMenuGroup(memory.DropDownItems, "系统", MemSystemFields, onChanged);
            AddMenuGroup(memory.DropDownItems, "规格", MemSpecFields, onChanged);

            AddMenuGroup(items, "磁盘", DiskFields, onChanged);
            AddMenuGroup(items, "GPU", GpuGroupFields, onChanged);
            AddMenuGroup(items, "网卡", NicFields, onChanged);

            items.Add(new ToolStripSeparator());
            ToolStripMenuItem reset = new ToolStripMenuItem("\u5168\u90e8\u663e\u793a");
            ToolStripItemCollection captured = items;
            reset.Click += delegate
            {
                PanelFieldSettings.ShowAll();
                PanelFieldSettings.SyncMenuChecks(captured);
                if (onChanged != null)
                {
                    onChanged();
                }
            };
            items.Add(reset);
        }

        /// <summary>建一个带箭头的子菜单，顺手把"点完不收起"和展开方向都挂上去。</summary>
        private static ToolStripMenuItem AddSubMenu(ToolStripItemCollection items, string label,
            Action onChanged)
        {
            ToolStripMenuItem parent = new ToolStripMenuItem(label);
            KeepOpenOnItemClick(parent.DropDown);
            // 每一层都要自己判断方向，否则深层子菜单又会拐回去压住上一层
            parent.DropDown.Opened += delegate { AlignGroupDirections(parent.DropDown); };
            items.Add(parent);
            return parent;
        }

        /// <summary>
        /// 往菜单里塞一组勾选项，外面包一个同名的子菜单。
        /// CheckOnClick 让系统自己处理打勾，点击回调里只管存配置。
        /// </summary>
        private static void AddMenuGroup(ToolStripItemCollection items, string group,
            FieldDef[] defs, Action onChanged)
        {
            ToolStripMenuItem parent = AddSubMenu(items, group, onChanged);
            AddFieldItems(parent.DropDownItems, defs, onChanged);
        }

        /// <summary>把一组勾选项平铺塞进某个菜单（不建子菜单）。</summary>
        private static void AddFieldItems(ToolStripItemCollection items, FieldDef[] defs,
            Action onChanged)
        {
            for (int i = 0; i < defs.Length; i++)
            {
                FieldDef def = defs[i];
                ToolStripMenuItem item = new ToolStripMenuItem(def.Label);
                item.CheckOnClick = true;
                item.Checked = PanelFieldSettings.IsVisible(def.Id);
                item.Tag = def.Id;
                item.CheckedChanged += delegate
                {
                    ToolStripMenuItem source = (ToolStripMenuItem)item;
                    PanelFieldSettings.SetVisible((string)source.Tag, source.Checked);
                    Trace("menu " + source.Tag + " checked=" + source.Checked);
                    if (onChanged != null)
                    {
                        onChanged();
                    }
                };

                items.Add(item);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (e.Button == MouseButtons.Right)
            {
                EnsureFieldMenu();
                // 无边框窗口默认不激活，先点一下自己再弹菜单，
                // 免得菜单出来之后第一下点击被当成"激活窗口"吃掉。
                Activate();
                fieldMenu.Show(this, e.Location);
                return;
            }

            if (e.Button == MouseButtons.Left && closeHover && CloseButtonBounds.Contains(ToLogical(e.Location)))
            {
                closeHover = false;
                Cursor = Cursors.Default;
                Hide();
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
            bool hover = CloseButtonBounds.Contains(ToLogical(e.Location));
            if (hover != closeHover)
            {
                closeHover = hover;
                Cursor = hover ? Cursors.Hand : Cursors.Default;
                Invalidate(ToPhysical(CloseButtonBounds));
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (closeHover)
            {
                closeHover = false;
                Cursor = Cursors.Default;
                Invalidate(ToPhysical(CloseButtonBounds));
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            // 画之前先按「窗口实际宽度 ÷ 设计稿宽度」校准比例。
            // 不查 DPI、不用缓存值：窗口被谁改了尺寸，这一帧就跟着改，
            // 内容和窗口永远同宽，字不会画出面板外面。
            SyncScaleFromWindow();
            // 整个画布按比例放大，下面的坐标全按 96 DPI 写，缩放到 150% 时
            // 边框、行距、字号一起放大，不会出现「字变大了格子没变大」的重叠。
            g.ScaleTransform(uiScale, uiScale);

            LayoutCards();

            // 内容高度和窗口高度对不上就当场校正：首次打开、插拔磁盘、
            // 改显示项都会走到这里，永远不会出现"底部空一块"或"底部被裁"。
            // 先比较再调用，避免每帧都去问一次屏幕工作区。
            if (Math.Abs(Height - contentBottom * uiScale) > 1.0f)
            {
                SyncHeight();
            }

            Rectangle body = new Rectangle(1, 1, UWidth - 3, UHeight - 3);
            using (GraphicsPath path = CreateRoundedRectangle(body, 18))
            using (LinearGradientBrush background = new LinearGradientBrush(body, Color.White, Color.FromArgb(235, 247, 255), LinearGradientMode.Vertical))
            using (Pen border = new Pen(Color.FromArgb(120, 71, 164, 226), 1.2f))
            {
                g.FillPath(background, path);
                g.DrawPath(border, path);
            }

            SystemResourceSnapshot snapshot = resourceSnapshot ?? SystemResourceSnapshot.Empty;

            DrawNetworkSection(g, snapshot);

            DrawCpuCard(g, snapshot);
            DrawMemoryCard(g, snapshot);
            DrawDiskCard(g, snapshot);
            DrawGpuCard(g, snapshot);
            DrawLinkCard(g, snapshot);

            DrawCloseButton(g);
        }

        // ─────────────────────── 网络区 ───────────────────────

        /// <summary>
        /// 网络区：上下行实时速度 + 可选的 60 秒曲线、今日/本月流量。
        /// 这几块都关掉时，整个区域只剩两行实时速度，面板会缩成一条。
        /// </summary>
        private void DrawNetworkSection(Graphics g, SystemResourceSnapshot snapshot)
        {
            g.DrawString("\u7cfb\u7edf\u8be6\u60c5", titleFont, darkBrush, 16, 13);

            string status = currentSample.HasAnyAdapter ? "\u8fde\u63a5\u6b63\u5e38" : "\u7f51\u7edc\u79bb\u7ebf";
            SizeF statusSize = g.MeasureString(status, smallFont);
            g.DrawString(status, smallFont, faintBrush, UWidth - 42 - statusSize.Width, 17);

            g.DrawString("\u4e0b\u8f7d  " + SpeedFormatter.FormatMegabytes(heldDownload.Target), valueFont, downBrush, 16, 43);
            g.DrawString("\u4e0a\u4f20  " + SpeedFormatter.FormatMegabytes(heldUpload.Target), valueFont, upBrush, 178, 43);

            int y = 72;

            if (PanelFieldSettings.IsVisible("net.graph"))
            {
                g.DrawString("\u6700\u8fd1 60 \u79d2", smallFont, mutedBrush, 16, y);

                // 曲线是自动缩放的，不标出峰值的话，"一根尖刺"就永远看不出实际量级。
                if (historyPeak > 0)
                {
                    string peakText = "\u5cf0\u503c " + SpeedFormatter.Format(historyPeak) + "/s";
                    SizeF peakSize = g.MeasureString(peakText, smallFont);
                    g.DrawString(peakText, smallFont, mutedBrush, UWidth - 16 - peakSize.Width, y);
                }

                Rectangle graph = new Rectangle(16, 91, UWidth - 32, 74);
                DrawGraph(g, graph);
                y = 91 + 74 + 8;
            }

            long todayDate = TrafficStore.ToDateInt(DateTime.Today);
            long[] todayTotals = trafficStore != null
                ? trafficStore.GetDayTotals(todayDate)
                : new long[] { 0, 0 };
            DateTime monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            long[] monthTotals = trafficStore != null
                ? trafficStore.GetRangeTotals(TrafficStore.ToDateInt(monthStart), todayDate)
                : new long[] { 0, 0 };

            int rightColumn = (UWidth - 32) / 2 + 16;

            if (PanelFieldSettings.IsVisible("net.today"))
            {
                g.DrawString("\u4eca\u65e5\u4e0b\u8f7d", smallFont, mutedBrush, 18, y);
                g.DrawString(SpeedFormatter.FormatTraffic(todayTotals[0]), valueFont, darkBrush, 18, y + 16);
                g.DrawString("\u4eca\u65e5\u4e0a\u4f20", smallFont, mutedBrush, rightColumn, y);
                g.DrawString(SpeedFormatter.FormatTraffic(todayTotals[1]), valueFont, darkBrush, rightColumn, y + 16);
                y += 40;
            }

            if (PanelFieldSettings.IsVisible("net.month"))
            {
                g.DrawString("\u672c\u6708\u4e0b\u8f7d", smallFont, mutedBrush, 18, y);
                g.DrawString(SpeedFormatter.FormatTraffic(monthTotals[0]), valueFont, darkBrush, 18, y + 16);
                g.DrawString("\u672c\u6708\u4e0a\u4f20", smallFont, mutedBrush, rightColumn, y);
                g.DrawString(SpeedFormatter.FormatTraffic(monthTotals[1]), valueFont, darkBrush, rightColumn, y + 16);
            }
        }

        // ─────────────────────── 卡片通用绘制 ───────────────────────

        /// <summary>卡片底：浅蓝一块，跟顶部网络区分开，一眼能看出"这是另一类信息"。</summary>
        private void DrawCard(Graphics g, Rectangle card)
        {
            using (Brush fill = new SolidBrush(Color.FromArgb(237, 246, 253)))
            using (Pen border = new Pen(Color.FromArgb(166, 203, 230), 1.0f))
            {
                g.FillRectangle(fill, card);
                g.DrawRectangle(border, card);
            }
        }

        /// <summary>卡片标题：左边一道彩色小竖条 + 名字，右边一个百分比或容量的大数字。</summary>
        private void DrawCardHeader(Graphics g, Rectangle card, string title, string value, Color accent)
        {
            using (Brush bar = new SolidBrush(accent))
            {
                g.FillRectangle(bar, card.Left + 10, card.Top + 9, 3, 12);
            }

            g.DrawString(title, cardTitleFont, darkBrush, card.Left + 18, card.Top + 6);

            if (!string.IsNullOrEmpty(value))
            {
                SizeF size = g.MeasureString(value, valueFont);
                g.DrawString(value, valueFont, darkBrush, card.Right - 12 - size.Width, card.Top + 5);
            }
        }

        /// <summary>副标题行：CPU 型号、内存主值、GPU 型号。没有就跳过，进度条会往上顶。</summary>
        private void DrawCardSubTitle(Graphics g, Rectangle card, string text, bool strong)
        {
            float size = strong ? valueFont.Size : smallFont.Size;
            Font font = strong ? valueFont : smallFont;
            Brush brush = strong ? darkBrush : mutedBrush;
            g.DrawString(FitText(g, text, font, card.Width - 24), font, brush,
                card.Left + 12, card.Top + 24);
        }

        private void DrawBar(Graphics g, int x, int y, int width, double percent, Color color)
        {
            using (Brush track = new SolidBrush(Color.FromArgb(206, 222, 237)))
            using (Brush bar = new SolidBrush(color))
            {
                g.FillRectangle(track, x, y, width, 6);
                int fill = (int)Math.Round(width * Math.Min(100.0, Math.Max(0.0, percent)) / 100.0);
                if (fill > 0)
                {
                    g.FillRectangle(bar, x, y, fill, 6);
                }
            }
        }

        /// <summary>一格「标签 + 值」，标签灰色、值黑色，跟任务管理器下半截那块一个排法。</summary>
        private void DrawField(Graphics g, string label, string value, int x, int y)
        {
            g.DrawString(label, smallFont, mutedBrush, x, y);
            float offset = g.MeasureString(label, smallFont).Width + 4;
            g.DrawString(value, strongFont, darkBrush, x + offset, y);
        }

        /// <summary>
        /// 按「每列一个字段」把一堆字段画成网格。关掉某一项之后，
        /// 后面的项会依次往前顶，不会在中间留空白格。
        /// </summary>
        private void DrawFieldGrid(Graphics g, Rectangle card, int top, int columns,
            List<FieldDef> fields, string[] values)
        {
            int columnWidth = (card.Width - 24) / columns;
            for (int i = 0; i < fields.Count && i < values.Length; i++)
            {
                int column = i % columns;
                int row = i / columns;
                DrawField(g, fields[i].Label, values[i],
                    card.Left + 12 + column * columnWidth, top + row * TextRow);
            }
        }

        /// <summary>型号名太长就截断加省略号，绝不让它顶到右边的百分比上。</summary>
        private string FitText(Graphics g, string text, Font font, int maxWidth)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "--";
            }

            if (g.MeasureString(text, font).Width <= maxWidth)
            {
                return text;
            }

            string result = text;
            while (result.Length > 2 && g.MeasureString(result + "\u2026", font).Width > maxWidth)
            {
                result = result.Substring(0, result.Length - 1);
            }

            return result + "\u2026";
        }

        // ─────────────────────── CPU ───────────────────────

        private void DrawCpuCard(Graphics g, SystemResourceSnapshot snapshot)
        {
            DrawCard(g, cpuCard);
            DrawCardHeader(g, cpuCard, "CPU", FormatPercent(heldCpu.Target), CpuColor);

            bool hasModel = PanelFieldSettings.IsVisible("cpu.model");
            if (hasModel)
            {
                DrawCardSubTitle(g, cpuCard, SystemInfoProvider.CpuName, false);
            }

            int barY = cpuCard.Top + (hasModel ? 42 : 24);
            DrawBar(g, cpuCard.Left + 12, barY, cpuCard.Width - 24, heldCpu.Target, CpuColor);

            List<FieldDef> fields = VisibleFields(CpuFields);
            string[] values = new string[fields.Count];
            for (int i = 0; i < fields.Count; i++)
            {
                values[i] = CpuFieldValue(fields[i].Id, snapshot);
            }

            DrawFieldGrid(g, cpuCard, barY + 16, 3, fields, values);
        }

        private string CpuFieldValue(string id, SystemResourceSnapshot snapshot)
        {
            switch (id)
            {
                case "cpu.speed": return FormatGhz(snapshot.CpuMhz);
                case "cpu.base": return FormatGhz(snapshot.CpuBaseMhz);
                case "cpu.uptime": return FormatUptime(snapshot.UptimeSeconds);
                case "cpu.process": return FormatCount(snapshot.ProcessCount);
                case "cpu.thread": return FormatCount(snapshot.ThreadCount);
                case "cpu.handle": return FormatCount(snapshot.HandleCount);
                case "cpu.cores": return FormatInt(SystemInfoProvider.CpuCores);
                case "cpu.logical": return FormatInt(SystemInfoProvider.CpuLogical);
                case "cpu.socket": return FormatInt(SystemInfoProvider.CpuPackages);
                case "cpu.l1": return FormatCache(SystemInfoProvider.L1Bytes);
                case "cpu.l2": return FormatCache(SystemInfoProvider.L2Bytes);
                case "cpu.l3": return FormatCache(SystemInfoProvider.L3Bytes);
                default: return "--";
            }
        }

        // ─────────────────────── 内存 ───────────────────────

        private void DrawMemoryCard(Graphics g, SystemResourceSnapshot snapshot)
        {
            DrawCard(g, memoryCard);
            DrawCardHeader(g, memoryCard, "\u5185\u5b58", FormatPercent(heldMemory.Target), MemoryColor);

            bool hasUsed = PanelFieldSettings.IsVisible("mem.used");
            if (hasUsed)
            {
                string used = FormatGb(snapshot.MemoryUsedBytes) + "/" + FormatGb(snapshot.MemoryTotalBytes) + " GB";
                DrawCardSubTitle(g, memoryCard, used, true);
            }

            int barY = memoryCard.Top + (hasUsed ? 42 : 24);
            DrawBar(g, memoryCard.Left + 12, barY, memoryCard.Width - 24, heldMemory.Target, MemoryColor);

            List<FieldDef> fields = VisibleFields(MemoryFields);
            List<FieldDef> grid = new List<FieldDef>(fields.Count);
            string[] values = new string[fields.Count];
            for (int i = 0; i < fields.Count; i++)
            {
                // 「已用/总量」已经画在副标题上了，网格里不再重复一遍
                if (string.Equals(fields[i].Id, "mem.used", StringComparison.Ordinal))
                {
                    continue;
                }

                grid.Add(fields[i]);
                values[grid.Count - 1] = MemoryFieldValue(fields[i].Id, snapshot);
            }

            DrawFieldGrid(g, memoryCard, barY + 16, 3, grid, values);
        }

        private string MemoryFieldValue(string id, SystemResourceSnapshot snapshot)
        {
            switch (id)
            {
                case "mem.available": return FormatGb(snapshot.MemoryAvailableBytes) + " GB";
                case "mem.cached": return FormatGb(snapshot.MemoryCachedBytes) + " GB";
                case "mem.speed": return FormatMemSpeed();
                case "mem.commit": return FormatGb(snapshot.CommitUsedBytes) + "/" + FormatGb(snapshot.CommitLimitBytes) + " GB";
                case "mem.paged": return FormatMb(snapshot.PagedPoolBytes);
                case "mem.nonpaged": return FormatMb(snapshot.NonPagedPoolBytes);
                case "mem.slot": return SystemInfoProvider.MemorySlotsUsed + "/" + SystemInfoProvider.MemorySlotsTotal;
                case "mem.type": return SystemInfoProvider.MemoryType;
                case "mem.module": return FormatModuleSize();
                default: return "--";
            }
        }

        // ─────────────────────── 磁盘 ───────────────────────

        private void DrawDiskCard(Graphics g, SystemResourceSnapshot snapshot)
        {
            int rows = DiskRowCount();
            if (rows <= 0)
            {
                return;
            }

            DrawCard(g, diskCard);
            using (Brush bar = new SolidBrush(DiskColor))
            {
                g.FillRectangle(bar, diskCard.Left + 10, diskCard.Top + 9, 3, 12);
            }

            g.DrawString("\u78c1\u76d8", cardTitleFont, darkBrush, diskCard.Left + 18, diskCard.Top + 6);

            bool showIo = PanelFieldSettings.IsVisible("disk.io");
            bool showCapacity = PanelFieldSettings.IsVisible("disk.capacity");
            bool showDetails = showIo || showCapacity;

            DiskActivityRow[] disks = snapshot.Disks;
            int rowHeight = DiskRowHeight();
            int y = diskCard.Top + 24;
            for (int i = 0; i < rows; i++)
            {
                DiskActivityRow disk = disks[i];
                double percent = i < shownDisks.Length ? shownDisks[i] : disk.Percent;

                g.DrawString(FitText(g, disk.Label, strongFont, diskCard.Width - 80),
                    strongFont, darkBrush, diskCard.Left + 12, y);

                string value = FormatPercent(percent);
                SizeF size = g.MeasureString(value, strongFont);
                g.DrawString(value, strongFont, darkBrush, diskCard.Right - 12 - size.Width, y);

                DrawBar(g, diskCard.Left + 12, y + 15, diskCard.Width - 24, percent, DiskColor);

                if (showDetails)
                {
                    int detailsY = y + 25;
                    int x = diskCard.Left + 12;
                    int columnWidth = (diskCard.Width - 24) / 3;

                    if (showIo)
                    {
                        DrawField(g, "\u8bfb\u53d6", SpeedFormatter.Format(disk.ReadBytesPerSecond) + "/s", x, detailsY);
                        DrawField(g, "\u5199\u5165", SpeedFormatter.Format(disk.WriteBytesPerSecond) + "/s",
                            x + columnWidth, detailsY);
                        x += columnWidth * 2;
                    }

                    if (showCapacity)
                    {
                        DrawField(g, "\u5bb9\u91cf", FormatCapacity(disk.CapacityBytes, disk.FreeBytes), x, detailsY);
                    }
                }

                y += rowHeight;
            }
        }

        // ─────────────────────── GPU ───────────────────────

        private void DrawGpuCard(Graphics g, SystemResourceSnapshot snapshot)
        {
            DrawCard(g, gpuCard);
            DrawCardHeader(g, gpuCard, "GPU", FormatPercent(heldGpu.Target), GpuColor);

            bool hasModel = PanelFieldSettings.IsVisible("gpu.model");
            if (hasModel)
            {
                DrawCardSubTitle(g, gpuCard, SystemInfoProvider.GpuName, false);
            }

            int barY = gpuCard.Top + (hasModel ? 42 : 24);
            DrawBar(g, gpuCard.Left + 12, barY, gpuCard.Width - 24, heldGpu.Target, GpuColor);

            List<FieldDef> fields = VisibleFields(GpuFields);
            string[] values = new string[fields.Count];
            for (int i = 0; i < fields.Count; i++)
            {
                values[i] = GpuFieldValue(fields[i].Id, snapshot);
            }

            DrawFieldGrid(g, gpuCard, barY + 16, 2, fields, values);
        }

        private string GpuFieldValue(string id, SystemResourceSnapshot snapshot)
        {
            switch (id)
            {
                case "gpu.dedicated":
                    return FormatGb(snapshot.GpuDedicatedUsed) + "/" +
                           FormatGb(SystemInfoProvider.GpuDedicatedBytes) + " GB";
                case "gpu.shared":
                    return FormatGb(snapshot.GpuSharedUsed) + "/" +
                           FormatGb(snapshot.MemoryTotalBytes / 2) + " GB";
                case "gpu.driver": return SystemInfoProvider.GpuDriverVersion;
                case "gpu.date": return SystemInfoProvider.GpuDriverDate;
                default: return "--";
            }
        }

        // ─────────────────────── 网卡 ───────────────────────

        private void DrawLinkCard(Graphics g, SystemResourceSnapshot snapshot)
        {
            if (linkCard.IsEmpty)
            {
                return;
            }

            DrawCard(g, linkCard);

            using (Brush bar = new SolidBrush(LinkColor))
            {
                g.FillRectangle(bar, linkCard.Left + 10, linkCard.Top + 9, 3, 12);
            }

            g.DrawString("\u7f51\u7edc\u9002\u914d\u5668", cardTitleFont, darkBrush, linkCard.Left + 18, linkCard.Top + 6);

            NetworkLinkRow[] links = snapshot.Links;
            int rows = LinkRowCount();
            int y = linkCard.Top + 24;
            for (int i = 0; i < rows; i++)
            {
                string name = links != null && i < links.Length ? links[i].Name : "\u6ca1\u6709\u6d3b\u52a8\u7f51\u5361";
                string link = links != null && i < links.Length ? links[i].Link : "--";

                g.DrawString(FitText(g, name, smallFont, linkCard.Width - 110),
                    smallFont, mutedBrush, linkCard.Left + 12, y);

                SizeF size = g.MeasureString(link, strongFont);
                g.DrawString(link, strongFont, darkBrush, linkCard.Right - 12 - size.Width, y);

                y += LinkRowHeight;
            }
        }

        // ─────────────────────── 格式化 ───────────────────────

        private static string FormatPercent(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                value = 0;
            }

            return Math.Max(0.0, Math.Min(100.0, value)).ToString("0.#") + "%";
        }

        private static string FormatGhz(double mhz)
        {
            if (mhz <= 0 || double.IsNaN(mhz) || double.IsInfinity(mhz))
            {
                return "--";
            }

            return (mhz / 1000.0).ToString("0.00") + " GHz";
        }

        private static string FormatInt(int value)
        {
            return value > 0 ? value.ToString("N0") : "--";
        }

        private static string FormatCount(int value)
        {
            return value > 0 ? value.ToString("N0") : "--";
        }

        private static string FormatGb(ulong bytes)
        {
            if (bytes == 0)
            {
                return "0.0";
            }

            return (bytes / 1073741824.0).ToString("0.0");
        }

        private static string FormatMb(ulong bytes)
        {
            if (bytes == 0)
            {
                return "0 MB";
            }

            double mb = bytes / 1048576.0;
            if (mb >= 1024)
            {
                return (mb / 1024.0).ToString("0.0") + " GB";
            }

            return mb.ToString("0") + " MB";
        }

        private static string FormatCache(long bytes)
        {
            if (bytes <= 0)
            {
                return "--";
            }

            double kb = bytes / 1024.0;
            if (kb >= 1024)
            {
                return (kb / 1024.0).ToString("0.#") + " MB";
            }

            return kb.ToString("0") + " KB";
        }

        private static string FormatMemSpeed()
        {
            int speed = SystemInfoProvider.MemorySpeedMhz;
            return speed > 0 ? speed.ToString("N0") + " MT/s" : "--";
        }

        private static string FormatModuleSize()
        {
            int mb = SystemInfoProvider.MemoryModuleMb;
            if (mb <= 0)
            {
                return "--";
            }

            if (mb >= 1024)
            {
                return (mb / 1024) + " GB";
            }

            return mb + " MB";
        }

        private static string FormatUptime(ulong seconds)
        {
            if (seconds == 0)
            {
                return "--";
            }

            TimeSpan span = TimeSpan.FromSeconds(seconds);
            if (span.TotalDays >= 1)
            {
                return ((int)span.TotalDays) + "." + span.Hours.ToString("00") + ":" +
                       span.Minutes.ToString("00") + ":" + span.Seconds.ToString("00");
            }

            return span.Hours + ":" + span.Minutes.ToString("00") + ":" + span.Seconds.ToString("00");
        }

        /// <summary>容量按「剩余/总计」写，跟任务管理器磁盘页一个口径。同一块盘的多个分区要加在一起。</summary>
        private static string FormatCapacity(long capacity, long free)
        {
            if (capacity <= 0)
            {
                return "--";
            }

            double totalGb = capacity / 1073741824.0;
            double freeGb = free / 1073741824.0;
            if (totalGb >= 1024)
            {
                return (freeGb / 1024.0).ToString("0.0") + "/" + (totalGb / 1024.0).ToString("0.0") + " TB";
            }

            return freeGb.ToString("0") + "/" + totalGb.ToString("0") + " GB";
        }

        // ─────────────────────── 曲线 ───────────────────────

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
                cardTitleFont.Dispose();
                valueFont.Dispose();
                strongFont.Dispose();
                smallFont.Dispose();
                darkBrush.Dispose();
                mutedBrush.Dispose();
                faintBrush.Dispose();
                downBrush.Dispose();
                upBrush.Dispose();
                if (fieldMenu != null)
                {
                    fieldMenu.Dispose();
                }

                resourceMonitor.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// 一块物理磁盘的实时活动情况。
    /// </summary>
    internal struct DiskActivityRow
    {
        /// <summary>显示用名字，形如「磁盘 0  C: D:」。</summary>
        public readonly string Label;
        /// <summary>活动时间百分比（0~100），跟任务管理器「磁盘」那一列同口径。</summary>
        public readonly double Percent;
        /// <summary>这一秒读了多少字节。任务管理器磁盘页里那条「读取」曲线。</summary>
        public readonly double ReadBytesPerSecond;
        /// <summary>这一秒写了多少字节。</summary>
        public readonly double WriteBytesPerSecond;
        /// <summary>这块盘的总容量，按第一个盘符去问的。</summary>
        public readonly long CapacityBytes;
        /// <summary>这块盘还剩多少空间。</summary>
        public readonly long FreeBytes;

        public DiskActivityRow(string label, double percent, double readBytesPerSecond,
            double writeBytesPerSecond, long capacityBytes, long freeBytes)
        {
            Label = label;
            Percent = percent;
            ReadBytesPerSecond = readBytesPerSecond;
            WriteBytesPerSecond = writeBytesPerSecond;
            CapacityBytes = capacityBytes;
            FreeBytes = freeBytes;
        }
    }

    /// <summary>一块活动网卡：名字 + 当前链路速度。</summary>
    internal struct NetworkLinkRow
    {
        public readonly string Name;
        public readonly string Link;

        public NetworkLinkRow(string name, string link)
        {
            Name = name;
            Link = link;
        }
    }

    /// <summary>
    /// 一次采样抓到的全部系统指标。
    ///
    /// 字段基本照着任务管理器「性能」页抄的：CPU 那块有利用率/频率/进程/线程/句柄/核数，
    /// 内存那块有已用/可用/已缓存/已提交/分页池，磁盘有活动率和读写速度，GPU 有占用和显存。
    /// 用 class 而不是 struct —— 二十几个字段的 struct 每次赋值都要整块拷贝，没必要。
    /// </summary>
    internal sealed class SystemResourceSnapshot
    {
        /// <summary>还没有采样时的空快照，界面第一帧拿它兜底，省掉一路判 null。</summary>
        public static readonly SystemResourceSnapshot Empty = new SystemResourceSnapshot();

        public double CpuPercent;
        /// <summary>CPU 当前实际跑在多少 MHz（不是标称主频）。</summary>
        public double CpuMhz;
        /// <summary>标称/基准频率，任务管理器 CPU 页那个「基准速度」。</summary>
        public double CpuBaseMhz;

        public double MemoryPercent;
        public ulong MemoryUsedBytes;
        public ulong MemoryTotalBytes;
        public ulong MemoryAvailableBytes;
        /// <summary>「已缓存」，对应任务管理器内存页的 Cached。</summary>
        public ulong MemoryCachedBytes;
        public ulong CommitUsedBytes;
        public ulong CommitLimitBytes;
        public ulong PagedPoolBytes;
        public ulong NonPagedPoolBytes;

        public int ProcessCount;
        public int ThreadCount;
        public int HandleCount;
        public ulong UptimeSeconds;

        public DiskActivityRow[] Disks = new DiskActivityRow[0];

        public double GpuPercent;
        public ulong GpuDedicatedUsed;
        public ulong GpuSharedUsed;

        public NetworkLinkRow[] Links = new NetworkLinkRow[0];

        public bool NetworkOnline
        {
            get { return Links != null && Links.Length > 0; }
        }
    }

    /// <summary>
    /// 系统资源采样器：CPU、内存、磁盘、GPU、网卡，每样都按任务管理器的口径取。
    ///
    /// 快的东西（CPU 时间差、GlobalMemoryStatusEx）每秒读一次；
    /// 慢的东西（枚举磁盘、枚举 GPU 引擎实例）缓存 5 秒扫一次，
    /// 不这么分层的话，一秒一次的全量扫描会把界面动画卡成 PPT。
    /// </summary>
    internal sealed class SystemResourceMonitor : IDisposable
    {
        /// <summary>性能计数器里的物理磁盘分类。实例名形如「0 C:」「1 D: E:」。</summary>
        private const string DiskCategory = "PhysicalDisk";
        /// <summary>每个逻辑处理器都有一份的计数器，装一把抓总。</summary>
        private const string CpuCategory = "Processor Information";
        /// <summary>GPU 各引擎的占用率，实例名里带 engtype_3D / engtype_Copy 这种尾巴。</summary>
        private const string GpuEngineCategory = "GPU Engine";
        /// <summary>GPU 专用/共享显存的实际占用量。</summary>
        private const string GpuMemoryCategory = "GPU Adapter Memory";
        /// <summary>
        /// 每隔几次采样重扫一遍磁盘列表。
        /// 每次重扫要花约 45ms（读注册表里的实例名），放在 UI 线程上每秒扫一次会卡动画，
        /// 所以 5 秒扫一次 —— 插上 U 盘最多 5 秒内出现在面板里。
        /// </summary>
        private const int DiskRescanInterval = 5;
        /// <summary>GPU 引擎实例能有几十上百个，枚举一次更贵，也 5 秒一次。</summary>
        private const int GpuRescanInterval = 5;
        /// <summary>网卡列表变化很慢，5 秒扫一次足够。</summary>
        private const int LinkRescanInterval = 5;
        /// <summary>
        /// 3D 引擎实例最多认这么多个。
        /// 实测这台机器上有 68 个（每个开着 3D 上下文的进程一个），
        /// 上限给到 96 是为了留余量，别把大占用进程漏掉。
        /// </summary>
        private const int MaxGpuInstances = 96;
        /// <summary>
        /// GPU 采样间隔（毫秒）。
        ///
        /// 为什么要单独开线程：3D 引擎实例实测有 68 个，逐个取一遍要二三十毫秒，
        /// 放在 UI 线程上就是每秒卡一下。扔到后台线程之后，界面只管读缓存好的数字，
        /// 一帧都不会掉；面板关掉时线程自动停，不白烧 CPU。
        /// </summary>
        private const int GpuSampleIntervalMs = 1000;

        private sealed class DiskCounter
        {
            public PerformanceCounter Idle;
            public PerformanceCounter Read;
            public PerformanceCounter Write;
            public bool Primed;
        }

        private sealed class GpuCounter
        {
            public PerformanceCounter Utilization;
            public bool Primed;
        }

        private readonly Dictionary<string, DiskCounter> diskCounters =
            new Dictionary<string, DiskCounter>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> diskInstances = new List<string>();
        private readonly List<DiskActivityRow> diskRows = new List<DiskActivityRow>();

        private readonly Dictionary<string, GpuCounter> gpuCounters =
            new Dictionary<string, GpuCounter>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> gpuInstances = new List<string>();
        private PerformanceCounter gpuDedicatedCounter;
        private PerformanceCounter gpuSharedCounter;
        private string gpuMemoryInstance;

        private PerformanceCounter cpuFrequencyCounter;
        private PerformanceCounter cpuBaseFrequencyCounter;
        private double cpuBaseMhz;
        private bool cpuFrequencyPrimed;

        private NetworkLinkRow[] linkRows = new NetworkLinkRow[0];

        // 性能计数器第一次被创建时，.NET 要先把整套计数器注册表灌进内存。
        // 实测这台机器上「Processor Frequency」这一下要 4.9 秒（ReadCpuMhz 里）。
        // 那 4.9 秒要是落在 UI 线程上，就是启动后悬浮球僵住几秒 —— 所以第一次读
        // 永远放到后台线程去做（PrimeAsync），UI 只用读好的那份缓存。
        private readonly object readLock = new object();
        private readonly object primeLock = new object();
        private readonly object primeSync = new object();
        private Thread primeThread;
        private bool primed;
        private bool primeFailed;
        private SystemResourceSnapshot primedSnapshot;

        // GPU 在后台线程里采，UI 只读这几个缓存值
        private readonly object gpuLock = new object();
        private Thread gpuThread;
        private volatile bool gpuThreadStop;
        private double gpuPercentCached;
        private ulong gpuDedicatedCached;
        private ulong gpuSharedCached;

        private bool hasCpuBaseline;
        private long lastIdle;
        private long lastKernel;
        private long lastUser;
        private int diskRescanCountdown;
        private int gpuRescanCountdown;
        private int linkRescanCountdown;

        public void Dispose()
        {
            StopGpuThread();

            foreach (DiskCounter counter in diskCounters.Values)            {
                DisposeCounter(counter.Idle);
                DisposeCounter(counter.Read);
                DisposeCounter(counter.Write);
            }

            diskCounters.Clear();
            diskInstances.Clear();

            foreach (GpuCounter counter in gpuCounters.Values)
            {
                DisposeCounter(counter.Utilization);
            }

            gpuCounters.Clear();
            gpuInstances.Clear();
            DisposeCounter(gpuDedicatedCounter);
            DisposeCounter(gpuSharedCounter);
            DisposeCounter(cpuFrequencyCounter);
            DisposeCounter(cpuBaseFrequencyCounter);
        }

        private static void DisposeCounter(PerformanceCounter counter)
        {
            if (counter == null)
            {
                return;
            }

            try
            {
                counter.Dispose();
            }
            catch
            {
            }
        }

        /// <summary>面板打开时把后台采 GPU 的线程拉起来，关掉时停掉，别在后台白烧 CPU。</summary>
        public void SetActive(bool active)
        {
            if (active)
            {
                StartGpuThread();
            }
            else
            {
                StopGpuThread();
            }
        }

        private void StartGpuThread()
        {
            lock (gpuLock)
            {
                if (gpuThread != null && gpuThread.IsAlive)
                {
                    return;
                }

                gpuThreadStop = false;
                gpuThread = new Thread(GpuLoop);
                gpuThread.IsBackground = true;
                gpuThread.Name = "NetSpeedBall.GpuSampler";
                gpuThread.Start();
            }
        }

        private void StopGpuThread()
        {
            Thread thread;
            lock (gpuLock)
            {
                thread = gpuThread;
                gpuThread = null;
                gpuThreadStop = true;
            }

            if (thread == null)
            {
                return;
            }

            try
            {
                thread.Join(1500);
            }
            catch
            {
            }
        }

        private void GpuLoop()
        {
            while (!gpuThreadStop)
            {
                lock (gpuLock)
                {
                    try
                    {
                        SampleGpuCore();
                    }
                    catch
                    {
                    }
                }

                // 分小段睡，停线程时不用等满一秒
                for (int i = 0; i < 10 && !gpuThreadStop; i++)
                {
                    Thread.Sleep(GpuSampleIntervalMs / 10);
                }
            }
        }

        /// <summary>截图自检用：主线程立刻采一次，不等后台那一拍。</summary>
        public void ForceGpuSample()
        {
            lock (gpuLock)
            {
                try
                {
                    SampleGpuCore();
                }
                catch
                {
                }
            }
        }

        /// <summary>后台把第一次（最贵的那次）读数跑完没有。没跑完之前 UI 别碰 Read()。</summary>
        public bool IsPrimed
        {
            get { lock (primeLock) { return primed; } }
        }

        /// <summary>
        /// 预热已经彻底失败。这种机器上就别再指望后台那份缓存了：
        /// 退回"打开面板时同步读"，慢，但至少数字是有的。
        /// </summary>
        public bool PrimeFailed
        {
            get { lock (primeLock) { return primeFailed; } }
        }

        /// <summary>后台预热读出来的那一份，UI 线程直接拿，不用等。</summary>
        public SystemResourceSnapshot CachedSnapshot
        {
            get { lock (primeLock) { return primedSnapshot; } }
        }

        /// <summary>
        /// 起一条后台线程把第一次读数跑掉。只跑一次，重复调用无害。
        /// 优先级压到 BelowNormal：这活是纯等待（等 Perflib 把注册表吐出来），
        /// 不该跟开机那一刻的绘制抢 CPU。
        /// </summary>
        public void PrimeAsync()
        {
            lock (primeLock)
            {
                if (primeThread != null)
                {
                    return;
                }

                primeThread = new Thread(PrimeWorker);
                primeThread.IsBackground = true;
                primeThread.Name = "nsb-resource-prime";
                primeThread.Priority = ThreadPriority.BelowNormal;
                primeFailed = false;
                primeThread.Start();
            }
        }

        private void PrimeWorker()
        {
            try
            {
                Read();
            }
            catch
            {
                // 预热失败就当没预热过，面板打开时再同步读一次
                lock (primeLock)
                {
                    primeThread = null;
                    primeFailed = true;
                }

                lock (primeSync)
                {
                    Monitor.PulseAll(primeSync);
                }
                return;
            }

            lock (primeSync)
            {
                Monitor.PulseAll(primeSync);
            }
        }

        /// <summary>
        /// 等预热完成。只给"面板已经打开、但预热还没跑完"这条罕见路径用，
        /// 最多等 timeoutMs，等不到就返回 null（面板先画空数据，下一拍自己补上）。
        /// </summary>
        public SystemResourceSnapshot WaitForPrime(int timeoutMs)
        {
            lock (primeSync)
            {
                if (!IsPrimed)
                {
                    Monitor.Wait(primeSync, timeoutMs);
                }
            }

            return CachedSnapshot;
        }

        public SystemResourceSnapshot Read()
        {
            // 预热线程和 UI 线程可能同时进来，PerformanceCounter 不是线程安全的，
            // 这里串起来；后台那次跑完之后 UI 再读只要几毫秒，不会被挡太久。
            lock (readLock)
            {
                SystemResourceSnapshot snapshot = ReadCore();
                lock (primeLock)
                {
                    primedSnapshot = snapshot;
                    primed = true;
                    primeFailed = false;
                }

                return snapshot;
            }
        }

        private SystemResourceSnapshot ReadCore()
        {
            SystemResourceSnapshot snapshot = new SystemResourceSnapshot();

            snapshot.CpuPercent = ReadCpuPercent();
            snapshot.CpuMhz = ReadCpuMhz();
            snapshot.CpuBaseMhz = cpuBaseMhz;

            // GetPerformanceInfo 一个调用就把任务管理器下半截的数字全给了：
            // 进程/线程/句柄、已提交、系统缓存、分页与非分页缓冲池。
            // 注意它返回的单位是「页」，要乘 PageSize 才是字节。
            NativeMethods.PerformanceInformation perf = new NativeMethods.PerformanceInformation();
            perf.Size = (uint)Marshal.SizeOf(typeof(NativeMethods.PerformanceInformation));
            bool perfOk = false;
            try
            {
                perfOk = NativeMethods.GetPerformanceInfo(ref perf, perf.Size);
            }
            catch
            {
                perfOk = false;
            }

            ulong pageSize = 4096;
            if (perfOk)
            {
                long rawPage = perf.PageSize.ToInt64();
                if (rawPage > 0)
                {
                    pageSize = (ulong)rawPage;
                }
            }

            ulong totalMemory = 0;
            ulong availableMemory = 0;
            snapshot.MemoryPercent = ReadMemoryPercent(ref totalMemory, ref availableMemory);
            snapshot.MemoryTotalBytes = totalMemory;
            snapshot.MemoryAvailableBytes = availableMemory;
            snapshot.MemoryUsedBytes = totalMemory >= availableMemory ? totalMemory - availableMemory : 0;

            if (perfOk)
            {
                snapshot.MemoryCachedBytes = ReadCachedBytes(pageSize, totalMemory,
                    ToBytes(perf.SystemCache, pageSize));
                snapshot.CommitUsedBytes = ToBytes(perf.CommitTotal, pageSize);
                snapshot.CommitLimitBytes = ToBytes(perf.CommitLimit, pageSize);
                snapshot.PagedPoolBytes = ToBytes(perf.KernelPaged, pageSize);
                snapshot.NonPagedPoolBytes = ToBytes(perf.KernelNonpaged, pageSize);
                snapshot.ProcessCount = (int)perf.ProcessCount;
                snapshot.ThreadCount = (int)perf.ThreadCount;
                snapshot.HandleCount = (int)perf.HandleCount;
            }

            snapshot.UptimeSeconds = NativeMethods.GetTickCount64() / 1000;
            snapshot.Disks = ReadDisks();
            ReadGpu(snapshot);
            snapshot.Links = ReadLinks();
            return snapshot;
        }

        private static ulong ToBytes(IntPtr pages, ulong pageSize)
        {
            long value = pages.ToInt64();
            if (value <= 0)
            {
                return 0;
            }

            return (ulong)value * pageSize;
        }

        /// <summary>
        /// 算「已缓存」：待机页（Standby，按 8 个优先级 + 8 个备用优先级分组）+ 已修改页。
        ///
        /// 这块结构是内核直接吐的定长数组，一共 22 个指针宽度，顺序固定：
        /// 0 零页 / 1 空闲页 / 2 已修改 / 3 已修改未写 / 4 坏页 / 5-12 待机页优先级 /
        /// 13-20 备用优先级 / 21 页面文件里的已修改页。
        /// 算出来的值还要过一道校验：超过物理内存总量就说明结构没对上，退回旧算法。
        /// </summary>
        private static ulong ReadCachedBytes(ulong pageSize, ulong totalMemory, ulong fallback)
        {
            try
            {
                int stride = IntPtr.Size;
                int size = stride * 22;
                byte[] buffer = new byte[size];
                int returned = 0;
                if (NativeMethods.NtQuerySystemInformation(
                        NativeMethods.SystemMemoryListInformation, buffer, size, ref returned) != 0)
                {
                    return fallback;
                }

                long pages = ReadPointer(buffer, 2 * stride) + ReadPointer(buffer, 3 * stride);
                for (int i = 5; i <= 20; i++)
                {
                    pages += ReadPointer(buffer, i * stride);
                }

                if (pages <= 0)
                {
                    return fallback;
                }

                ulong bytes = (ulong)pages * pageSize;
                if (totalMemory > 0 && bytes > totalMemory)
                {
                    return fallback;
                }

                return bytes;
            }
            catch
            {
                return fallback;
            }
        }

        private static long ReadPointer(byte[] buffer, int offset)
        {
            if (offset < 0 || offset + IntPtr.Size > buffer.Length)
            {
                return 0;
            }

            return IntPtr.Size == 8
                ? BitConverter.ToInt64(buffer, offset)
                : BitConverter.ToInt32(buffer, offset);
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

        /// <summary>
        /// CPU 当前跑在多少 MHz。
        ///
        /// 任务管理器那个「速度 2.04 GHz」不是标称主频，是实时频率：
        /// 「Processor Frequency」拿到的是基准频率（这台机器就是 2300），
        /// 「% Processor Performance」是相对基准的百分比，两者一乘才是此刻的真实频率。
        /// 注意基准频率不能从注册表 ~MHz 拿 —— 那玩意儿在不少机器上给的是别的数，
        /// 一乘就把实时频率算飞了。
        /// </summary>
        private double ReadCpuMhz()
        {
            ReadCpuBaseFrequency();

            if (cpuBaseMhz <= 0)
            {
                return 0;
            }

            if (cpuFrequencyCounter == null)
            {
                try
                {
                    cpuFrequencyCounter = new PerformanceCounter(
                        CpuCategory, "% Processor Performance", "_Total", true);
                }
                catch
                {
                    cpuFrequencyCounter = null;
                    return cpuBaseMhz;
                }
            }

            try
            {
                float performance = cpuFrequencyCounter.NextValue();
                if (!cpuFrequencyPrimed)
                {
                    cpuFrequencyPrimed = true;
                    return cpuBaseMhz;
                }

                if (performance <= 0 || float.IsNaN(performance) || float.IsInfinity(performance))
                {
                    return cpuBaseMhz;
                }

                return cpuBaseMhz * performance / 100.0;
            }
            catch
            {
                return cpuBaseMhz;
            }
        }

        /// <summary>基准频率读一次就记住，没必要每秒问一遍。</summary>
        private void ReadCpuBaseFrequency()
        {
            // WMI 那边读好了就以它为准：任务管理器的「基准速度」用的就是这个值。
            // 后台线程可能还没读完，所以每拍都探一下，读到了就覆盖掉临时值。
            double official = SystemInfoProvider.CpuBaseMhz;
            if (official > 0)
            {
                cpuBaseMhz = official;
                return;
            }

            if (cpuBaseMhz > 0)
            {
                return;
            }

            try
            {
                if (cpuBaseFrequencyCounter == null)
                {
                    cpuBaseFrequencyCounter = new PerformanceCounter(
                        CpuCategory, "Processor Frequency", "_Total", true);
                }

                float value = cpuBaseFrequencyCounter.NextValue();
                if (value > 0 && !float.IsNaN(value) && !float.IsInfinity(value))
                {
                    cpuBaseMhz = value;
                }
            }
            catch
            {
            }

            // 计数器也拿不到时退回注册表那个近似值，总比显示 "--" 强
            if (cpuBaseMhz <= 0)
            {
                cpuBaseMhz = SystemInfoProvider.CpuBaseMhz;
            }
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
        /// 读出每块物理磁盘的「活动时间」和读写速度，口径就是任务管理器那一列：
        /// 活动时间 = 100 − 空闲时间，不是磁盘用了多少空间。
        ///
        /// 磁盘列表每 5 秒重扫一次，所以插上 U 盘（或者拔掉）这里会当场多一行、少一行，
        /// 不用重启程序，也不需要知道盘符 —— 计数器实例名里自带盘符。
        /// </summary>
        private DiskActivityRow[] ReadDisks()
        {
            if (--diskRescanCountdown <= 0)
            {
                diskRescanCountdown = DiskRescanInterval;
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
                double read = 0;
                double write = 0;
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

                    if (counter.Read != null)
                    {
                        read = Math.Max(0.0, counter.Read.NextValue());
                    }

                    if (counter.Write != null)
                    {
                        write = Math.Max(0.0, counter.Write.NextValue());
                    }
                }
                catch
                {
                    // 磁盘正好被拔掉时计数器会失效；这一拍按 0 显示，下一轮重扫会清掉它。
                    percent = 0;
                    read = 0;
                    write = 0;
                }

                long capacity = 0;
                long free = 0;
                ReadDiskCapacity(instance, ref capacity, ref free);
                diskRows.Add(new DiskActivityRow(FriendlyDiskName(instance), percent, read, write,
                    capacity, free));
            }

            return diskRows.ToArray();
        }

        /// <summary>
        /// 从「1 D: E:」里把所有盘符的容量加起来。
        ///
        /// 一块物理盘可能分成 C: D: 两个分区，任务管理器磁盘页报的是整块盘，
        /// 所以这里必须逐个盘符累加 —— 只取第一个会把 D: 那 243GB 直接吞掉。
        /// </summary>
        private static void ReadDiskCapacity(string instance, ref long capacity, ref long free)
        {
            int space = instance.IndexOf(' ');
            if (space <= 0 || space + 1 >= instance.Length)
            {
                return;
            }

            string volumes = instance.Substring(space + 1);
            long totalBytes = 0;
            long freeBytes = 0;

            for (int i = 0; i + 1 < volumes.Length; i++)
            {
                if (!char.IsLetter(volumes[i]) || volumes[i + 1] != ':')
                {
                    continue;
                }

                try
                {
                    string root = volumes.Substring(i, 2) + "\\";
                    DriveInfo drive = new DriveInfo(root);
                    if (drive.IsReady)
                    {
                        totalBytes += drive.TotalSize;
                        freeBytes += drive.AvailableFreeSpace;
                    }
                }
                catch
                {
                    // 光驱没盘、U 盘刚拔、BitLocker 没解锁，都会走到这里，静默跳过
                }

                i++;
            }

            capacity = totalBytes;
            free = freeBytes;
        }

        /// <summary>
        /// GPU：3D 引擎利用率 + 专用/共享显存占用。
        ///
        /// 任务管理器那一栏是「按引擎分开算」的：3D、Copy、Video Decode 各有各的百分比，
        /// 这里只取 3D —— 平时关心的就是它，也是游戏帧数的直接指标。
        /// 同一个引擎在多个进程里都有实例，加起来才是整机 3D 占用。
        /// </summary>
        private void ReadGpu(SystemResourceSnapshot snapshot)
        {
            // 这里只读后台线程算好的缓存值，一次锁的开销可以忽略
            lock (gpuLock)
            {
                snapshot.GpuPercent = gpuPercentCached;
                snapshot.GpuDedicatedUsed = gpuDedicatedCached;
                snapshot.GpuSharedUsed = gpuSharedCached;
            }
        }

        /// <summary>必须在 gpuLock 里调：PerformanceCounter 不是线程安全的。</summary>
        private void SampleGpuCore()
        {
            if (--gpuRescanCountdown <= 0)
            {
                gpuRescanCountdown = GpuRescanInterval;
                RescanGpu();
            }

            double total = 0;
            for (int i = 0; i < gpuInstances.Count; i++)
            {
                GpuCounter counter;
                if (!gpuCounters.TryGetValue(gpuInstances[i], out counter))
                {
                    continue;
                }

                try
                {
                    float value = counter.Utilization.NextValue();
                    if (counter.Primed && value > 0 && !float.IsNaN(value) && !float.IsInfinity(value))
                    {
                        total += value;
                    }

                    counter.Primed = true;
                }
                catch
                {
                    // 进程退出后实例就废了，下一轮重扫会清掉
                }
            }

            gpuPercentCached = Math.Max(0.0, Math.Min(100.0, total));

            if (gpuDedicatedCounter != null)
            {
                try
                {
                    gpuDedicatedCached = (ulong)Math.Max(0.0, gpuDedicatedCounter.NextValue());
                }
                catch
                {
                }
            }

            if (gpuSharedCounter != null)
            {
                try
                {
                    gpuSharedCached = (ulong)Math.Max(0.0, gpuSharedCounter.NextValue());
                }
                catch
                {
                }
            }
        }

        private void RescanGpu()
        {
            List<string> found = new List<string>();
            try
            {
                PerformanceCounterCategory category = new PerformanceCounterCategory(GpuEngineCategory);
                string[] names = category.GetInstanceNames();
                for (int i = 0; i < names.Length && found.Count < MaxGpuInstances; i++)
                {
                    string name = names[i];
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    // 只认 3D 引擎，其余（Copy / VideoDecode / VideoProcessing）先不掺和
                    if (name.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    found.Add(name);
                }
            }
            catch
            {
                // 老系统 / 虚拟机上根本没有 GPU Engine 分类，保持原样
                return;
            }

            List<string> stale = new List<string>();
            foreach (string key in gpuCounters.Keys)
            {
                if (!found.Contains(key))
                {
                    stale.Add(key);
                }
            }

            for (int i = 0; i < stale.Count; i++)
            {
                DisposeCounter(gpuCounters[stale[i]].Utilization);
                gpuCounters.Remove(stale[i]);
            }

            for (int i = 0; i < found.Count; i++)
            {
                if (gpuCounters.ContainsKey(found[i]))
                {
                    continue;
                }

                try
                {
                    PerformanceCounter counter = new PerformanceCounter(
                        GpuEngineCategory, "Utilization Percentage", found[i], true);
                    gpuCounters[found[i]] = new GpuCounter { Utilization = counter };
                }
                catch
                {
                }
            }

            gpuInstances.Clear();
            for (int i = 0; i < found.Count; i++)
            {
                if (gpuCounters.ContainsKey(found[i]))
                {
                    gpuInstances.Add(found[i]);
                }
            }

            RescanGpuMemory();
        }

        /// <summary>
        /// 显存占用按「物理适配器」分组，实例名形如 luid_0x00000000_0x0000C4A8_phys_0。
        /// 多显卡机器上取专用显存最大的那块 —— 那才是用户心里的「GPU」。
        /// </summary>
        private void RescanGpuMemory()
        {
            string best = null;
            long bestTotal = -1;
            string[] names = null;
            try
            {
                names = new PerformanceCounterCategory(GpuMemoryCategory).GetInstanceNames();
            }
            catch
            {
                return;
            }

            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                if (string.IsNullOrEmpty(name) || name.IndexOf("phys_", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                try
                {
                    using (PerformanceCounter probe = new PerformanceCounter(
                        GpuMemoryCategory, "Dedicated Usage", name, true))
                    {
                        long value = (long)probe.NextValue();
                        if (value > bestTotal)
                        {
                            bestTotal = value;
                            best = name;
                        }
                    }
                }
                catch
                {
                }
            }

            if (best == null)
            {
                return;
            }

            // 实例没变就不重建，省掉一次分配
            if (string.Equals(best, gpuMemoryInstance, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            DisposeCounter(gpuDedicatedCounter);
            DisposeCounter(gpuSharedCounter);
            gpuDedicatedCounter = null;
            gpuSharedCounter = null;
            gpuMemoryInstance = best;

            try
            {
                gpuDedicatedCounter = new PerformanceCounter(
                    GpuMemoryCategory, "Dedicated Usage", best, true);
                gpuSharedCounter = new PerformanceCounter(
                    GpuMemoryCategory, "Shared Usage", best, true);
            }
            catch
            {
                DisposeCounter(gpuDedicatedCounter);
                DisposeCounter(gpuSharedCounter);
                gpuDedicatedCounter = null;
                gpuSharedCounter = null;
            }
        }

        /// <summary>
        /// 活动网卡列表。虚拟网卡（VMware、Hyper-V、VPN）平时也在，但链路速度是 0，
        /// 直接筛掉 —— 不然真网卡会被一堆「0 bps」淹掉。
        /// </summary>
        private NetworkLinkRow[] ReadLinks()
        {
            if (--linkRescanCountdown > 0 && linkRows.Length > 0)
            {
                return linkRows;
            }

            linkRescanCountdown = LinkRescanInterval;
            List<NetworkLinkRow> rows = new List<NetworkLinkRow>();
            try
            {
                NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < interfaces.Length; i++)
                {
                    NetworkInterface item = interfaces[i];
                    if (item.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }

                    if (item.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        item.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    {
                        continue;
                    }

                    long speed = 0;
                    try
                    {
                        speed = item.Speed;
                    }
                    catch
                    {
                        speed = 0;
                    }

                    if (speed <= 0)
                    {
                        continue;
                    }

                    rows.Add(new NetworkLinkRow(item.Name, FormatLink(speed)));
                }
            }
            catch
            {
                return linkRows;
            }

            linkRows = rows.ToArray();
            return linkRows;
        }

        private static string FormatLink(long bitsPerSecond)
        {
            if (bitsPerSecond <= 0)
            {
                return "--";
            }

            double mbps = bitsPerSecond / 1000000.0;
            if (mbps >= 1000)
            {
                return (mbps / 1000.0).ToString("0.#") + " Gbps";
            }

            return mbps.ToString("0") + " Mbps";
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
                DisposeCounter(diskCounters[stale[i]].Idle);
                DisposeCounter(diskCounters[stale[i]].Read);
                DisposeCounter(diskCounters[stale[i]].Write);
                diskCounters.Remove(stale[i]);
            }

            for (int i = 0; i < found.Count; i++)
            {
                if (diskCounters.ContainsKey(found[i]))
                {
                    continue;
                }

                DiskCounter counter = new DiskCounter();
                try
                {
                    counter.Idle = new PerformanceCounter(
                        DiskCategory, "% Idle Time", found[i], true);
                }
                catch
                {
                    // 这块盘的活动时间读不到就跳过它，别把整个面板拖垮
                    continue;
                }

                try
                {
                    counter.Read = new PerformanceCounter(
                        DiskCategory, "Disk Read Bytes/sec", found[i], true);
                    counter.Write = new PerformanceCounter(
                        DiskCategory, "Disk Write Bytes/sec", found[i], true);
                }
                catch
                {
                    // 读写速度拿不到就算了，活动率还在
                    DisposeCounter(counter.Read);
                    DisposeCounter(counter.Write);
                    counter.Read = null;
                    counter.Write = null;
                }

                diskCounters[found[i]] = counter;
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

    /// <summary>
    /// 开机之后就不会变的那些硬件信息：CPU 型号/核数/缓存、内存条频率和插槽、GPU 名字和驱动。
    ///
    /// 这些东西要么要读注册表、要么要解析固件表，全都比「取个百分比」贵得多，
    /// 所以只读一次，读完缓存住。第一次读放在后台线程，界面先显示「--」，
    /// 读好了自己重绘一次，用户完全感觉不到卡顿。
    /// </summary>
    internal static class SystemInfoProvider
    {
        private const string ProcessorKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
        private const string DisplayClassKey =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        private static readonly object Sync = new object();
        private static readonly ManualResetEventSlim Ready = new ManualResetEventSlim(false);
        private static bool started;

        private static string cpuName = "--";
        private static double cpuBaseMhz;
        private static int cpuCores;
        private static int cpuPackages;
        private static int cpuLogical;
        private static long l1Bytes;
        private static long l2Bytes;
        private static long l3Bytes;
        private static bool virtualizationEnabled;
        private static int memorySpeedMhz;
        private static int memorySlotsUsed;
        private static int memorySlotsTotal = 1;
        private static int memoryModuleMb;
        private static string memoryType = "--";
        private static string gpuName = "--";
        private static string gpuDriverVersion = "--";
        private static string gpuDriverDate = "--";
        private static ulong gpuDedicatedBytes;

        public static string CpuName { get { return cpuName; } }
        public static double CpuBaseMhz { get { return cpuBaseMhz; } }
        public static int CpuCores { get { return cpuCores; } }
        public static int CpuPackages { get { return cpuPackages; } }
        public static int CpuLogical { get { return cpuLogical; } }
        public static long L1Bytes { get { return l1Bytes; } }
        public static long L2Bytes { get { return l2Bytes; } }
        public static long L3Bytes { get { return l3Bytes; } }
        public static bool VirtualizationEnabled { get { return virtualizationEnabled; } }
        public static int MemorySpeedMhz { get { return memorySpeedMhz; } }
        public static int MemorySlotsUsed { get { return memorySlotsUsed; } }
        public static int MemorySlotsTotal { get { return memorySlotsTotal; } }
        public static int MemoryModuleMb { get { return memoryModuleMb; } }
        public static string MemoryType { get { return memoryType; } }
        public static string GpuName { get { return gpuName; } }
        public static string GpuDriverVersion { get { return gpuDriverVersion; } }
        public static string GpuDriverDate { get { return gpuDriverDate; } }
        public static ulong GpuDedicatedBytes { get { return gpuDedicatedBytes; } }

        /// <summary>
        /// 触发一次后台加载。传进来的回调在工作线程里跑，调用方负责切回 UI 线程。
        /// 重复调用只生效第一次。
        /// </summary>
        public static void EnsureLoading(Action onLoaded)
        {
            lock (Sync)
            {
                if (started)
                {
                    return;
                }

                started = true;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Load();
                }
                catch
                {
                    // 任何一项读不到都只是显示「--」，绝不能让面板挂掉
                }
                finally
                {
                    Ready.Set();
                }

                if (onLoaded != null)
                {
                    try
                    {
                        onLoaded();
                    }
                    catch
                    {
                    }
                }
            });
        }

        /// <summary>截图自检用：等静态信息读完，最多等 timeout 毫秒。</summary>
        public static void WaitForStaticInfo(int timeout)
        {
            EnsureLoading(null);
            try
            {
                Ready.Wait(timeout);
            }
            catch
            {
            }
        }

        private static void Load()
        {
            ReadCpuFromRegistry();
            ReadCpuBaseFrequency();
            ReadCpuTopology();
            ReadMemoryModules();
            ReadGpuFromRegistry();
            virtualizationEnabled = SafeIsVirtualizationEnabled();
        }

        /// <summary>
        /// 基准频率以 WMI 的 Win32_Processor.MaxClockSpeed 为准 —— 任务管理器显示的
        /// 「基准速度」就是它。注册表的 ~MHz 和性能计数器给的都可能是别的数
        /// （实测有些机器会报成睿频后的值），所以只拿来兜底。
        /// </summary>
        private static void ReadCpuBaseFrequency()
        {
            try
            {
                using (ManagementObjectSearcher searcher =
                    new ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor"))
                {
                    using (ManagementObjectCollection results = searcher.Get())
                    {
                        foreach (ManagementObject item in results)
                        {
                            try
                            {
                                object value = item["MaxClockSpeed"];
                                if (value != null)
                                {
                                    double mhz = Convert.ToDouble(value);
                                    if (mhz > 0)
                                    {
                                        cpuBaseMhz = mhz;
                                        return;
                                    }
                                }
                            }
                            finally
                            {
                                item.Dispose();
                            }
                        }
                    }
                }
            }
            catch
            {
                // WMI 服务被关掉、权限不足都会走到这里，保留注册表那个近似值
            }
        }

        private static void ReadCpuFromRegistry()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(ProcessorKey))
                {
                    if (key == null)
                    {
                        return;
                    }

                    object name = key.GetValue("ProcessorNameString");
                    if (name != null)
                    {
                        cpuName = name.ToString().Trim();
                    }

                    object mhz = key.GetValue("~MHz");
                    if (mhz != null)
                    {
                        double value = Convert.ToDouble(mhz);
                        if (value > 0)
                        {
                            cpuBaseMhz = value;
                        }
                    }
                }
            }
            catch
            {
            }

            if (cpuBaseMhz <= 0)
            {
                cpuBaseMhz = 0;
            }

            cpuLogical = Environment.ProcessorCount;
        }

        /// <summary>
        /// 数物理核、量三级缓存。
        ///
        /// 系统给的是一个可变长的「条目数组」，每条前面 4 字节是关系类型、再 4 字节是本条多长，
        /// 所以只能按字节走，不能声明成固定结构体 —— 那是 x86 时代的老 API 才有的待遇。
        /// 缓存要按「层级 + 大小 + 掩码 + 类型」去重：同一个缓存会被多个逻辑处理器各报一遍，
        /// 直接累加会把 24MB 的 L3 算成 384MB。
        /// </summary>
        private static void ReadCpuTopology()
        {
            try
            {
                byte[] buffer = ReadProcessorTopology(0);
                if (buffer != null)
                {
                    int offset = 0;
                    int cores = 0;
                    while (offset + 8 <= buffer.Length)
                    {
                        int relationship = BitConverter.ToInt32(buffer, offset);
                        int entrySize = BitConverter.ToInt32(buffer, offset + 4);
                        if (entrySize <= 0 || offset + entrySize > buffer.Length)
                        {
                            break;
                        }

                        if (relationship == 0)
                        {
                            cores++;
                        }

                        offset += entrySize;
                    }

                    if (cores > 0)
                    {
                        cpuCores = cores;
                    }
                }
            }
            catch
            {
            }

            // 一个 RelationProcessorPackage 条目 = 一个物理 CPU 插座（双路服务器就是 2）
            try
            {
                byte[] buffer = ReadProcessorTopology(3);
                if (buffer != null)
                {
                    int offset = 0;
                    int packages = 0;
                    while (offset + 8 <= buffer.Length)
                    {
                        int entrySize = BitConverter.ToInt32(buffer, offset + 4);
                        if (entrySize <= 0 || offset + entrySize > buffer.Length)
                        {
                            break;
                        }

                        packages++;
                        offset += entrySize;
                    }

                    if (packages > 0)
                    {
                        cpuPackages = packages;
                    }
                }
            }
            catch
            {
            }

            try
            {
                byte[] buffer = ReadProcessorTopology(2);
                if (buffer == null)
                {
                    return;
                }

                Dictionary<string, int> seen = new Dictionary<string, int>();
                long l1 = 0;
                long l2 = 0;
                long l3 = 0;
                int offset = 0;
                while (offset + 8 <= buffer.Length)
                {
                    int relationship = BitConverter.ToInt32(buffer, offset);
                    int entrySize = BitConverter.ToInt32(buffer, offset + 4);
                    if (entrySize <= 0 || offset + entrySize > buffer.Length)
                    {
                        break;
                    }

                    // CACHE_RELATIONSHIP：Level 在 +8，CacheSize 在 +12，Type 在 +16，
                    // 后面 20 字节保留，GroupMask 在 +40（8 字节掩码 + 2 字节组号）
                    if (relationship == 2 && entrySize >= 48)
                    {
                        int level = buffer[offset + 8];
                        int cacheSize = BitConverter.ToInt32(buffer, offset + 12);
                        int type = BitConverter.ToInt32(buffer, offset + 16);
                        long mask = BitConverter.ToInt64(buffer, offset + 40);
                        string key = level + ":" + cacheSize + ":" + type + ":" + mask;
                        if (cacheSize > 0 && !seen.ContainsKey(key))
                        {
                            seen[key] = cacheSize;
                            if (level == 1)
                            {
                                l1 += cacheSize;
                            }
                            else if (level == 2)
                            {
                                l2 += cacheSize;
                            }
                            else if (level == 3)
                            {
                                l3 += cacheSize;
                            }
                        }
                    }

                    offset += entrySize;
                }

                l1Bytes = l1;
                l2Bytes = l2;
                l3Bytes = l3;
            }
            catch
            {
            }
        }

        private static byte[] ReadProcessorTopology(int relationshipType)
        {
            uint needed = 0;
            try
            {
                NativeMethods.GetLogicalProcessorInformationEx(relationshipType, null, ref needed);
                if (needed == 0)
                {
                    return null;
                }

                byte[] buffer = new byte[needed];
                if (!NativeMethods.GetLogicalProcessorInformationEx(relationshipType, buffer, ref needed))
                {
                    return null;
                }

                return buffer;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 内存条的频率和插槽数：只能从固件里的 SMBIOS 表读。
        /// 结构是「定长头部 + 双零结尾的字符串区」，要一条一条往后走。
        /// </summary>
        private static void ReadMemoryModules()
        {
            try
            {
                uint size = NativeMethods.GetSystemFirmwareTable(NativeMethods.RsMbProvider, 0, null, 0);
                if (size < 16 || size > 4 * 1024 * 1024)
                {
                    return;
                }

                byte[] buffer = new byte[size];
                uint written = NativeMethods.GetSystemFirmwareTable(NativeMethods.RsMbProvider, 0, buffer, size);
                if (written < 16)
                {
                    return;
                }

                // 头 8 字节是版本信息，第 4 个字节往后的 DWORD 是表长度
                int tableLength = BitConverter.ToInt32(buffer, 4);
                int end = Math.Min(buffer.Length, 8 + tableLength);
                int position = 8;
                int slots = 0;
                int used = 0;
                int bestSpeed = 0;

                while (position + 4 <= end)
                {
                    byte type = buffer[position];
                    byte length = buffer[position + 1];
                    if (length < 4)
                    {
                        break;
                    }

                    if (type == 17 && length >= 0x16)
                    {
                        // Size 为 0 表示这个槽是空的；0x7FFF 表示要用扩展字段
                        int sizeField = BitConverter.ToUInt16(buffer, position + 0x0C);
                        int speed = BitConverter.ToUInt16(buffer, position + 0x15);
                        int configured = length >= 0x22
                            ? BitConverter.ToUInt16(buffer, position + 0x20)
                            : 0;
                        int typeCode = length >= 0x13 ? buffer[position + 0x12] : 0;

                        slots++;
                        if (sizeField != 0)
                        {
                            used++;
                            int actual = configured > 0 ? configured : speed;
                            if (actual > bestSpeed)
                            {
                                bestSpeed = actual;
                            }

                            // 单条容量：0x7FFF 表示超出 16 位，要读扩展字段（单位 MB）
                            int moduleMb = sizeField;
                            if (sizeField == 0x7FFF && length >= 0x20)
                            {
                                moduleMb = (int)Math.Min(int.MaxValue,
                                    BitConverter.ToUInt32(buffer, position + 0x1C));
                            }

                            if (moduleMb > 0)
                            {
                                memoryModuleMb = moduleMb;
                            }

                            string typeName = MemoryTypeName(typeCode);
                            if (typeName != null)
                            {
                                memoryType = typeName;
                            }
                        }
                    }

                    if (type == 127)
                    {
                        break;
                    }

                    // 跳到下一条：先走完定长区，再跨过以「00 00」结尾的字符串区
                    int next = position + length;
                    if (next + 1 >= end)
                    {
                        break;
                    }

                    if (buffer[next] == 0 && buffer[next + 1] == 0)
                    {
                        position = next + 2;
                    }
                    else
                    {
                        while (next + 1 < end && !(buffer[next] == 0 && buffer[next + 1] == 0))
                        {
                            next++;
                        }

                        position = next + 2;
                    }
                }

                if (slots > 0)
                {
                    memorySlotsTotal = slots;
                    memorySlotsUsed = used;
                }

                memorySpeedMhz = bestSpeed;
            }
            catch
            {
            }
        }

        /// <summary>
        /// 显卡名字、驱动版本、专用显存：注册表里显卡类下面挂着每个适配器一个子键（0000、0001…）。
        /// 多显卡（核显 + 独显）时挑专用显存最大的 —— 跟用户嘴上说的「我的显卡」对得上。
        /// </summary>
        private static void ReadGpuFromRegistry()
        {
            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(DisplayClassKey))
                {
                    if (root == null)
                    {
                        return;
                    }

                    string[] subKeys = root.GetSubKeyNames();
                    ulong bestMemory = 0;
                    string bestName = null;
                    string bestVersion = null;
                    string bestDate = null;

                    for (int i = 0; i < subKeys.Length; i++)
                    {
                        string sub = subKeys[i];
                        if (sub.Length != 4)
                        {
                            continue;
                        }

                        using (RegistryKey key = root.OpenSubKey(sub))
                        {
                            if (key == null)
                            {
                                continue;
                            }

                            object desc = key.GetValue("DriverDesc");
                            if (desc == null)
                            {
                                continue;
                            }

                            ulong memory = 0;
                            object size = key.GetValue("HardwareInformation.qwMemorySize");
                            if (size != null)
                            {
                                try
                                {
                                    memory = Convert.ToUInt64(size);
                                }
                                catch
                                {
                                    memory = 0;
                                }
                            }

                            if (bestName == null || memory > bestMemory)
                            {
                                bestMemory = memory;
                                bestName = desc.ToString();

                                object version = key.GetValue("DriverVersion");
                                bestVersion = version != null ? FormatDriverVersion(version.ToString()) : "--";

                                object date = key.GetValue("DriverDate");
                                bestDate = date != null ? FormatDriverDate(date.ToString()) : "--";
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(bestName))
                    {
                        gpuName = bestName;
                        gpuDriverVersion = bestVersion;
                        gpuDriverDate = bestDate;
                        gpuDedicatedBytes = bestMemory;
                    }
                }
            }
            catch
            {
            }
        }

        /// <summary>注册表里的驱动版本号长得像「32.0.15.6102」，原样显示就行。</summary>
        private static string FormatDriverVersion(string raw)
        {
            return string.IsNullOrEmpty(raw) ? "--" : raw.Trim();
        }

        /// <summary>驱动日期是「6-11-2026」这种 月-日-年，转成「2026/6/11」。</summary>
        private static string FormatDriverDate(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return "--";
            }

            DateTime parsed;
            if (DateTime.TryParse(raw, out parsed))
            {
                return parsed.Year + "/" + parsed.Month + "/" + parsed.Day;
            }

            return raw.Trim();
        }

        /// <summary>SMBIOS 里的内存类型代码翻成人话，表里没认出来的就返回 null 不显示。</summary>
        private static string MemoryTypeName(int code)
        {
            switch (code)
            {
                case 0x12: return "DDR";
                case 0x13: return "DDR2";
                case 0x18: return "DDR3";
                case 0x1A: return "DDR4";
                case 0x1B: return "LPDDR";
                case 0x1C: return "LPDDR2";
                case 0x1D: return "LPDDR3";
                case 0x1E: return "LPDDR4";
                case 0x20: return "HBM";
                case 0x21: return "HBM2";
                case 0x22: return "DDR5";
                case 0x23: return "LPDDR5";
                case 0x24: return "HBM3";
                case 0x0F: return "SDRAM";
                default: return null;
            }
        }

        private static bool SafeIsVirtualizationEnabled()        {
            try
            {
                return NativeMethods.IsProcessorFeaturePresent(
                    NativeMethods.PfVirtualizationFirmwareEnabled);
            }
            catch
            {
                return false;
            }
        }
    }

    internal sealed class TrafficWindow : Form
    {
        private const int RankRows = 12;

        // 版面常量。窗口是 FixedSingle，尺寸固定，所以这些值只在一处算。
        private const int PadX = 20;
        private const int RowHeight = 23;
        private const int CardHeight = 64;
        private const int ChartHeight = 226;

        // 一整套配色集中在这里。原来同一个蓝在四个方法里各写一遍，
        // 想调一次得翻半个文件，调完还容易漏掉一处。
        private static readonly Color Ink = Color.FromArgb(32, 41, 55);
        private static readonly Color InkSoft = Color.FromArgb(71, 85, 105);
        private static readonly Color Muted = Color.FromArgb(122, 134, 152);
        private static readonly Color Faint = Color.FromArgb(167, 177, 191);
        private static readonly Color Line = Color.FromArgb(232, 237, 244);
        private static readonly Color Surface = Color.FromArgb(247, 249, 252);
        private static readonly Color CardBg = Color.White;
        private static readonly Color DownColor = Color.FromArgb(45, 105, 245);
        private static readonly Color UpColor = Color.FromArgb(14, 165, 90);
        private static readonly Color Track = Color.FromArgb(237, 241, 247);

        private readonly TrafficStore store;
        private Font sectionFont;
        private Font valueFont;
        private Font smallFont;
        private Font tinyFont;
        private readonly ToolTip toolTip = new ToolTip();
        private readonly FormsTimer refreshTimer;
        private readonly MonthCalendar calendar;

        /// <summary>设计稿的客户区尺寸。真实尺寸 = 它 × DPI 比例。</summary>
        private const int LW = 720;
        private const int LH = 760;

        /// <summary>当前 DPI 比例。坐标全按 96 DPI 写，画之前整体乘它。</summary>
        private float uiScale;

        /// <summary>用户设定缩放对应的比例（屏幕装不下时 uiScale 会比它小）。</summary>
        private float targetScale;

        // 顶部那一排全是自绘的：原生 Button 是灰底 3D 边，跟下面的卡片放在一起
        // 像两个年代的软件拼起来的。命中区在构造时算好，点击靠坐标判断。
        private readonly Rectangle[] presetRects = new Rectangle[5];
        private readonly string[] presetTags = { "today", "yesterday", "month", "lastmonth", "year" };
        private readonly string[] presetTexts = { "今天", "昨天", "本月", "上月", "今年" };
        private readonly Rectangle[] chipRects = new Rectangle[2];

        private int activePreset = 2;
        private bool editingFrom;
        private int hoverPreset = -1;
        private int hoverChip = -1;
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
            // 自绘界面，尺寸和字体都由自己按 DPI 算，别让 WinForms 自动缩放（它只会拉宽）
            AutoScaleMode = AutoScaleMode.None;
            uiScale = UiScale.Factor;
            targetScale = uiScale;
            BuildFonts();
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.Manual;
            // 卡片是白的，底色就得浅一档，否则整片糊在一起分不出层次
            BackColor = Surface;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            // 标题栏用球自己的图标。不设的话是个默认的空窗口图标，
            // 在一堆系统窗口里认不出来是这个程序的。
            try
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception)
            {
                // 取不到就算了，不值得为图标让窗口开不出来
            }

            // 尺寸统一走 ApplyScale：它会顺带处理"屏幕装不下就整体缩小"，
            // 免得 200% 缩放下窗口比屏幕还高、底部应用排行永远看不到。
            ApplyScale(targetScale);

            // 日期选择用一个常驻的 MonthCalendar，点胶囊时挪过去弹出来。
            // 用 DateTimePicker 的话外观改不动（灰底 + 系统下拉箭头），
            // 而且它的下拉框没法用代码打开。
            calendar = new MonthCalendar();
            calendar.MaxSelectionCount = 1;
            calendar.Visible = false;
            calendar.DateSelected += OnCalendarPicked;
            calendar.Leave += OnCalendarLeave;
            Controls.Add(calendar);

            DateTime today = DateTime.Today;
            rangeFrom = TrafficStore.ToDateInt(new DateTime(today.Year, today.Month, 1));
            rangeTo = TrafficStore.ToDateInt(today);
            RefreshData();

            refreshTimer = new FormsTimer();
            refreshTimer.Interval = 2000;
            refreshTimer.Tick += OnRefreshTick;
            refreshTimer.Start();
        }

        private void BuildFonts()
        {
            if (sectionFont != null)
            {
                sectionFont.Dispose();
            }
            if (valueFont != null)
            {
                valueFont.Dispose();
            }
            if (smallFont != null)
            {
                smallFont.Dispose();
            }
            if (tinyFont != null)
            {
                tinyFont.Dispose();
            }

            sectionFont = UiScale.Font(9.2f, FontStyle.Bold, "Microsoft YaHei UI");
            valueFont = UiScale.Font(14.5f, FontStyle.Bold, "Microsoft YaHei UI");
            smallFont = UiScale.Font(8.6f, FontStyle.Regular, "Microsoft YaHei UI");
            tinyFont = UiScale.Font(7.8f, FontStyle.Regular, "Microsoft YaHei UI");
        }

        /// <summary>鼠标给的是真实像素，判断命中前换算回设计稿坐标。</summary>
        private Point ToLogical(Point point)
        {
            if (uiScale <= 0)
            {
                return point;
            }

            return new Point((int)Math.Round(point.X / uiScale), (int)Math.Round(point.Y / uiScale));
        }

        /// <summary>
        /// 自绘窗口自己按 DPI 重排，不让 WinForms 再缩一遍。
        /// 尤其这里还有原生 MonthCalendar 子控件，两套缩放叠加会把它弹到窗口外面去。
        /// </summary>
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
        }

        /// <summary>用户在设置里改了缩放：立刻按新 DPI 重排，不用等下一秒的轮询。</summary>
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == NativeMethods.WM_DPICHANGED)
            {
                UiScale.Adopt((int)(m.WParam.ToInt64() & 0xFFFF));
                ApplyUiScale();
            }
        }

        /// <summary>用户改了缩放：字体、客户区、命中区全部重算。</summary>
        public void ApplyUiScale()
        {
            float next = UiScale.Factor;
            if (Math.Abs(next - targetScale) < 0.01f)
            {
                return;
            }

            targetScale = next;
            ApplyScale(next);
        }

        /// <summary>按比例定客户区尺寸。屏幕高度装不下时整体压一档，保证底部内容永远看得到。</summary>
        private void ApplyScale(float scale)
        {
            int maxHeight = 0;
            try
            {
                maxHeight = Screen.FromControl(this).WorkingArea.Height - 120;
            }
            catch
            {
            }

            if (maxHeight > 0)
            {
                float fit = maxHeight / (float)LH;
                if (fit < scale)
                {
                    scale = Math.Max(0.5f, fit);
                }
            }

            uiScale = scale;
            BuildFonts();
            ClientSize = new Size((int)Math.Round(LW * scale), (int)Math.Round(LH * scale));
            LayoutChrome();
            Invalidate();
        }

        /// <summary>算好顶部控件的命中区。布局只依赖固定的 ClientSize，所以只算一次。</summary>
        private void LayoutChrome()
        {
            // 五个快捷范围做成一条分段控件：整组一个浅色底，选中项白底加粗。
            const int segTop = 14;
            const int segHeight = 30;
            const int segPadding = 3;
            const int segWidth = 62;

            int x = PadX;
            for (int i = 0; i < presetRects.Length; i++)
            {
                presetRects[i] = new Rectangle(x + segPadding + i * segWidth, segTop + segPadding,
                    segWidth, segHeight - segPadding * 2);
            }

            int groupWidth = segWidth * presetRects.Length;
            presetGroupRect = new Rectangle(PadX, segTop, groupWidth + segPadding * 2, segHeight);

            // 右侧两个日期胶囊，右对齐
            const int chipHeight = 30;
            const int chipWidth = 104;
            int right = LW - PadX;
            chipRects[1] = new Rectangle(right - chipWidth, segTop, chipWidth, chipHeight);
            chipRects[0] = new Rectangle(chipRects[1].Left - chipWidth - 26, segTop, chipWidth, chipHeight);
        }

        private Rectangle presetGroupRect;

        /// <summary>
        /// 把流量统计窗口渲染成 PNG，只给 <c>--render-traffic 路径</c> 自检用。
        /// 默认渲染"上个月"，因为垃圾数据就是在上个月暴露出来的。
        /// </summary>
        public static void SavePreview(string path)
        {
            TrafficStore store = new TrafficStore();
            using (TrafficWindow window = new TrafficWindow(store))
            {
                if (window.Handle == IntPtr.Zero)
                {
                    return;
                }

                DateTime monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1);
                window.rangeFrom = TrafficStore.ToDateInt(monthStart);
                window.rangeTo = TrafficStore.ToDateInt(monthStart.AddMonths(1).AddDays(-1));
                window.RefreshData();

                using (Bitmap bitmap = new Bitmap(window.Width, window.Height))
                {
                    window.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        }

        private void ApplyPreset(string tag)
        {
            DateTime today = DateTime.Today;
            if (tag == "today")
            {
                SetRange(today, today, 0);
            }
            else if (tag == "yesterday")
            {
                DateTime day = today.AddDays(-1);
                SetRange(day, day, 1);
            }
            else if (tag == "month")
            {
                SetRange(new DateTime(today.Year, today.Month, 1), today, 2);
            }
            else if (tag == "lastmonth")
            {
                DateTime first = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
                SetRange(first, first.AddMonths(1).AddDays(-1), 3);
            }
            else if (tag == "year")
            {
                SetRange(new DateTime(today.Year, 1, 1), today, 4);
            }
        }

        /// <summary>
        /// 点日历里的一天。点的是哪一端就改哪一端，改完立刻刷新。
        /// </summary>
        private void OnCalendarPicked(object sender, DateRangeEventArgs e)
        {
            calendar.Visible = false;
            DateTime from = TrafficStore.FromDateInt(rangeFrom);
            DateTime to = TrafficStore.FromDateInt(rangeTo);

            if (editingFrom)
            {
                // 起点不能晚于终点，否则整段是倒的、一条数据都查不到。
                // 这时候把终点一起顶过去，比给个"日期不合法"的提示顺手。
                if (e.Start > to)
                {
                    to = e.Start;
                }

                SetRange(e.Start, to, -1);
            }
            else
            {
                if (e.Start < from)
                {
                    from = e.Start;
                }

                SetRange(from, e.Start, -1);
            }
        }

        private void OnCalendarLeave(object sender, EventArgs e)
        {
            calendar.Visible = false;
        }

        /// <summary>
        /// `preset` 是哪个快捷范围被选中（-1 表示用户手挑的日期，五个都不高亮）。
        /// </summary>
        private void SetRange(DateTime from, DateTime to, int preset)
        {
            rangeFrom = TrafficStore.ToDateInt(from);
            rangeTo = TrafficStore.ToDateInt(to);
            activePreset = preset;
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

            Point point = ToLogical(e.Location);
            int preset = HitPreset(point);
            int chip = chipRects[0].Contains(point) ? 0 : (chipRects[1].Contains(point) ? 1 : -1);
            Cursor = (preset >= 0 || chip >= 0) ? Cursors.Hand : Cursors.Default;

            bool chromeDirty = preset != hoverPreset || chip != hoverChip;
            hoverPreset = preset;
            hoverChip = chip;
            if (chromeDirty)
            {
                Invalidate();
            }

            int index = -1;
            if (rangeDays.Count > 0 && chartPlot.Width > 0 && chartSlot > 0 &&
                point.X >= chartPlot.Left && point.X <= chartPlot.Right &&
                point.Y >= chartPlot.Top - 24 && point.Y <= chartPlot.Bottom)
            {
                index = (point.X - chartPlot.Left) / chartSlot;
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
                DayPoint dayPoint = rangeDays[index];
                DateTime day = TrafficStore.FromDateInt(dayPoint.Date);
                toolTip.SetToolTip(this, day.ToString("M月d日 ddd") + "\r\n下载 " +
                    SpeedFormatter.Format(dayPoint.Rx) + "\r\n上传 " + SpeedFormatter.Format(dayPoint.Tx));
            }
            else
            {
                toolTip.SetToolTip(this, string.Empty);
            }

            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverPreset < 0 && hoverChip < 0)
            {
                return;
            }

            hoverPreset = -1;
            hoverChip = -1;
            Invalidate();
        }

        private int HitPreset(Point point)
        {
            for (int i = 0; i < presetRects.Length; i++)
            {
                if (presetRects[i].Contains(point))
                {
                    return i;
                }
            }

            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            if (calendar.Visible && !calendar.Bounds.Contains(e.Location))
            {
                calendar.Visible = false;
            }

            Point point = ToLogical(e.Location);
            int preset = HitPreset(point);
            if (preset >= 0)
            {
                ApplyPreset(presetTags[preset]);
                return;
            }

            if (chipRects[0].Contains(point))
            {
                OpenCalendar(true);
            }
            else if (chipRects[1].Contains(point))
            {
                OpenCalendar(false);
            }
        }

        /// <summary>
        /// 把日历挪到某个日期胶囊下面弹出来。
        /// </summary>
        private void OpenCalendar(bool from)
        {
            editingFrom = from;
            DateTime current = TrafficStore.FromDateInt(from ? rangeFrom : rangeTo);
            calendar.SetDate(current);

            Rectangle chip = chipRects[from ? 0 : 1];
            // 日历是原生控件，坐标是真实像素，而 chipRects 是设计稿坐标，这里换算一次。
            // 用面板自己的 uiScale 而不是全局 DPI，否则两者一有偏差日历就弹歪。
            int left = Math.Min((int)Math.Round(chip.Left * uiScale), ClientSize.Width - calendar.Width - 8);
            int top = (int)Math.Round((chip.Bottom + 4) * uiScale);
            if (top + calendar.Height > ClientSize.Height - 8)
            {
                top = (int)Math.Round(chip.Top * uiScale) - calendar.Height - 4;
            }

            calendar.Location = new Point(Math.Max(4, left), Math.Max(4, top));
            calendar.Visible = true;
            calendar.BringToFront();
            calendar.Focus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            // 比例以「客户区实际宽度 ÷ 设计稿宽度」为准：窗口被谁改了尺寸都不会错位。
            int clientWidth = ClientSize.Width;
            if (clientWidth > 0)
            {
                float next = clientWidth / (float)LW;
                if (next > 0.3f && next < 6.0f)
                {
                    uiScale = next;
                }
            }
            // 画布整体按比例放大，下面的坐标全按 96 DPI 的设计稿写
            g.ScaleTransform(uiScale, uiScale);

            DrawChrome(g);

            DateTime from = TrafficStore.FromDateInt(rangeFrom);
            DateTime to = TrafficStore.FromDateInt(rangeTo);
            string scopeText = rangeFrom == rangeTo
                ? from.ToString("yyyy年M月d日 dddd")
                : from.ToString("yyyy年M月d日") + "  至  " + to.ToString("yyyy年M月d日");
            int dayCount = Math.Max(1, (int)TrafficStore.DaysBetween(rangeFrom, rangeTo) + 1);

            using (Brush muted = new SolidBrush(Muted))
            {
                g.DrawString(scopeText + "    ·    共 " + dayCount + " 天", smallFont, muted, PadX, 56);
            }

            DrawSummaryCards(g, dayCount);
            DrawDailyChart(g);
            DrawRankList(g);

            using (Brush note = new SolidBrush(Faint))
            {
                g.DrawString("排行榜按 TCP 连接统计，不含 UDP/QUIC 与协议头开销 · 数据只存本机，年占用不足 1 MB",
                    tinyFont, note, PadX, LH - 22);
            }
        }

        /// <summary>
        /// 顶部一整条：分段控件 + 两个日期胶囊。全部自绘 —— 原生 Button 是灰底 3D 边，
        /// 跟下面的卡片摆在一起像两个年代的软件拼起来的。
        /// </summary>
        private void DrawChrome(Graphics g)
        {
            using (GraphicsPath path = CreateRoundedRectangle(presetGroupRect, 9))
            using (Brush fill = new SolidBrush(Color.FromArgb(237, 242, 249)))
            {
                g.FillPath(fill, path);
            }

            for (int i = 0; i < presetRects.Length; i++)
            {
                Rectangle rect = presetRects[i];
                bool active = i == activePreset;
                if (active)
                {
                    using (GraphicsPath path = CreateRoundedRectangle(rect, 7))
                    using (Brush fill = new SolidBrush(CardBg))
                    using (Pen border = new Pen(Color.FromArgb(221, 229, 241)))
                    {
                        g.FillPath(fill, path);
                        g.DrawPath(border, path);
                    }
                }
                else if (i == hoverPreset)
                {
                    using (GraphicsPath path = CreateRoundedRectangle(rect, 7))
                    using (Brush fill = new SolidBrush(Color.FromArgb(18, 80, 120, 200)))
                    {
                        g.FillPath(fill, path);
                    }
                }

                using (Brush text = new SolidBrush(active ? DownColor : InkSoft))
                {
                    DrawLabel(g, presetTexts[i], active ? sectionFont : smallFont, text, rect,
                        StringAlignment.Center);
                }
            }

            for (int i = 0; i < chipRects.Length; i++)
            {
                Rectangle rect = chipRects[i];
                bool hot = i == hoverChip;
                using (GraphicsPath path = CreateRoundedRectangle(rect, 9))
                using (Brush fill = new SolidBrush(hot ? Color.FromArgb(246, 249, 253) : CardBg))
                using (Pen border = new Pen(hot ? Color.FromArgb(196, 211, 232) : Line))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(border, path);
                }

                DateTime value = TrafficStore.FromDateInt(i == 0 ? rangeFrom : rangeTo);
                using (Brush text = new SolidBrush(Ink))
                {
                    DrawLabel(g, value.ToString("yyyy/MM/dd"), smallFont, text, rect, StringAlignment.Center);
                }
            }

            Rectangle gap = Rectangle.FromLTRB(chipRects[0].Right, chipRects[0].Top,
                chipRects[1].Left, chipRects[0].Bottom);
            using (Brush arrow = new SolidBrush(Faint))
            {
                DrawLabel(g, "→", smallFont, arrow, gap, StringAlignment.Center);
            }
        }

        private void DrawSummaryCards(Graphics g, int dayCount)
        {
            long down = rangeTotals[0];
            long up = rangeTotals[1];
            long total = down + up;
            long perDay = total / dayCount;

            const int gap = 12;
            int width = (LW - PadX * 2 - gap * 3) / 4;
            string[] labels = { "下载", "上传", "合计", "日均" };
            string[] values =
            {
                SpeedFormatter.Format(down),
                SpeedFormatter.Format(up),
                SpeedFormatter.Format(total),
                SpeedFormatter.Format(perDay)
            };
            Color[] accents = { DownColor, UpColor, Color.FromArgb(30, 64, 175), Faint };

            for (int i = 0; i < 4; i++)
            {
                Rectangle card = new Rectangle(PadX + i * (width + gap), 76, width, CardHeight);
                // 合计是主角，单独给一层淡蓝底，其余三张白底。
                bool hero = i == 2;
                using (GraphicsPath path = CreateRoundedRectangle(card, 12))
                using (Brush fill = new SolidBrush(hero ? Color.FromArgb(240, 245, 255) : CardBg))
                using (Pen border = new Pen(hero ? Color.FromArgb(206, 222, 250) : Line))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(border, path);
                }

                // 颜色只用来标类别（一根 3px 竖条），不用来染数字。
                // 原来四个数字是蓝绿橙深灰各一色，横着看过去像四个互不相干的指标。
                using (Brush accent = new SolidBrush(accents[i]))
                {
                    g.FillRectangle(accent, card.X + 14, card.Y + 15, 3, card.Height - 30);
                }

                using (Brush label = new SolidBrush(Muted))
                using (Brush value = new SolidBrush(Ink))
                {
                    g.DrawString(labels[i], smallFont, label, card.X + 25, card.Y + 9);
                    g.DrawString(values[i], valueFont, value, card.X + 24, card.Y + 26);
                }
            }
        }

        private void DrawDailyChart(Graphics g)
        {
            Rectangle panel = new Rectangle(PadX, 152, LW - PadX * 2, ChartHeight);
            DrawCardSurface(g, panel);

            using (Brush heading = new SolidBrush(Ink))
            {
                g.DrawString("每日流量", sectionFont, heading, panel.X + 16, panel.Y + 12);
            }

            DrawLegend(g, panel.Right - 136, panel.Y + 14);

            // 上留 56（标题一行 + 刻度值一行），下留 34 给日期轴
            Rectangle plot = new Rectangle(panel.Left + 16, panel.Top + 56,
                panel.Width - 32, panel.Height - 90);
            chartPlot = plot;
            chartSlot = rangeDays.Count > 0 ? Math.Max(1, plot.Width / rangeDays.Count) : 0;

            double maximum = 0;
            for (int i = 0; i < rangeDays.Count; i++)
            {
                maximum = Math.Max(maximum, (double)(rangeDays[i].Rx + rangeDays[i].Tx));
            }

            maximum = NiceCeil(maximum);

            // 刻度线只留顶端和中间。原来画五条，密度和柱子差不多，
            // 整块图看起来像草稿纸。
            using (Pen grid = new Pen(Color.FromArgb(228, 233, 242)))
            {
                grid.DashStyle = DashStyle.Dash;
                grid.DashPattern = new float[] { 3f, 4f };
                int half = plot.Bottom - plot.Height / 2;
                g.DrawLine(grid, plot.Left, half, plot.Right, half);
            }

            using (Pen baseline = new Pen(Color.FromArgb(214, 222, 234)))
            {
                g.DrawLine(baseline, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
            }

            using (Brush axisText = new SolidBrush(Faint))
            {
                // 两个刻度值分别靠左右两侧摆：都放右边会跟图例叠在一起。
                DrawLabel(g, SpeedFormatter.Format((long)maximum), tinyFont, axisText,
                    new Rectangle(plot.Left, plot.Top - 22, 90, 16), StringAlignment.Near);
                DrawLabel(g, SpeedFormatter.Format((long)(maximum / 2)), tinyFont, axisText,
                    new Rectangle(plot.Right - 90, plot.Top + plot.Height / 2 - 18, 90, 14), StringAlignment.Far);
            }

            if (rangeDays.Count == 0)
            {
                using (Brush empty = new SolidBrush(Faint))
                {
                    DrawLabel(g, "这段时间没有记录", smallFont, empty, plot, StringAlignment.Center);
                }

                return;
            }

            int slot = chartSlot;
            // 柱宽封顶 46px。单日时 slot 等于整幅图的宽度，不封顶就会变成
            // 一整块占掉半个窗口的蓝色板子（就是改版前那张截图里的样子）。
            int barWidth = Math.Min(46, Math.Max(3, (int)Math.Round(slot * 0.56)));

            using (Brush downBrush = new SolidBrush(DownColor))
            using (Brush upBrush = new SolidBrush(UpColor))
            using (Brush hoverBrush = new SolidBrush(Color.FromArgb(14, 45, 105, 245)))
            {
                for (int i = 0; i < rangeDays.Count; i++)
                {
                    DayPoint point = rangeDays[i];
                    int slotLeft = plot.Left + i * slot;
                    if (i == hoverDayIndex)
                    {
                        g.FillRectangle(hoverBrush, slotLeft, plot.Top, slot, plot.Height);
                    }

                    long total = point.Rx + point.Tx;
                    if (total <= 0)
                    {
                        continue;
                    }

                    int barHeight = Math.Max(2,
                        (int)Math.Round(Math.Min(1.0, total / maximum) * plot.Height));
                    Rectangle bar = new Rectangle(slotLeft + (slot - barWidth) / 2,
                        plot.Bottom - barHeight, barWidth, barHeight);

                    // 下载在下、上传在上。用一条圆角路径当模具分别填色：
                    // 柱顶永远是圆的，中间那道接缝是直的。
                    using (GraphicsPath clip = CreateRoundedRectangle(bar, 3))
                    {
                        GraphicsState state = g.Save();
                        g.SetClip(clip, CombineMode.Replace);

                        int downHeight = (int)Math.Round(
                            Math.Min(1.0, (double)point.Rx / maximum) * plot.Height);
                        if (downHeight > 0)
                        {
                            g.FillRectangle(downBrush, bar.X, bar.Bottom - downHeight, bar.Width, downHeight);
                        }

                        if (point.Tx > 0)
                        {
                            int upHeight = Math.Max(2, (int)Math.Round(
                                Math.Min(1.0, (double)point.Tx / maximum) * plot.Height));
                            g.FillRectangle(upBrush, bar.X, bar.Bottom - downHeight - upHeight,
                                bar.Width, upHeight);
                        }

                        g.Restore(state);
                    }
                }
            }

            DrawDateAxis(g, plot);
        }

        /// <summary>
        /// 小圆点 + 文字，标一下柱子两种颜色各是什么。
        /// </summary>
        private void DrawLegend(Graphics g, int x, int y)
        {
            string[] names = { "下载", "上传" };
            Color[] colors = { DownColor, UpColor };
            using (Brush text = new SolidBrush(Muted))
            {
                for (int i = 0; i < names.Length; i++)
                {
                    int left = x + i * 66;
                    using (Brush dot = new SolidBrush(colors[i]))
                    {
                        g.FillEllipse(dot, left, y + 3, 7, 7);
                    }

                    g.DrawString(names[i], tinyFont, text, left + 12, y - 1);
                }
            }
        }

        private void DrawDateAxis(Graphics g, Rectangle plot)
        {
            int count = rangeDays.Count;
            if (count == 0)
            {
                return;
            }

            using (Brush axisText = new SolidBrush(Faint))
            {
                if (count == 1)
                {
                    // 只有一天时起点一定在最左边，看着像"图从这儿开始"。居中摆。
                    DateTime only = TrafficStore.FromDateInt(rangeDays[0].Date);
                    DrawLabel(g, only.ToString("M月d日"), tinyFont, axisText,
                        new Rectangle(plot.Left, plot.Bottom + 8, plot.Width, 16), StringAlignment.Center);
                    return;
                }

                int step = count <= 16 ? 1 : (count <= 62 ? 7 : 30);
                int y = plot.Bottom + 9;
                for (int i = 0; i < count; i += step)
                {
                    if (i > 0 && plot.Left + i * chartSlot + 30 > plot.Right)
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

                    g.DrawString(label, tinyFont, axisText, plot.Left + i * chartSlot, y);
                }
            }
        }

        private void DrawRankList(Graphics g)
        {
            Rectangle card = new Rectangle(PadX, 388, LW - PadX * 2, 340);
            DrawCardSurface(g, card);

            using (Brush heading = new SolidBrush(Ink))
            {
                g.DrawString("应用排行", sectionFont, heading, card.X + 16, card.Y + 10);
            }

            using (Brush hint = new SolidBrush(Faint))
            {
                DrawLabel(g, "按总流量排序", tinyFont, hint,
                    new Rectangle(card.Right - 160, card.Y + 10, 144, 18), StringAlignment.Far);
            }

            int headerTop = card.Y + 38;
            using (Pen line = new Pen(Line))
            {
                g.DrawLine(line, card.X + 1, headerTop, card.Right - 1, headerTop);
            }

            int indexLeft = card.X + 14;
            int nameLeft = card.X + 42;
            int nameWidth = 166;
            int barLeft = card.X + 210;
            int barRight = card.Right - 190;
            int downLeft = card.Right - 176;
            int upLeft = card.Right - 92;
            int upRight = card.Right - 16;

            using (Brush headText = new SolidBrush(Faint))
            {
                DrawLabel(g, "应用", tinyFont, headText,
                    new Rectangle(nameLeft, headerTop + 1, nameWidth, 20), StringAlignment.Near);
                DrawLabel(g, "下载", tinyFont, headText,
                    new Rectangle(downLeft, headerTop + 1, upLeft - 4 - downLeft, 20), StringAlignment.Far);
                DrawLabel(g, "上传", tinyFont, headText,
                    new Rectangle(upLeft, headerTop + 1, upRight - upLeft, 20), StringAlignment.Far);
            }

            double maxTotal = 1.0;
            for (int i = 0; i < rangeRanks.Count; i++)
            {
                maxTotal = Math.Max(maxTotal, (double)rangeRanks[i].Total);
            }

            int y = card.Y + 58;
            using (Brush indexText = new SolidBrush(Faint))
            using (Brush nameText = new SolidBrush(InkSoft))
            using (Brush valueText = new SolidBrush(InkSoft))
            using (Brush downBrush = new SolidBrush(DownColor))
            using (Brush upBrush = new SolidBrush(UpColor))
            {
                for (int i = 0; i < rangeRanks.Count; i++)
                {
                    AppRankRow row = rangeRanks[i];

                    // 斑马纹用最浅的一层，只为断开相邻两行，不抢名字的注意力
                    if (i % 2 == 0)
                    {
                        Rectangle stripe = new Rectangle(card.X + 8, y, card.Width - 16, RowHeight);
                        using (GraphicsPath path = CreateRoundedRectangle(stripe, 6))
                        using (Brush fill = new SolidBrush(Color.FromArgb(249, 251, 254)))
                        {
                            g.FillPath(fill, path);
                        }
                    }

                    DrawLabel(g, (i + 1).ToString(), tinyFont, indexText,
                        new Rectangle(indexLeft, y, 22, RowHeight), StringAlignment.Far);

                    DrawLabel(g, TruncateName(row.Name, nameWidth, g, smallFont), smallFont, nameText,
                        new Rectangle(nameLeft, y, nameWidth, RowHeight), StringAlignment.Near);

                    int barY = y + (RowHeight - 6) / 2;
                    Rectangle barArea = new Rectangle(barLeft, barY, barRight - barLeft, 6);
                    using (GraphicsPath path = CreateRoundedRectangle(barArea, 3))
                    using (Brush fill = new SolidBrush(Track))
                    {
                        g.FillPath(fill, path);
                    }

                    long total = row.Total;
                    if (total > 0)
                    {
                        int filled = (int)Math.Round((barRight - barLeft) * total / maxTotal);
                        filled = Math.Min(barRight - barLeft, Math.Max(2, filled));
                        Rectangle barFill = new Rectangle(barLeft, barY, filled, 6);
                        using (GraphicsPath clip = CreateRoundedRectangle(barFill, 3))
                        {
                            GraphicsState state = g.Save();
                            g.SetClip(clip, CombineMode.Replace);
                            int downWidth = (int)Math.Round(filled * (double)row.Rx / total);
                            if (downWidth > 0)
                            {
                                g.FillRectangle(downBrush, barLeft, barY, downWidth, 6);
                            }

                            if (filled - downWidth > 0)
                            {
                                g.FillRectangle(upBrush, barLeft + downWidth, barY, filled - downWidth, 6);
                            }

                            g.Restore(state);
                        }
                    }

                    DrawLabel(g, SpeedFormatter.Format(row.Rx), smallFont, valueText,
                        new Rectangle(downLeft, y, upLeft - 4 - downLeft, RowHeight), StringAlignment.Far);
                    DrawLabel(g, SpeedFormatter.Format(row.Tx), smallFont, valueText,
                        new Rectangle(upLeft, y, upRight - upLeft, RowHeight), StringAlignment.Far);

                    y += RowHeight;
                }
            }

            if (rangeRanks.Count == 0)
            {
                using (Brush empty = new SolidBrush(Muted))
                {
                    // 空列表有两种原因，必须说清楚，否则用户只会以为功能坏了。
                    if (AppTrafficMonitor.CanCollectAppTraffic)
                    {
                        DrawLabel(g, "所选范围内暂无应用流量记录", smallFont, empty,
                            new Rectangle(card.X + 8, y + 6, card.Width - 16, 24), StringAlignment.Center);
                    }
                    else
                    {
                        DrawLabel(g, "应用流量排行需要管理员权限", smallFont, empty,
                            new Rectangle(card.X + 8, y + 2, card.Width - 16, 22), StringAlignment.Center);
                        DrawLabel(g, "右键托盘图标 → 以管理员身份重启", tinyFont, empty,
                            new Rectangle(card.X + 8, y + 24, card.Width - 16, 20), StringAlignment.Center);
                    }
                }
            }
        }

        /// <summary>
        /// 卡片底：白底 + 1px 描边 + 12px 圆角。
        /// </summary>
        private static void DrawCardSurface(Graphics g, Rectangle rect)
        {
            using (GraphicsPath path = CreateRoundedRectangle(rect, 12))
            using (Brush fill = new SolidBrush(CardBg))
            using (Pen border = new Pen(Line))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
        }

        /// <summary>
        /// 在矩形里画一行字，垂直居中由 StringFormat 负责。
        /// 原来的写法是每处 `DrawString(..., x, y + 4)` 手调偏移，换个字号就全歪。
        /// </summary>
        private static void DrawLabel(Graphics g, string text, Font font, Brush brush,
            RectangleF box, StringAlignment align)
        {
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = align;
                format.LineAlignment = StringAlignment.Center;
                format.FormatFlags = StringFormatFlags.NoWrap;
                format.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(text, font, brush, box, format);
            }
        }

        /// <summary>
        /// 把最大值向上取整到 1 / 2 / 5 × 10ⁿ。
        /// 直接用原始最大值当刻度顶，轴上会出现 "182.7 MB" 这种数字，
        /// 而柱顶永远差一点点到顶，看起来像没画完。
        /// </summary>
        private static double NiceCeil(double value)
        {
            if (value <= 0)
            {
                return 1;
            }

            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
            double normalized = value / magnitude;
            double step;
            if (normalized <= 1)
            {
                step = 1;
            }
            else if (normalized <= 2)
            {
                step = 2;
            }
            else if (normalized <= 5)
            {
                step = 5;
            }
            else
            {
                step = 10;
            }

            return step * magnitude;
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

    /// <summary>
    /// 拖动悬浮球时出现在屏幕下方的「发射台」—— 一个卡通小发射台，不是写实场景：
    /// 淡蓝的天、两朵慢慢飘的云、一座小山丘、圆圆的发射坪、矮墩墩的小塔、一面飘的小旗。
    /// 出场时整个台子从坪面往上弹出来；球拖进坪面（激活）时夹爪张开、小手臂扬起让路、
    /// 塔灯变绿、火箭底部开始排汽；松手后一大团棉花糖似的白烟沿坪面翻滚散开，再收起。
    ///
    /// 动画由自己的 33ms 时钟驱动，**只在可见时走**，隐藏时就停，不占 CPU。
    /// </summary>
    internal sealed class LaunchPadForm : Form
    {
        private bool active;

        /// <summary>设计稿尺寸；真实尺寸 = 它 × DPI 比例。</summary>
        private const int LW = 258;
        private const int LH = 148;

        // 坪面中心的 x（设计稿坐标，96 DPI）
        private const float PadCx = LW / 2.0f;

        /// <summary>点火白烟时长（秒）。</summary>
        private const double BurstDuration = 0.6;

        /// <summary>动画时钟（秒），只在可见时累加。</summary>
        private double clock;
        /// <summary>出场进度 0→1（约 0.26 秒）：台子从坪面往上弹出来。</summary>
        private double popT = 1.0;
        /// <summary>小手臂扬起、夹爪张开的进度 0→1，跟着激活状态平滑过渡。</summary>
        private float armRetract;
        /// <summary>点火白烟进度 0→1；小于 0 表示没在播。</summary>
        private double burst = -1.0;
        private DateTime lastTick;
        private readonly FormsTimer animTimer;
        private readonly Font hintFont;
        private readonly Font hintBoldFont;

        public LaunchPadForm()
        {
            Text = "LaunchPad";
            AutoScaleMode = AutoScaleMode.None;
            Size = new Size(UiScale.Px(LW), UiScale.Px(LH));
            MinimumSize = Size;
            MaximumSize = Size;
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(248, 252, 255);
            Opacity = 1.0;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            hintFont = UiScale.Font(9.0f, FontStyle.Regular, "Microsoft YaHei UI");
            hintBoldFont = UiScale.Font(10.5f, FontStyle.Bold, "Microsoft YaHei UI");
            animTimer = new FormsTimer { Interval = 33 };
            animTimer.Tick += OnAnimTick;
            UpdateWindowRegion();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            // 显示时才走时钟；隐藏立刻停。一个静止的窗口不该每秒重绘 30 次。
            if (Visible)
            {
                lastTick = DateTime.UtcNow;
                popT = 0.0;
                animTimer.Start();
            }
            else
            {
                animTimer.Stop();
            }
        }

        private void OnAnimTick(object sender, EventArgs e)
        {
            DateTime now = DateTime.UtcNow;
            double elapsed = Math.Min(0.08, Math.Max(0.0, (now - lastTick).TotalSeconds));
            lastTick = now;
            clock += elapsed;
            popT = Math.Min(1.0, popT + elapsed / 0.26);
            // 手臂和夹爪用 0.2 秒平滑地张开/合上，不瞬变
            float target = active ? 1.0f : 0.0f;
            float step = (float)(elapsed / 0.2);
            armRetract = armRetract < target
                ? Math.Min(target, armRetract + step)
                : Math.Max(target, armRetract - step);
            if (burst >= 0.0)
            {
                burst += elapsed / BurstDuration;
                if (burst >= 1.0)
                {
                    // 白烟放完，自己收起（球那边不再管）
                    burst = -1.0;
                    Hide();
                    return;
                }
            }
            Invalidate();
        }

        /// <summary>点火：留在屏幕上播一段白烟，放完自己隐藏。</summary>
        public void PlayBurst()
        {
            SetActive(false);
            burst = 0.0;
            if (!Visible)
            {
                Show();
            }
            Invalidate();
        }

        /// <summary>球又被按住了：白烟别再播，回到待命。</summary>
        public void CancelBurst()
        {
            burst = -1.0;
        }

        /// <summary>离屏渲染一帧，给 <c>--render-launchpad</c> 自检用。</summary>
        public static void SavePreview(string path, bool activeState, double clockSeconds, double burstProgress)
        {
            using (LaunchPadForm pad = new LaunchPadForm())
            using (Bitmap bitmap = new Bitmap(pad.Width, pad.Height))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                pad.active = activeState;
                pad.clock = clockSeconds;
                pad.burst = burstProgress;
                pad.popT = 1.0;
                pad.armRetract = activeState ? 1.0f : 0.0f;
                g.Clear(Color.Transparent);
                pad.DrawSurface(g);
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                animTimer.Stop();
                animTimer.Dispose();
                hintFont.Dispose();
                hintBoldFont.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        /// <summary>自绘界面自己按 DPI 重排，不让 WinForms 再缩一遍。</summary>
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
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

        /// <summary>DPI 改了：按下新比例换尺寸，形状不会变形。</summary>
        public void ApplyUiScale()
        {
            Size target = new Size(UiScale.Px(LW), UiScale.Px(LH));
            if (Size == target)
            {
                return;
            }

            MinimumSize = Size.Empty;
            MaximumSize = Size.Empty;
            Size = target;
            MinimumSize = target;
            MaximumSize = target;
            UpdateWindowRegion();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            DrawSurface(e.Graphics);
        }

        /// <summary>发射坪面（设计稿 y）。火箭的喷口就该落在这一高度上。</summary>
        private const float DeckTop = 102.0f;

        /// <summary>
        /// 球该停在哪儿：让球的喷口正好落在坪面中心。拖动时球会往这个点轻轻吸，
        /// 不然火箭会悬在发射台上方半截，像没放好。
        /// </summary>
        public Point GetDockLocation(Size ballSize)
        {
            float s = Width > 0 ? Width / (float)LW : UiScale.Factor;
            float bs = ballSize.Width > 0 ? ballSize.Width / (float)SpeedBallForm.BallSize : UiScale.Factor;
            return new Point(
                Left + (int)Math.Round(PadCx * s - SpeedBallForm.BallSize / 2.0f * bs),
                Top + (int)Math.Round(DeckTop * s - SpeedBallForm.LaunchNozzleDesignY * bs));
        }

        /// <summary>整块画面。坐标全部按 96 DPI 的设计稿写。</summary>
        private void DrawSurface(Graphics raw)
        {
            Graphics g = raw;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            // 比例直接由窗口实际宽度反推 —— 不查 DPI，内容永远和窗口严丝合缝
            float scale = Width > 0 ? Width / (float)LW : UiScale.Factor;
            g.ScaleTransform(scale, scale);

            // 出场：整个台子从坪面往上"弹"出来 —— 先小一点，再带着一点回弹长到原大
            float pop = EaseOutBack((float)Math.Min(1.0, popT));
            float popScale = 0.84f + 0.16f * pop;
            g.TranslateTransform(PadCx, DeckTop);
            g.ScaleTransform(popScale, popScale);
            g.TranslateTransform(-PadCx, -DeckTop);

            float t = (float)clock;
            DrawScene(g, t);
            if (active)
            {
                DrawVenting(g, t);
            }
            if (burst >= 0.0)
            {
                DrawIgnition(g, (float)Math.Min(1.0, burst));
            }
            if (burst < 0.0)
            {
                DrawHint(g);
            }
        }

        private static float EaseOutBack(float x)
        {
            float c1 = 1.70158f;
            float c3 = c1 + 1.0f;
            return 1.0f + c3 * (x - 1.0f) * (x - 1.0f) * (x - 1.0f) + c1 * (x - 1.0f) * (x - 1.0f);
        }

        /// <summary>卡通小发射台：淡蓝的天、一座小山丘、圆圆的发射坪、矮墩墩的小塔、一面小旗。</summary>
        private void DrawScene(Graphics g, float t)
        {
            Rectangle card = new Rectangle(3, 3, LW - 7, LH - 7);
            using (GraphicsPath shell = CreateRoundedRectangle(card, 18))
            {
                using (LinearGradientBrush sky = new LinearGradientBrush(card,
                           Color.FromArgb(255, 219, 238, 255), Color.FromArgb(255, 250, 253, 255), LinearGradientMode.Vertical))
                {
                    g.FillPath(sky, shell);
                }

                // 卡片里的东西一律不许画出卡片外
                GraphicsState clip = g.Save();
                g.SetClip(shell);
                DrawClouds(g, t);
                DrawHill(g);
                DrawFlag(g, t);
                DrawBush(g, 46.0f, 114.5f, 1.0f);
                DrawBush(g, 214.0f, 114.5f, 0.8f);
                DrawTower(g, t);
                DrawDeck(g);
                g.Restore(clip);

                using (Pen edge = new Pen(Color.FromArgb(255, 184, 208, 228), 1.0f))
                {
                    g.DrawPath(edge, shell);
                }
            }
        }

        private static void DrawClouds(Graphics g, float t)
        {
            // 两朵小云从右往左慢慢飘，飘出去了再从另一边回来
            float span = LW + 80.0f;
            DrawCloud(g, LW + 40.0f - ((t * 5.0f) % span), 44.0f, 1.0f);
            DrawCloud(g, LW + 40.0f - ((t * 3.0f + 150.0f) % span), 64.0f, 0.68f);
        }

        private static void DrawCloud(Graphics g, float x, float y, float s)
        {
            using (GraphicsPath cloud = new GraphicsPath())
            {
                cloud.FillMode = FillMode.Winding;
                cloud.AddEllipse(x - 14 * s, y - 3 * s, 16 * s, 10 * s);
                cloud.AddEllipse(x - 6 * s, y - 10 * s, 15 * s, 15 * s);
                cloud.AddEllipse(x + 3 * s, y - 6 * s, 13 * s, 12 * s);
                cloud.AddEllipse(x - 9 * s, y - 2 * s, 26 * s, 10 * s);
                using (Brush white = new SolidBrush(Color.FromArgb(225, 255, 255, 255)))
                {
                    g.FillPath(white, cloud);
                }
                // 云底下淡淡一条影子，云才不是贴纸
                using (Brush shade = new SolidBrush(Color.FromArgb(70, 170, 200, 226)))
                {
                    g.FillEllipse(shade, x - 10 * s, y + 5 * s, 20 * s, 2.5f * s);
                }
            }
        }

        private static void DrawHill(Graphics g)
        {
            using (GraphicsPath hill = new GraphicsPath())
            {
                hill.AddBezier(3, 124, 60, 103, 198, 103, 255, 124);
                hill.AddLine(255, LH, 3, LH);
                hill.CloseFigure();
                using (LinearGradientBrush grass = new LinearGradientBrush(new Rectangle(3, 103, 252, 45),
                           Color.FromArgb(255, 213, 240, 222), Color.FromArgb(255, 178, 222, 194), LinearGradientMode.Vertical))
                {
                    g.FillPath(grass, hill);
                }
            }
            // 只描山顶那条弧线，底边贴着卡片不描
            using (Pen edge = new Pen(Color.FromArgb(255, 150, 202, 170), 1.0f))
            {
                g.DrawBezier(edge, 3, 124, 60, 103, 198, 103, 255, 124);
            }
        }

        private static void DrawBush(Graphics g, float x, float y, float s)
        {
            using (GraphicsPath bush = new GraphicsPath())
            {
                bush.FillMode = FillMode.Winding;
                bush.AddEllipse(x - 9 * s, y - 6 * s, 11 * s, 9 * s);
                bush.AddEllipse(x - 2 * s, y - 9 * s, 11 * s, 12 * s);
                bush.AddEllipse(x + 3 * s, y - 5 * s, 8 * s, 8 * s);
                using (Brush leaf = new SolidBrush(Color.FromArgb(255, 150, 210, 172)))
                {
                    g.FillPath(leaf, bush);
                }
            }
        }

        /// <summary>坪边一面小旗，慢慢飘。</summary>
        private static void DrawFlag(Graphics g, float t)
        {
            float wave = (float)Math.Sin(t * 3.1) * 1.6f;
            using (Pen pole = new Pen(Color.FromArgb(255, 160, 178, 196), 1.6f))
            using (GraphicsPath flag = new GraphicsPath())
            using (Brush cloth = new SolidBrush(Color.FromArgb(255, 255, 138, 104)))
            {
                pole.StartCap = LineCap.Round;
                g.DrawLine(pole, 190, 112, 190, 84);
                flag.AddPolygon(new[]
                {
                    new PointF(191, 84), new PointF(191, 92), new PointF(205 + wave, 88 + wave * 0.4f)
                });
                g.FillPath(cloth, flag);
            }
        }

        /// <summary>矮墩墩的小塔：圆角柱子、三个小窗、顶上一盏灯、一条伸向火箭的小手臂。</summary>
        private void DrawTower(Graphics g, float t)
        {
            using (GraphicsPath body = CreateRoundedRectangle(new Rectangle(86, 48, 18, 56), 5))
            using (LinearGradientBrush wall = new LinearGradientBrush(new Rectangle(86, 48, 18, 56),
                       Color.FromArgb(255, 246, 249, 252), Color.FromArgb(255, 222, 231, 240), LinearGradientMode.Horizontal))
            using (Pen edge = new Pen(Color.FromArgb(255, 172, 194, 214), 1.0f))
            {
                g.FillPath(wall, body);
                g.DrawPath(edge, body);
            }

            // 塔顶的小帽子
            using (GraphicsPath cap = CreateRoundedRectangle(new Rectangle(83, 43, 24, 7), 3))
            using (Brush capBrush = new SolidBrush(Color.FromArgb(255, 214, 226, 238)))
            using (Pen edge = new Pen(Color.FromArgb(255, 172, 194, 214), 1.0f))
            {
                g.FillPath(capBrush, cap);
                g.DrawPath(edge, cap);
            }

            // 三个圆舷窗
            using (Brush glass = new SolidBrush(Color.FromArgb(255, 158, 202, 234)))
            using (Pen edge = new Pen(Color.FromArgb(255, 134, 178, 214), 0.8f))
            {
                for (int i = 0; i < 3; i++)
                {
                    g.FillEllipse(glass, 92.5f, 56.5f + i * 14, 5, 5);
                    g.DrawEllipse(edge, 92.5f, 56.5f + i * 14, 5, 5);
                }
            }

            // 顶灯：待命时红光慢慢呼吸，激活后变绿常亮
            if (active)
            {
                using (GraphicsPath halo = new GraphicsPath())
                {
                    halo.AddEllipse(92.0f, 35.0f, 10, 10);
                    using (PathGradientBrush glow = new PathGradientBrush(halo))
                    {
                        glow.CenterColor = Color.FromArgb(110, 90, 220, 150);
                        glow.SurroundColors = new[] { Color.FromArgb(0, 90, 220, 150) };
                        g.FillPath(glow, halo);
                    }
                }
            }
            int breathe = (int)(150 + 90 * (0.5 + 0.5 * Math.Sin(t * 2.4)));
            using (Brush beacon = new SolidBrush(active
                       ? Color.FromArgb(255, 74, 204, 126)
                       : Color.FromArgb(breathe, 255, 104, 86)))
            {
                g.FillEllipse(beacon, 94.0f, 37.0f, 6, 6);
            }

            // 小手臂：平时搭向火箭，火箭在场就扬起来让路
            float length = 21.0f - 14.0f * armRetract;
            float angle = -0.5f * armRetract;
            double rad = angle;
            float ax = 104.0f;
            float ay = 62.0f;
            float bx = ax + length * (float)Math.Cos(rad);
            float by = ay + length * (float)Math.Sin(rad);
            using (Pen arm = new Pen(Color.FromArgb(255, 150, 170, 190), 3.0f))
            using (Brush joint = new SolidBrush(Color.FromArgb(255, 120, 142, 164)))
            {
                arm.StartCap = LineCap.Round;
                arm.EndCap = LineCap.Round;
                g.DrawLine(arm, ax, ay, bx, by);
                g.FillEllipse(joint, ax - 2.5f, ay - 2.5f, 5, 5);
                g.FillEllipse(joint, bx - 2.0f, by - 2.0f, 4, 4);
            }
        }

        /// <summary>圆圆的发射坪：有厚度，坪沿三盏小灯，坪上四个夹爪（火箭在场时张开）。</summary>
        private void DrawDeck(Graphics g)
        {
            using (GraphicsPath side = new GraphicsPath())
            {
                side.AddArc(PadCx - 30, DeckTop - 8, 60, 16, 0, 180);
                side.CloseFigure();
                using (Brush sideBrush = new SolidBrush(Color.FromArgb(255, 196, 210, 224)))
                {
                    g.FillPath(sideBrush, side);
                }
            }

            using (LinearGradientBrush top = new LinearGradientBrush(new RectangleF(PadCx - 30, DeckTop - 8, 60, 16),
                       Color.FromArgb(255, 250, 252, 254), Color.FromArgb(255, 228, 236, 244), LinearGradientMode.Vertical))
            using (Pen edge = new Pen(Color.FromArgb(255, 172, 194, 214), 1.0f))
            {
                g.FillEllipse(top, PadCx - 30, DeckTop - 8, 60, 16);
                g.DrawEllipse(edge, PadCx - 30, DeckTop - 8, 60, 16);
            }

            // 坪面中间浅浅的一圈，像铺了一层防热层
            using (Brush inner = new SolidBrush(Color.FromArgb(255, 236, 241, 247)))
            {
                g.FillEllipse(inner, PadCx - 21, DeckTop - 5, 42, 10);
            }

            // 坪沿三盏小灯
            Color lamp = active ? Color.FromArgb(255, 74, 204, 126) : Color.FromArgb(255, 196, 208, 220);
            using (Brush lampBrush = new SolidBrush(lamp))
            {
                g.FillEllipse(lampBrush, PadCx - 26, DeckTop - 2, 3, 3);
                g.FillEllipse(lampBrush, PadCx + 23, DeckTop - 2, 3, 3);
                g.FillEllipse(lampBrush, PadCx - 1.5f, DeckTop - 7, 3, 3);
            }

            // 火箭在场时，坪面上压出来一块阴影，让它看着是"放"在坪上而不是飘着
            if (active)
            {
                using (Brush shadow = new SolidBrush(Color.FromArgb(50, 60, 76, 100)))
                {
                    g.FillEllipse(shadow, PadCx - 16, DeckTop - 2, 32, 6);
                }
            }

            // 四个夹爪：待命时竖着夹住火箭脚，激活时向外翻开放手
            DrawClamp(g, PadCx - 12, DeckTop + 1, -1, 9.0f);
            DrawClamp(g, PadCx + 12, DeckTop + 1, 1, 9.0f);
            DrawClamp(g, PadCx - 8, DeckTop - 2, -1, 7.0f);
            DrawClamp(g, PadCx + 8, DeckTop - 2, 1, 7.0f);
        }

        private void DrawClamp(Graphics g, float x, float yBase, int side, float height)
        {
            GraphicsState state = g.Save();
            g.TranslateTransform(x, yBase);
            g.RotateTransform(side * 48.0f * armRetract);
            using (Pen claw = new Pen(Color.FromArgb(255, 138, 156, 174), 3.2f))
            {
                claw.StartCap = LineCap.Round;
                claw.EndCap = LineCap.Round;
                g.DrawLine(claw, 0, 0, 0, -height);
                // 爪尖一个小勾，向火箭那边弯
                g.DrawLine(claw, 0, -height, -side * 3.0f, -height + 0.5f);
            }
            g.Restore(state);
        }

        /// <summary>待命加注时的排汽：几小团白汽从火箭底部往两侧慢慢飘散。</summary>
        private void DrawVenting(Graphics g, float t)
        {
            for (int i = 0; i < 6; i++)
            {
                float phase = (t * 0.32f + i * 0.17f) % 1.0f;
                int side = i % 2 == 0 ? -1 : 1;
                float x = PadCx + side * (5 + phase * 16);
                float y = DeckTop - 2 - phase * 10;
                float r = 2.5f + phase * 4.5f;
                int alpha = (int)(110 * (1 - phase));
                using (Brush steam = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
                {
                    g.FillEllipse(steam, x - r, y - r * 0.8f, r * 2, r * 1.6f);
                }
            }
        }

        /// <summary>点火：一大团棉花糖似的白烟沿着坪面向两边翻滚、慢慢散掉；坪心先是一团炽白。</summary>
        private void DrawIgnition(Graphics g, float p)
        {
            // 先画远处的、大的；后画近处的
            for (int i = 11; i >= 0; i--)
            {
                int side = i % 2 == 0 ? -1 : 1;
                float o = (i / 2) + 1;
                float reach = (6 + o * 11) * (0.35f + p * 1.1f);
                float x = PadCx + side * reach;
                float r = (4 + o * 1.9f) * (0.6f + p * 1.7f);
                float y = DeckTop + 1 - r * 0.4f - p * 4.0f * (o / 6.0f);
                int alpha = (int)(235 * (1 - p) * (1 - p * 0.3f));
                if (alpha <= 0)
                {
                    continue;
                }
                using (GraphicsPath puff = new GraphicsPath())
                {
                    puff.AddEllipse(x - r, y - r * 0.8f, r * 2, r * 1.6f);
                    using (PathGradientBrush smoke = new PathGradientBrush(puff))
                    {
                        smoke.CenterColor = Color.FromArgb(alpha, 255, 255, 255);
                        smoke.SurroundColors = new[] { Color.FromArgb((int)(alpha * 0.55), 196, 210, 224) };
                        g.FillPath(smoke, puff);
                    }
                }
            }

            // 点火那一瞬间的炽白
            if (p < 0.45f)
            {
                float f = 1 - p / 0.45f;
                using (GraphicsPath core = new GraphicsPath())
                {
                    float rr = 9 + 14 * p;
                    core.AddEllipse(PadCx - rr, DeckTop - rr * 0.7f, rr * 2, rr * 1.4f);
                    using (PathGradientBrush glow = new PathGradientBrush(core))
                    {
                        glow.CenterColor = Color.FromArgb((int)(240 * f), 255, 244, 210);
                        glow.SurroundColors = new[] { Color.FromArgb(0, 255, 190, 90) };
                        g.FillPath(glow, core);
                    }
                }
            }
        }

        private void DrawHint(Graphics g)
        {
            string text = active ? "松手！" : "把球拖到发射台上";
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                using (Brush brush = new SolidBrush(active ? Color.FromArgb(255, 24, 140, 205) : Color.FromArgb(255, 110, 132, 152)))
                {
                    g.DrawString(text, active ? hintBoldFont : hintFont, brush, new RectangleF(0, 10, LW, 18), format);
                }
            }
        }

        private void UpdateWindowRegion()
        {
            if (Width <= 0 || Height <= 0)
            {
                return;
            }

            // 裁剪形状要跟画出来的圆角一致（外框 18 + 3px 留边），
            // 否则四角会露出一圈底色的直角"小耳朵"
            int radius = Math.Max(8, UiScale.Px(21));
            using (GraphicsPath path = CreateRoundedRectangle(new Rectangle(0, 0, Width, Height), radius))
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

    /// <summary>
    /// 「定时关机」的自定义对话框：选一个**时刻**和动作（关机 / 重启 / 睡眠 / 锁定）。
    ///
    /// 两种说法都支持：从现在算多少分钟，或者直接指定哪天的几点几分。
    /// 预设菜单只给到「2 小时后」，而实际要排的往往是「明天下午两点五分」这种，
    /// 光靠分钟数得自己算半天，还算错。
    /// </summary>
    internal sealed class PowerScheduleForm : Form
    {
        private readonly RadioButton modeDelay;
        private readonly RadioButton modeMoment;
        private readonly NumericUpDown amountBox;
        private readonly ComboBox unitBox;
        private readonly DateTimePicker datePicker;
        private readonly DateTimePicker timePicker;
        private readonly ComboBox actionBox;
        private readonly Label previewLabel;
        private readonly Button okButton;

        public string SelectedAction
        {
            get
            {
                switch (actionBox.SelectedIndex)
                {
                    case 1:
                        return "restart";
                    case 2:
                        return "sleep";
                    case 3:
                        return "lock";
                    default:
                        return "shutdown";
                }
            }
        }

        /// <summary>
        /// 用户最终选定的执行时刻。
        /// </summary>
        public DateTime When
        {
            get
            {
                if (modeMoment.Checked)
                {
                    return datePicker.Value.Date + timePicker.Value.TimeOfDay;
                }

                int amount = (int)amountBox.Value;
                return DateTime.Now.AddMinutes(unitBox.SelectedIndex == 1 ? amount * 60 : amount);
            }
        }

        public PowerScheduleForm()
        {
            Text = "\u5b9a\u65f6\u5173\u673a";
            // 这个对话框是原生控件拼的，坐标按 96 DPI 设计稿写死，
            // 构造完再统一乘一次 DPI 比例 —— WinForms 自带的自动缩放只会拉宽，靠不住。
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(396, 216);
            Font = new Font("Microsoft YaHei UI", 9.0f);
            BackColor = Color.White;

            // ---- 第一行：从现在算多少 ----
            modeDelay = new RadioButton();
            modeDelay.Text = "从现在算";
            modeDelay.AutoSize = true;
            modeDelay.Location = new Point(16, 19);
            Controls.Add(modeDelay);

            amountBox = new NumericUpDown();
            amountBox.Minimum = 1;
            amountBox.Maximum = 9999;
            amountBox.Value = 30;
            amountBox.Location = new Point(128, 17);
            amountBox.Width = 64;
            Controls.Add(amountBox);

            unitBox = new ComboBox();
            unitBox.DropDownStyle = ComboBoxStyle.DropDownList;
            unitBox.Items.AddRange(new object[] { "分钟后", "小时后" });
            unitBox.SelectedIndex = 0;
            unitBox.Location = new Point(198, 17);
            unitBox.Width = 76;
            Controls.Add(unitBox);

            // ---- 第二行：哪天的几点 ----
            modeMoment = new RadioButton();
            modeMoment.Text = "指定时刻";
            modeMoment.AutoSize = true;
            modeMoment.Location = new Point(16, 51);
            modeMoment.Checked = true;
            Controls.Add(modeMoment);

            datePicker = new DateTimePicker();
            datePicker.Format = DateTimePickerFormat.Custom;
            datePicker.CustomFormat = "yyyy 年 M 月 d 日";
            datePicker.Location = new Point(128, 49);
            datePicker.Width = 148;
            Controls.Add(datePicker);

            timePicker = new DateTimePicker();
            timePicker.Format = DateTimePickerFormat.Custom;
            timePicker.CustomFormat = "HH:mm";
            timePicker.ShowUpDown = true;
            timePicker.Location = new Point(284, 49);
            timePicker.Width = 76;
            Controls.Add(timePicker);

            // 初值给「一小时后的整五分」，比默认值更接近真实意图。
            // 跨天时日期一起跟上，否则会得到"今天 00:30"这种已经过去的时刻。
            DateTime soon = RoundUpToFiveMinutes(DateTime.Now.AddHours(1));
            datePicker.Value = soon.Date;
            timePicker.Value = soon;

            // ---- 动作 ----
            Label actionLabel = new Label();
            actionLabel.Text = "动作";
            actionLabel.AutoSize = true;
            actionLabel.ForeColor = Color.FromArgb(71, 85, 105);
            actionLabel.Location = new Point(16, 90);
            Controls.Add(actionLabel);

            actionBox = new ComboBox();
            actionBox.DropDownStyle = ComboBoxStyle.DropDownList;
            actionBox.Items.AddRange(new object[] { "关机", "重启", "睡眠", "锁定" });
            actionBox.SelectedIndex = 0;
            actionBox.Location = new Point(128, 87);
            actionBox.Width = 96;
            Controls.Add(actionBox);

            Panel divider = new Panel();
            divider.BackColor = Color.FromArgb(232, 237, 244);
            divider.Location = new Point(16, 124);
            divider.Size = new Size(364, 1);
            Controls.Add(divider);

            // ---- 结果预览 ----
            // 这一行不是装饰：日期和时间分两个框选，很容易只改了时间忘了日期，
            // 或者把"明天"选成"今天"。这里把最终会执行的时刻写成人话再确认一遍。
            previewLabel = new Label();
            previewLabel.AutoSize = false;
            previewLabel.Location = new Point(16, 132);
            previewLabel.Size = new Size(364, 22);
            previewLabel.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            Controls.Add(previewLabel);

            Label hintLabel = new Label();
            hintLabel.Text = "关机和重启由系统执行，中途重启电脑会让计划失效。";
            hintLabel.AutoSize = false;
            hintLabel.ForeColor = Color.FromArgb(150, 160, 175);
            hintLabel.Font = new Font("Microsoft YaHei UI", 8.0f);
            hintLabel.Location = new Point(16, 156);
            hintLabel.Size = new Size(364, 20);
            Controls.Add(hintLabel);

            okButton = new Button();
            okButton.Text = "确定";
            okButton.DialogResult = DialogResult.OK;
            okButton.Location = new Point(244, 180);
            okButton.Size = new Size(64, 28);
            Controls.Add(okButton);

            Button cancelButton = new Button();
            cancelButton.Text = "取消";
            cancelButton.DialogResult = DialogResult.Cancel;
            cancelButton.Location = new Point(316, 180);
            cancelButton.Size = new Size(64, 28);
            Controls.Add(cancelButton);

            AcceptButton = okButton;
            CancelButton = cancelButton;

            // 事件全部绑在最后。RadioButton 之间会互相取消选中，给一个
            // Checked = true 就会触发另一个的 CheckedChanged，而那个时刻
            // 下面的日期框、预览行、确定按钮都还没建出来。
            modeDelay.CheckedChanged += OnModeChanged;
            modeMoment.CheckedChanged += OnModeChanged;
            amountBox.ValueChanged += OnModeChanged;
            unitBox.SelectedIndexChanged += OnModeChanged;
            datePicker.ValueChanged += OnModeChanged;
            timePicker.ValueChanged += OnModeChanged;
            actionBox.SelectedIndexChanged += OnModeChanged;

            OnModeChanged(null, EventArgs.Empty);

            ScaleForDpi();
        }

        /// <summary>
        /// 按当前 DPI 把对话框和里面每个控件一起放大。
        ///
        /// 只放位置和尺寸，不放字体 —— 字体是「磅」，系统已经按 DPI 放大过一次了，
        /// 再乘一遍就成了两倍大，字会从框里挤出去。
        /// </summary>
        private void ScaleForDpi()
        {
            float scale = UiScale.Factor;
            if (Math.Abs(scale - 1f) < 0.01f)
            {
                return;
            }

            SuspendLayout();
            ClientSize = new Size(UiScale.Px(396), UiScale.Px(216));
            ScaleControls(this, 396, 216);
            ResumeLayout(true);
        }

        private static void ScaleControls(Control parent, int designWidth, int designHeight)
        {
            foreach (Control control in parent.Controls)
            {
                int left = control.Left;
                int top = control.Top;
                int width = control.Width;
                int height = control.Height;
                // 右下角对齐的控件：按到右边界/下边界的距离算，放大后依然贴边
                bool rightAligned = left + width >= designWidth - 4;
                bool bottomAligned = top + height >= designHeight - 4;

                if (!control.AutoSize)
                {
                    int newWidth = UiScale.Px(width);
                    int newHeight = UiScale.Px(height);
                    control.Size = new Size(newWidth, newHeight);
                    control.Left = rightAligned
                        ? UiScale.Px(designWidth) - newWidth - UiScale.Px(designWidth - left - width)
                        : UiScale.Px(left);
                    control.Top = bottomAligned
                        ? UiScale.Px(designHeight) - newHeight - UiScale.Px(designHeight - top - height)
                        : UiScale.Px(top);
                }
                else
                {
                    control.Left = UiScale.Px(left);
                    control.Top = UiScale.Px(top);
                }
            }
        }

        /// <summary>
        /// 把对话框原样渲染成 PNG，只给 <c>--render-power 路径</c> 自检用。
        /// </summary>
        public static void SavePreview(string path)
        {
            using (PowerScheduleForm dialog = new PowerScheduleForm())
            {
                // 必须先 Show 出来再画：DrawToBitmap 对还没创建句柄的子控件
                // 会画出空白，而这里几乎全是子控件。
                dialog.Show();
                Application.DoEvents();

                using (Bitmap bitmap = new Bitmap(dialog.Width, dialog.Height))
                {
                    dialog.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        }

        private static DateTime RoundUpToFiveMinutes(DateTime value)
        {
            DateTime floored = new DateTime(value.Year, value.Month, value.Day,
                value.Hour, value.Minute, 0);
            int add = 5 - floored.Minute % 5;
            return floored.AddMinutes(add);
        }

        /// <summary>
        /// 切换模式时把另一组控件禁用，同时刷新预览。
        /// 禁用而不只是不管它：两组都在可编辑状态时，用户改错了那一组也看不出来。
        /// </summary>
        private void OnModeChanged(object sender, EventArgs e)
        {
            bool moment = modeMoment.Checked;
            amountBox.Enabled = !moment;
            unitBox.Enabled = !moment;
            datePicker.Enabled = moment;
            timePicker.Enabled = moment;
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            DateTime when = When;
            string action = PowerActionText(actionBox.SelectedIndex);
            previewLabel.Text = "将在 " + DescribeMoment(when) + " " + action + "。";

            bool past = when <= DateTime.Now;
            previewLabel.ForeColor = past
                ? Color.FromArgb(198, 45, 45)
                : Color.FromArgb(37, 99, 235);
            if (past)
            {
                previewLabel.Text = "这个时刻已经过去了，换一个。";
            }

            okButton.Enabled = !past;
        }

        private static string PowerActionText(int index)
        {
            switch (index)
            {
                case 1:
                    return "重启";
                case 2:
                    return "睡眠";
                case 3:
                    return "锁定";
                default:
                    return "关机";
            }
        }

        /// <summary>
        /// 把时刻写成人话：今天 14:05 / 明天 14:05 / 11 月 30 日 14:05。
        /// 通知气泡里也用这一句，两边说法保持一致。
        /// </summary>
        internal static string DescribeMoment(DateTime when)
        {
            DateTime now = DateTime.Now;
            string time = when.ToString("HH:mm");

            if (when.Date == now.Date)
            {
                return "今天 " + time;
            }

            if (when.Date == now.Date.AddDays(1))
            {
                return "明天 " + time;
            }

            if (when.Date == now.Date.AddDays(2))
            {
                return "后天 " + time;
            }

            if (when.Year == now.Year)
            {
                return when.Month + " 月 " + when.Day + " 日 " + time;
            }

            return when.ToString("yyyy 年 M 月 d 日 ") + time;
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

    /// <summary>
    /// 面板上哪些行该显示，用户自己说了算。
    ///
    /// 存的是「隐藏了哪些」，不是「显示了哪些」：以后版本加了新字段，
    /// 默认就是显示的，旧配置不用迁移。注册表里存成一个逗号分隔的字符串，
    /// 出问题时用 regedit 也能肉眼改。
    /// </summary>
    internal static class PanelFieldSettings
    {
        private const string SettingsKey = @"Software\NetSpeedBall";
        private const string ValueName = "HiddenPanelFields";

        private static readonly object Sync = new object();
        private static HashSet<string> hidden;
        private static bool loaded;

        public static bool IsVisible(string id)
        {
            lock (Sync)
            {
                EnsureLoaded();
                return !hidden.Contains(id);
            }
        }

        public static void SetVisible(string id, bool visible)
        {
            if (string.IsNullOrEmpty(id))
            {
                return;
            }

            lock (Sync)
            {
                EnsureLoaded();
                bool changed = visible ? hidden.Remove(id) : hidden.Add(id);
                if (changed)
                {
                    Save();
                }
            }
        }

        public static void ShowAll()
        {
            lock (Sync)
            {
                EnsureLoaded();
                if (hidden.Count == 0)
                {
                    return;
                }

                hidden.Clear();
                Save();
            }
        }

        /// <summary>「全部显示」之后把菜单里的勾重新同步一遍。</summary>
        public static void SyncMenuChecks(ToolStripItemCollection items)
        {
            if (items == null)
            {
                return;
            }

            foreach (ToolStripItem item in items)
            {
                SyncMenuItems(item);
            }
        }

        private static void SyncMenuItems(ToolStripItem item)
        {
            ToolStripMenuItem menuItem = item as ToolStripMenuItem;
            if (menuItem == null)
            {
                return;
            }

            string id = menuItem.Tag as string;
            if (id != null)
            {
                menuItem.Checked = IsVisible(id);
            }

            foreach (ToolStripItem child in menuItem.DropDownItems)
            {
                SyncMenuItems(child);
            }
        }

        private static void EnsureLoaded()
        {
            if (loaded)
            {
                return;
            }

            loaded = true;
            hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey, false))
                {
                    if (key == null)
                    {
                        return;
                    }

                    string raw = key.GetValue(ValueName, string.Empty) as string;
                    if (string.IsNullOrEmpty(raw))
                    {
                        return;
                    }

                    string[] parts = raw.Split(',');
                    for (int i = 0; i < parts.Length; i++)
                    {
                        string id = parts[i].Trim();
                        if (id.Length > 0)
                        {
                            hidden.Add(id);
                        }
                    }
                }
            }
            catch
            {
                // 注册表读不了就当全部显示
            }
        }

        private static void Save()
        {
            try
            {
                StringBuilder text = new StringBuilder();
                foreach (string id in hidden)
                {
                    if (text.Length > 0)
                    {
                        text.Append(',');
                    }

                    text.Append(id);
                }

                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                {
                    if (key != null)
                    {
                        key.SetValue(ValueName, text.ToString(), RegistryValueKind.String);
                    }
                }
            }
            catch
            {
            }
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

        /// <summary>
        /// 计划任务名。用它实现"开机就是管理员，而且不弹 UAC"。
        ///
        /// 原理：计划任务里勾了"使用最高权限运行"，任务计划程序会用用户的完整令牌
        /// 直接拉起进程 —— 这不是"右键以管理员身份运行"，所以不会弹 UAC 确认框。
        /// 注册这种任务本身需要管理员权限，所以第一次得提权一次，之后就一劳永逸。
        /// </summary>
        private const string TaskName = "\u6d4b\u901f\u6d6e\u5149";

        public static bool IsEnabled(string appName)
        {
            return IsTaskEnabled() || (HasRunEntry(appName) && !IsBlockedByApprovedList(appName));
        }

        public static void SetEnabled(string appName, bool enabled)
        {
            if (!enabled)
            {
                DisableTask();
            }

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
        /// 注册"登录时以最高权限启动"的计划任务，并删掉旧的 Run 键启动项（避免启动两次）。
        /// 只有当前进程已经是管理员时才会成功。
        /// </summary>
        public static bool TryEnableElevated(string appName)
        {
            if (!NativeMethods.IsUserAnAdmin())
            {
                return false;
            }

            // schtasks 的 /TR 参数里，exe 路径本身要再用一对引号包起来：
            //   /TR "\"C:\Program Files\app.exe\""
            string quotedExe = "\"" + Application.ExecutablePath + "\"";
            string tr = quotedExe.Replace("\"", "\\\"");
            string arguments = "/Create /TN \"" + TaskName + "\" /TR \"" + tr +
                "\" /SC ONLOGON /RL HIGHEST /F";
            if (RunSchTasks(arguments) != 0)
            {
                return false;
            }

            // 任务和 Run 键同时存在会启动两次：第二次会被单实例锁挡掉，但球会闪一下
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key != null)
                    {
                        key.DeleteValue(appName, false);
                    }
                }

                WriteApprovedFlag(appName, false);
            }
            catch
            {
            }

            return true;
        }

        public static bool IsTaskEnabled()
        {
            return RunSchTasks("/Query /TN \"" + TaskName + "\"") == 0;
        }

        private static bool DisableTask()
        {
            return RunSchTasks("/Delete /TN \"" + TaskName + "\" /F") == 0;
        }

        /// <summary>
        /// 跑一次 schtasks.exe，返回退出码（0 = 成功；拿不到就 -1）。
        /// 标准输入要重定向并立刻关掉：万一系统想交互式问密码，会直接失败而不是挂住界面。
        /// </summary>
        private static int RunSchTasks(string arguments)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo("schtasks.exe", arguments);
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardInput = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.WindowStyle = ProcessWindowStyle.Hidden;

                using (Process process = Process.Start(info))
                {
                    if (process == null)
                    {
                        return -1;
                    }

                    try
                    {
                        process.StandardInput.Close();
                    }
                    catch
                    {
                    }

                    process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(8000))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }

                        return -1;
                    }

                    return process.ExitCode;
                }
            }
            catch
            {
                return -1;
            }
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
        /// <summary>SMBIOS 原始表在固件里的提供者签名，倒过来写就是 'RSMB'。</summary>
        public const uint RsMbProvider = 0x52534D42;
        /// <summary>「固件是否启用了虚拟化」这一项，任务管理器里的「虚拟化：已启用」就是它。</summary>
        public const int PfVirtualizationFirmwareEnabled = 21;
        /// <summary>NtQuerySystemInformation 的「内存列表」信息类，能拿到待机/已修改页数。</summary>
        public const int SystemMemoryListInformation = 80;
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

        /// <summary>WM_DPICHANGED：窗口所在显示器的 DPI 变了（用户改了缩放，或拖到另一块屏）。</summary>
        public const int WM_DPICHANGED = 0x02E0;

        [DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

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

        /// <summary>
        /// 任务管理器「性能」页下半截那一堆数字的来源：进程数、线程数、句柄数、
        /// 已提交、缓存、分页/非分页缓冲池，一次调用全给齐（单位是「页」，要乘 PageSize）。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct PerformanceInformation
        {
            public uint Size;
            public IntPtr CommitTotal;
            public IntPtr CommitLimit;
            public IntPtr CommitPeak;
            public IntPtr PhysicalTotal;
            public IntPtr PhysicalAvailable;
            public IntPtr SystemCache;
            public IntPtr KernelTotal;
            public IntPtr KernelPaged;
            public IntPtr KernelNonpaged;
            public IntPtr PageSize;
            public uint HandleCount;
            public uint ProcessCount;
            public uint ThreadCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime,
            out FileTime userTime);

        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool GetPerformanceInfo(ref PerformanceInformation info, uint size);

        /// <summary>开机到现在多少毫秒。任务管理器里那个「正常运行时间」。</summary>
        [DllImport("kernel32.dll")]
        public static extern ulong GetTickCount64();

        /// <summary>
        /// 一次性问出「有几个物理核」「L1/L2/L3 各多大」。
        /// relationshipType：0 = 物理核，2 = 缓存。
        /// 先传 null 问需要多大缓冲，再按真实大小申请一次 —— 这是这套 API 的标准用法。
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetLogicalProcessorInformationEx(int relationshipType,
            byte[] buffer, ref uint returnedLength);

        [DllImport("kernel32.dll")]
        public static extern bool IsProcessorFeaturePresent(int feature);

        /// <summary>读固件里的 SMBIOS 原始表，内存条的频率和插槽数只能从这里拿。</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint GetSystemFirmwareTable(uint providerSignature, uint tableId,
            byte[] buffer, uint bufferSize);

        /// <summary>
        /// 任务管理器「已缓存」= 待机页 + 已修改页，只能从这条内核接口问。
        /// GetPerformanceInfo 的 SystemCache 是「待机页 + 系统工作集」，比它大几百 MB，
        /// 直接拿来当「已缓存」会跟任务管理器对不上。
        /// 返回值 0 表示成功，非 0 是 NTSTATUS 错误码。
        /// </summary>
        [DllImport("ntdll.dll")]
        public static extern int NtQuerySystemInformation(int infoClass, byte[] buffer,
            int length, ref int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        // 睡眠 / 休眠（hibernate = true 就是休眠）。普通用户就能调，不需要管理员。
        [DllImport("powrprof.dll", SetLastError = true)]
        public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        // 锁定当前工作站，等同于 Win+L。
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool LockWorkStation();

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

    /// <summary>
    /// 全程序的 DPI 换算。
    ///
    /// 这个软件的界面全是自己一笔一笔画的，坐标都按 96 DPI 的设计稿写死。
    /// 用户把缩放改成 125% / 150% 时，如果只让系统把字号放大、坐标不变，
    /// 就会出现「字把格子撑爆、两列字叠在一起」。所以统一在这里算一个比例：
    /// 窗口尺寸乘它、画布整体 ScaleTransform 乘它，字号按像素造（不再随 DPI 自动变）。
    /// </summary>
    internal static class UiScale
    {
        private static int dpi = ReadDpi();

        public static float Factor
        {
            get { return dpi / 96.0f; }
        }

        public static int DpiValue
        {
            get { return dpi; }
        }

        /// <summary>
        /// 直接采用一个已知的 DPI（来自 WM_DPICHANGED 的 wParam）。
        /// 返回 true 表示比例确实变了，调用方要重新排一遍。
        /// </summary>
        public static bool Adopt(int value)
        {
            if (value < 72 || value > 480 || value == dpi)
            {
                return false;
            }

            dpi = value;
            return true;
        }

        /// <summary>重新读一次系统 DPI，返回 true 表示用户刚改过缩放。</summary>
        public static bool Refresh()
        {
            int current = ReadDpi();
            if (current == dpi)
            {
                return false;
            }

            dpi = current;
            return true;
        }

        /// <summary>把设计稿像素换算成真实像素。</summary>
        public static int Px(int designPixels)
        {
            return (int)Math.Round(designPixels * Factor);
        }

        public static float Px(float designPixels)
        {
            return (float)(designPixels * Factor);
        }

        /// <summary>按设计稿的磅值造字体，单位用像素 —— 这样只有画布变换会放大它，不会翻倍。</summary>
        public static Font Font(float points, FontStyle style)
        {
            return Font(points, style, "Segoe UI");
        }

        public static Font Font(float points, FontStyle style, string family)
        {
            return new Font(family, points * 96f / 72f, style, GraphicsUnit.Pixel);
        }

        private static int ReadDpi()
        {
            // 自查用：设了 NETSPEEDBALL_DPI 就按这个值画，方便在一台机器上
            // 直接看 100% / 125% / 150% / 200% 的排版，不用真去改系统缩放。
            string forced = Environment.GetEnvironmentVariable("NETSPEEDBALL_DPI");
            int value;
            if (!string.IsNullOrEmpty(forced) && int.TryParse(forced, out value) && value >= 72 && value <= 480)
            {
                return value;
            }

            // GetDpiForSystem 是 Win10 1607+ 的接口，在 PerMonitorV2 进程里
            // 用户改完缩放它马上就变 —— 这是"不重启也能跟上"的关键。
            try
            {
                uint systemDpi = NativeMethods.GetDpiForSystem();
                if (systemDpi >= 72 && systemDpi <= 480)
                {
                    return (int)systemDpi;
                }
            }
            catch
            {
            }

            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    return g.DpiX > 0 ? (int)Math.Round(g.DpiX) : 96;
                }
            }
            catch
            {
                return 96;
            }
        }
    }
}

