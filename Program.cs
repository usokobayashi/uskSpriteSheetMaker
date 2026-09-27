using System;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: InternalsVisibleTo("SpriteSheetMakerTests")]

namespace SpriteSheetMaker
{
    internal static class Program
    {
        private static readonly object ErrorLogLock = new object();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiFlag);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                SetProcessDpiAwarenessContext(new IntPtr(-4));
            }
            catch (EntryPointNotFoundException)
            {
                SetProcessDPIAware();
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += Application_ThreadException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            var form = new MainForm();
            if (args != null && args.Length > 0 &&
                string.Equals(Path.GetExtension(args[0]), ProjectFile.Extension, StringComparison.OrdinalIgnoreCase))
                form.InitialProjectPath = args[0];
            Application.Run(form);
        }

        private static void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
        {
            string logPath = WriteErrorLog(e.Exception);
            try
            {
                using (var dialog = new Form
                {
                    Text = AppInfo.Name,
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.CenterScreen,
                    ShowInTaskbar = false,
                    BackColor = Color.FromArgb(17, 23, 28),
                    ForeColor = Color.White,
                    ClientSize = new Size(500, 238),
                    Font = new Font("Meiryo UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
                    TopMost = true
                })
                {
                    var title = new Label
                    {
                        Text = Loc.T("dialog.crashTitle"),
                        Location = new Point(28, 24),
                        Size = new Size(444, 30),
                        Font = new Font("Meiryo UI", 13f, FontStyle.Bold, GraphicsUnit.Point)
                    };
                    var message = new Label
                    {
                        Text = Loc.T("dialog.crashMessage", logPath),
                        Location = new Point(28, 68),
                        Size = new Size(444, 92),
                        ForeColor = Color.FromArgb(190, 198, 205),
                        AutoEllipsis = true
                    };
                    var close = new Button
                    {
                        Text = Loc.T("button.exit"),
                        Location = new Point(362, 178),
                        Size = new Size(110, 42),
                        BackColor = Color.FromArgb(82, 68, 255),
                        ForeColor = Color.White,
                        FlatStyle = FlatStyle.Flat,
                        DialogResult = DialogResult.OK
                    };
                    close.FlatAppearance.BorderColor = Color.FromArgb(115, 103, 255);
                    dialog.Controls.Add(title);
                    dialog.Controls.Add(message);
                    dialog.Controls.Add(close);
                    dialog.AcceptButton = close;
                    dialog.CancelButton = close;
                    dialog.ShowDialog();
                }
            }
            catch
            {
                // The log remains available even if an error prevents the custom dialog from opening.
            }
            Application.Exit();
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            WriteErrorLog(e.ExceptionObject as Exception ?? new Exception(Convert.ToString(e.ExceptionObject)));
        }

        private static string WriteErrorLog(Exception exception)
        {
            string directory = AppInfo.DataDirectory;
            string path = Path.Combine(directory, "error.log");
            try
            {
                lock (ErrorLogLock)
                {
                    Directory.CreateDirectory(directory);
                    // 記録が増え続けないよう、1MBを超えたら古い方を1世代だけ残して入れ替える。
                    try
                    {
                        var info = new FileInfo(path);
                        if (info.Exists && info.Length > 1024 * 1024)
                        {
                            string old = path + ".old";
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(path, old);
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    File.AppendAllText(path,
                        "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "]\r\n" +
                        (exception == null ? "Unknown error" : exception.ToString()) + "\r\n\r\n");
                }
            }
            catch
            {
                return Loc.T("message.logSaveFailed");
            }
            return path;
        }
    }
}
