using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodexIdleHibernate
{
    static class Program
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
        static bool Signal(string name)
        {
            try { using (var signal = EventWaitHandle.OpenExisting(name)) signal.Set(); return true; }
            catch (WaitHandleCannotBeOpenedException) { return false; }
        }
        public static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
        public static string CallerId()
        {
            return CallerContext.Resolve(Root, Environment.GetEnvironmentVariable("CODEX_THREAD_ID"),
                Environment.GetEnvironmentVariable("CODEX_SESSION_ID"), Environment.GetEnvironmentVariable("CODEX_HOME"));
        }
        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                if (args.Contains("--stop"))
                {
                    return Signal(@"Local\CodexIdleHibernate.Stop") ? 0 : 2;
                }
                if (args.Contains("--check"))
                {
                    var result = new DesktopBridge(CallerId()).QueryAsync(CancellationToken.None).GetAwaiter().GetResult();
                    File.WriteAllText(Path.Combine(Root, "last-check.json"), Json.Encode(result.Record()), new System.Text.UTF8Encoding(false));
                    return 0;
                }
                if (DesktopIdentity.RelaunchIfPackaged(args)) return 0;
                SetCurrentProcessExplicitAppUserModelID("KoTools.CodexIdleHibernate");
                bool created;
                using (var mutex = new Mutex(true, @"Local\CodexIdleHibernate", out created))
                {
                    if (!created)
                    {
                        if (!Signal(@"Local\CodexIdleHibernate.Show"))
                            MessageBox.Show("程序正在启动，请稍后再次打开。", "Codex 空闲后休眠");
                        return 0;
                    }
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new MainWindow(args.Contains("--arm"), args.Contains("--dry-run")));
                }
                return 0;
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(Root, "last-error.txt"), DateTime.Now.ToString("o") + " " + error);
                if (!args.Contains("--check")) MessageBox.Show(error.Message, "启动失败");
                return 1;
            }
        }
    }

    sealed class MainWindow : Form
    {
        readonly Label status = new Label(), timing = new Label();
        readonly Button start = new Button(), stop = new Button();
        readonly CheckBox dryRun = new CheckBox();
        readonly DataGridView chats = new DataGridView();
        readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer { Interval = 250 };
        readonly DesktopBridge bridge;
        readonly object logLock = new object();
        readonly EventWaitHandle stopSignal;
        readonly RegisteredWaitHandle stopRegistration;
        readonly EventWaitHandle showSignal;
        readonly RegisteredWaitHandle showRegistration;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip trayMenu;
        readonly ToolStripMenuItem trayStart, trayStop;
        CancellationTokenSource session;
        DateTime? deadline;
        string phase = "stopped";
        bool closing;
        bool exitRequested;
        public MainWindow(bool arm, bool simulate)
        {
            bridge = new DesktopBridge(Program.CallerId());
            Text = "Codex 空闲后休眠";
            ShowInTaskbar = true;
            ShowIcon = true;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = new Font("Microsoft YaHei UI", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(740, 495);
            MinimumSize = new Size(680, 450);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(248, 249, 251);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 6 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            var title = new Label { Text = "等待 Codex 完成，再休眠电脑", Font = new Font(Font.FontFamily, 15F, FontStyle.Bold), Dock = DockStyle.Fill };
            var hint = new Label { Text = "每 60 秒检查本机全部 Codex 会话（含当前会话）。\n空闲 30 秒后复查并休眠；点 X 后仍在托盘运行。", Dock = DockStyle.Fill };
            status.Text = "尚未开始监测。"; status.Dock = DockStyle.Fill; status.ForeColor = Color.FromArgb(35, 83, 160);
            chats.Dock = DockStyle.Fill; chats.ReadOnly = true; chats.AllowUserToAddRows = false;
            chats.AllowUserToDeleteRows = false; chats.AllowUserToResizeRows = false; chats.RowHeadersVisible = false;
            chats.BackgroundColor = Color.White; chats.BorderStyle = BorderStyle.FixedSingle;
            chats.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; chats.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            chats.MultiSelect = false; chats.ColumnHeadersHeight = 30;
            chats.Columns.Add("status", "状态"); chats.Columns.Add("title", "会话");
            chats.Columns[0].FillWeight = 20; chats.Columns[1].FillWeight = 80;
            timing.Dock = DockStyle.Fill; timing.TextAlign = ContentAlignment.MiddleLeft;
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            start.Text = "开始监测"; start.Size = new Size(115, 34);
            stop.Text = "取消监测"; stop.Size = new Size(115, 34); stop.Enabled = false;
            dryRun.Text = "仅测试（不休眠）"; dryRun.AutoSize = true; dryRun.Checked = simulate; dryRun.Margin = new Padding(16, 8, 0, 0);
            var logs = new Button { Text = "查看日志", Size = new Size(100, 34), Margin = new Padding(16, 3, 0, 0) };
            logs.Click += (s, e) => { string dir = Path.Combine(Program.Root, "logs"); Directory.CreateDirectory(dir); Process.Start("explorer.exe", "\"" + dir + "\""); };
            actions.Controls.AddRange(new Control[] { start, stop, dryRun, logs });
            layout.Controls.Add(title); layout.Controls.Add(hint); layout.Controls.Add(status); layout.Controls.Add(chats); layout.Controls.Add(timing); layout.Controls.Add(actions);
            Controls.Add(layout);
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("显示窗口", null, (s, e) => RestoreWindow());
            trayMenu.Items.Add(new ToolStripSeparator());
            trayStart = new ToolStripMenuItem("开始监测", null, async (s, e) => await ArmAsync());
            trayStop = new ToolStripMenuItem("取消监测", null, (s, e) => Cancel());
            trayMenu.Items.Add(trayStart); trayMenu.Items.Add(trayStop);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("退出", null, (s, e) => { exitRequested = true; Close(); });
            tray = new NotifyIcon { Icon = Icon, Text = Text + " · 未开始监测", ContextMenuStrip = trayMenu, Visible = true };
            tray.DoubleClick += (s, e) => RestoreWindow();
            UpdateTray("未开始监测");
            stopSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CodexIdleHibernate.Stop");
            stopRegistration = ThreadPool.RegisterWaitForSingleObject(stopSignal, (s, timedOut) => {
                if (!closing && IsHandleCreated)
                {
                    try { BeginInvoke((Action)(() => { if (!closing && phase != "committed") Cancel(); })); }
                    catch (InvalidOperationException) { }
                }
            }, null, Timeout.Infinite, false);
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CodexIdleHibernate.Show");
            showRegistration = ThreadPool.RegisterWaitForSingleObject(showSignal, (s, timedOut) => {
                if (!closing && IsHandleCreated)
                {
                    try { BeginInvoke((Action)(() => { if (!closing) RestoreWindow(); })); }
                    catch (InvalidOperationException) { }
                }
            }, null, Timeout.Infinite, false);
            start.Click += async (s, e) => await ArmAsync();
            stop.Click += (s, e) => Cancel();
            FormClosing += (s, e) => {
                if (e.CloseReason == CloseReason.UserClosing && !exitRequested)
                {
                    e.Cancel = true;
                    Hide(); clock.Stop();
                    Log("window_hidden", new { monitoring = session != null && !session.IsCancellationRequested });
                    return;
                }
                closing = true; if (session != null) session.Cancel(); clock.Stop();
                stopRegistration.Unregister(null); stopSignal.Dispose();
                showRegistration.Unregister(null); showSignal.Dispose();
                tray.Visible = false; tray.Dispose(); trayMenu.Dispose();
            };
            clock.Tick += (s, e) => UpdateClock(); clock.Start();
            Shown += async (s, e) => { if (arm) await ArmAsync(); };
        }
        void RestoreWindow()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            clock.Start(); UpdateClock();
            Activate(); BringToFront();
        }
        void UpdateTray(string message)
        {
            bool running = session != null && !session.IsCancellationRequested;
            trayStart.Enabled = !running;
            trayStop.Enabled = running && phase != "committed";
            string text = Text + " · " + message;
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }
        void Log(string eventName, object detail)
        {
            string dir = Path.Combine(Program.Root, "logs");
            lock (logLock)
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl"),
                    Json.Encode(new { time = DateTimeOffset.Now.ToString("o"), eventName = eventName, detail = detail }) + Environment.NewLine,
                    new System.Text.UTF8Encoding(false));
            }
        }
        void Cancel()
        {
            if (session == null) return;
            session.Cancel(); deadline = null; phase = "stopped";
            status.Text = "已取消监测。"; timing.Text = "本次不会休眠，可随时重新开始。";
            stop.Enabled = false;
            UpdateTray("已取消监测");
        }
        async Task ArmAsync()
        {
            if (session != null) return;
            var run = new CancellationTokenSource(); session = run;
            bool simulate = dryRun.Checked;
            start.Enabled = false; dryRun.Enabled = false; stop.Enabled = true;
            UpdateTray("正在监测");
            try
            {
                Log("armed", new { intervalSeconds = 60, countdownSeconds = 30, dryRun = simulate });
                var loop = new MonitorLoop(bridge.QueryAsync, ct => CommitAsync(run, simulate, ct), item => {
                    Log(item.Stage, new { message = item.Message, snapshot = item.Snapshot == null ? null : item.Snapshot.Record() });
                    if (!closing && !run.IsCancellationRequested && IsHandleCreated)
                        BeginInvoke((Action)(() => { if (!closing && session == run && !run.IsCancellationRequested) ShowEvent(item); }));
                });
                await loop.RunAsync(run.Token);
                status.Text = simulate ? "测试完成：已走完休眠流程，未执行休眠。" : "已执行一次休眠，监测已停止。";
                phase = "stopped"; deadline = null; timing.Text = "如需再次监测，请点击「开始监测」。";
                Log("completed", new { dryRun = simulate });
            }
            catch (OperationCanceledException) { Log("cancelled", new { reason = closing ? "window_closed" : "user_cancelled" }); }
            catch (Exception error)
            {
                status.Text = "监测已停止：" + error.Message; deadline = null; phase = "stopped";
                timing.Text = "请处理后重新开始监测。";
                try { Log("stopped_on_error", new { message = error.Message }); } catch (IOException) { }
            }
            finally
            {
                session = null; run.Dispose();
                if (!closing) { start.Enabled = true; dryRun.Enabled = true; stop.Enabled = false; UpdateTray(status.Text); }
            }
        }
        Task CommitAsync(CancellationTokenSource run, bool simulate, CancellationToken token)
        {
            var done = new TaskCompletionSource<bool>();
            if (closing || token.IsCancellationRequested) { done.SetCanceled(); return done.Task; }
            // Serialize the final power decision with the Cancel / window-close handlers on the UI thread.
            BeginInvoke((Action)(async () => {
                if (closing || session != run || token.IsCancellationRequested) { done.TrySetCanceled(); return; }
                try
                {
                    deadline = null; phase = "committed"; stop.Enabled = false;
                    UpdateTray(simulate ? "正在完成演练" : "正在休眠");
                    Log(simulate ? "dry_run_would_hibernate" : "hibernate_requested", new { command = "shutdown.exe /h" });
                    if (!simulate)
                    {
                        status.Text = "正在执行休眠……";
                        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe"), "/h")
                            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                        using (var process = Process.Start(info))
                        {
                            Task<string> stderr = process.StandardError.ReadToEndAsync();
                            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                            await Task.Run(() => process.WaitForExit());
                            string error = await stderr; await stdout;
                            if (process.ExitCode != 0) throw new IOException("休眠命令失败（" + process.ExitCode + "）：" + error.Trim());
                        }
                    }
                    done.TrySetResult(true);
                }
                catch (Exception error) { done.TrySetException(error); }
            }));
            return done.Task;
        }
        void ShowEvent(MonitorEvent item)
        {
            if (item.Stage == "waiting")
            {
                phase = "waiting"; deadline = item.DeadlineUtc; UpdateClock(); return;
            }
            status.Text = item.Message;
            UpdateTray(item.Message);
            phase = item.Stage; deadline = item.DeadlineUtc;
            if (item.Snapshot != null)
            {
                chats.Rows.Clear();
                foreach (var chat in item.Snapshot.Chats)
                {
                    string text = chat.IsActive ? "运行中" : chat.Status == "notLoaded" ? "未加载" : chat.Status == "idle" ? "空闲" : chat.Status;
                    int i = chats.Rows.Add(text, chat.Title);
                    chats.Rows[i].DefaultCellStyle.ForeColor = chat.IsActive ? Color.FromArgb(175, 83, 12) : Color.FromArgb(70, 75, 82);
                    chats.Rows[i].Cells[1].ToolTipText = chat.Id;
                }
            }
            UpdateClock();
        }
        void UpdateClock()
        {
            if (!deadline.HasValue) return;
            int seconds = Math.Max(0, (int)Math.Ceiling((deadline.Value - DateTime.UtcNow).TotalSeconds));
            timing.Text = phase == "countdown" ? "休眠前复查倒计时：" + seconds + " 秒　·　现在仍可取消"
                : "下次检查：" + seconds + " 秒后　·　上次检查 " + DateTime.Now.AddSeconds(seconds - 60).ToString("HH:mm:ss");
        }
    }
}
