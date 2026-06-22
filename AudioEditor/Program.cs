using System;
using System.Windows.Forms;
using WaveEdit.UI;

namespace WaveEdit;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var form = new MainForm();

        // Allow "open with" / drag a file onto the exe.
        if (args.Length > 0 && System.IO.File.Exists(args[0]))
            form.Shown += (_, _) => form.TryOpenPath(args[0]);

        Application.Run(form);
    }
}
