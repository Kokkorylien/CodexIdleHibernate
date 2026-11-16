using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodexIdleHibernate
{
    // Run separately with the normal app closed. All monitoring in this check uses dry-run mode.
    static class LifecycleChecks
    {
        static readonly StringBuilder results = new StringBuilder();
        static T Field<T>(MainWindow window, string name)
        {
            return (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
        }
        static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            results.AppendLine("PASS: " + name);
        }
        [STAThread]
        static int Main()
        {
            int code = 1;
            Application.EnableVisualStyles();
            var window = new MainWindow(true, true);
            window.Shown += async (s, e) => {
                try
                {
                    IntPtr handle = window.Handle;
                    var run = Field<CancellationTokenSource>(window, "session");
                    var tray = Field<NotifyIcon>(window, "tray");
                    Check(run != null, "dry-run monitoring starts before closing the window");
                    window.Close(); // Same UserClosing path as the title-bar X / Alt+F4.
                    Check(!window.Visible && !window.IsDisposed, "X hides the window without disposing it");
                    Check(tray.Visible, "notification icon remains registered while the window is hidden");
                    Check(!run.IsCancellationRequested, "X preserves the running monitoring session");
                    Check(!Field<System.Windows.Forms.Timer>(window, "clock").Enabled, "hidden window stops the display-only timer");
                    await Task.Delay(1500);
                    Check(Field<DataGridView>(window, "chats").Rows.Count > 0, "real status query finishes and updates the hidden window");
                    Check(!run.IsCancellationRequested && Field<CancellationTokenSource>(window, "session") == run,
                        "background monitoring remains active after hiding");
                    ((ToolStripMenuItem)tray.ContextMenuStrip.Items[0]).PerformClick();
                    Check(window.Visible && window.Handle == handle, "tray Show restores the same window");
                    Check(Field<System.Windows.Forms.Timer>(window, "clock").Enabled, "restoring resumes the display timer");
                    ((ToolStripMenuItem)tray.ContextMenuStrip.Items[tray.ContextMenuStrip.Items.Count - 1]).PerformClick();
                    Check(window.IsDisposed && !tray.Visible, "tray Exit closes the window and removes its icon");
                    Check(run.IsCancellationRequested, "tray Exit cancels the monitoring session");
                    results.AppendLine("No power commands executed; dry-run was forced by this check.");
                    code = 0;
                }
                catch (Exception error) { results.AppendLine("FAIL: " + error); Application.Exit(); }
                finally { File.WriteAllText(Path.Combine(Program.Root, "lifecycle-checks.txt"), results.ToString()); }
            };
            Application.Run(window);
            return code;
        }
    }
}
