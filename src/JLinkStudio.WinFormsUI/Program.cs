using System.Runtime.InteropServices;

namespace JLinkStudio.WinFormsUI;

internal static class Program
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);

    [STAThread]
    private static void Main(string[] args)
    {
        // 支持 --page=rtt|watch|svd|log|settings 直接打开指定子界面（历史值 config 自动映射到 settings）
        string? initialPage = args
            .FirstOrDefault(a => a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))
            ?["--page=".Length..];

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => ShowFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowFatal(e.ExceptionObject as Exception);

        // 将系统定时器分辨率提升到 1ms，保证 200Hz~1000Hz 采样与 RTT 毫秒级采集精度
        TimeBeginPeriod(1);
        try
        {
            using var host = new AppHost();
            var form = new MainForm(host, initialPage);

#if DEBUG
            // 调试构建支持 --open-connect：启动后自动展开右上角连接配置浮层（配合 --shot 做视觉回归）
            if (args.Any(a => a.Equals("--open-connect", StringComparison.OrdinalIgnoreCase)))
            {
                var openTimer = new System.Windows.Forms.Timer { Interval = 1200 };
                openTimer.Tick += (_, _) =>
                {
                    openTimer.Stop();
                    openTimer.Dispose();
                    form.ShowConnectionPanel();
                };
                openTimer.Start();
            }

            // 调试构建支持 --shot=<path>：启动若干秒后把整窗渲染为 PNG，便于离屏视觉回归
            string? shotPath = args
                .FirstOrDefault(a => a.StartsWith("--shot=", StringComparison.OrdinalIgnoreCase))
                ?["--shot=".Length..];
            if (!string.IsNullOrWhiteSpace(shotPath))
            {
                var shotTimer = new System.Windows.Forms.Timer { Interval = 6000 };
                shotTimer.Tick += (_, _) =>
                {
                    shotTimer.Stop();
                    shotTimer.Dispose();
                    try
                    {
                        using var bitmap = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                        bitmap.Save(shotPath, System.Drawing.Imaging.ImageFormat.Png);

                        // 浮层等独立窗口单独出图，便于校验连接配置面板
                        int index = 0;
                        foreach (Form open in Application.OpenForms)
                        {
                            if (ReferenceEquals(open, form)) continue;
                            using var child = new Bitmap(open.Width, open.Height);
                            open.DrawToBitmap(child, new Rectangle(0, 0, open.Width, open.Height));
                            child.Save($"{shotPath}.{++index}.png", System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                    catch (Exception ex)
                    {
                        File.WriteAllText(shotPath + ".error.txt", ex.ToString());
                    }
                };
                shotTimer.Start();
            }
#endif

            Application.Run(form);
        }
        finally
        {
            TimeEndPeriod(1);
        }
    }

    private static void ShowFatal(Exception? ex)
    {
        if (ex == null) return;

        string logPath = string.Empty;
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JLinkStudioPro");
            Directory.CreateDirectory(directory);
            logPath = Path.Combine(directory, "crash.log");
            File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
        }
        catch
        {
            // 日志写入失败不影响提示
        }

        MessageBox.Show($"{ex.Message}\r\n\r\n详细堆栈已写入:\r\n{logPath}",
            "JLink-Studio-Pro 未处理异常", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
