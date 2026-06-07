using System.Text;

namespace ClubPortableLinker;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--render-ui")
        {
            ApplicationConfiguration.Initialize();
            var outPath = args.Length >= 2 ? args[1] : "ui.png";
            var w = args.Length >= 3 && int.TryParse(args[2], out var pw) ? pw : 1480;
            var h = args.Length >= 4 && int.TryParse(args[3], out var ph) ? ph : 920;
            var tab = args.Length >= 5 && int.TryParse(args[4], out var pt) ? pt : 0;
            using var form = new Form1();
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-4000, -4000);
            form.Size = new System.Drawing.Size(w, h);
            form.Show();
            form.SelectTabForRender(tab);
            for (var i = 0; i < 8; i++) Application.DoEvents();
            using var bmp = new System.Drawing.Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            form.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height));
            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            form.Close();
            return 0;
        }

        if (args.Length >= 1 && args[0] == "--render-icon")
        {
            var outPath = args.Length >= 2 ? args[1] : "icon.png";
            Form1.RenderIconStripForDev(outPath);
            return 0;
        }

        if (args.Length >= 1 && args[0] == "--make-ico")
        {
            var outPath = args.Length >= 2 ? args[1] : "ClubPortableLinker.ico";
            Form1.WriteIcoFile(outPath);
            Console.WriteLine($"ICO: {outPath}");
            return 0;
        }

        if (args.Length > 0)
        {
            ConfigureCliEncoding();
        }

        if (args.Length > 0)
        {
            return Cli.Run(args, Console.Out, Console.Error);
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new Form1());
        return 0;
    }

    private static void ConfigureCliEncoding()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // WinExe-запуск с pipe может не позволить менять OutputEncoding напрямую.
        }

        // Не заменяем Console.Out вручную: у WinExe при запуске из PowerShell
        // стандартный writer работает стабильнее, чем OpenStandardOutput().
    }
}
