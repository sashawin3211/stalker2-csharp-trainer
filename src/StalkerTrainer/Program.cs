using System.Drawing.Imaging;

namespace StalkerTrainer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] != "--render-preview")
        {
            Environment.ExitCode = ScanCli.Run(args);
            return;
        }
        ApplicationConfiguration.Initialize();
        if (args is ["--render-preview", var output])
        {
            using var preview = new MainForm(interactive: false);
            preview.ShowInTaskbar = false;
            preview.Opacity = 0;
            preview.StartPosition = FormStartPosition.Manual;
            preview.Location = new Point(-30000, -30000);
            preview.Show();
            Application.DoEvents();
            preview.PerformLayout();
            using var bitmap = new Bitmap(preview.Width, preview.Height);
            preview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.GetFullPath(output), ImageFormat.Png);
            preview.Close();
            return;
        }
        using var mutex = new Mutex(true, @"Local\StalkerTrainer-CSharp-1", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("Трейнер уже запущен.", "S.T.A.L.K.E.R. 2 Trainer");
            return;
        }
        Application.Run(new MainForm());
    }
}
