using System.Drawing;
using System.Windows.Forms;

namespace AquariumSaver;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Register global exception handlers before any UI work.
        Application.SetUnhandledExceptionMode(
            UnhandledExceptionMode.CatchException);

        Application.ThreadException += (_, argsEx) =>
        {
            AppLog.Log(
                "UI-thread exception:" +
                Environment.NewLine +
                argsEx.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, argsEx) =>
        {
            AppLog.Log(
                "Unhandled exception:" +
                Environment.NewLine +
                argsEx.ExceptionObject);
        };

        try
        {
            var windowed = args.Any(a => a.Equals("--windowed", StringComparison.OrdinalIgnoreCase));
            if (windowed)
            {
                RunWindowed(Settings.Load());
                return 0;
            }

            var mode = args.Length == 0 ? Mode.None : ParseMode(args[0]);

            return mode switch
            {
                Mode.Run or Mode.None => RunScreensaver(Settings.Load()),
                Mode.Preview => RunPreview(ParseHwnd(args), Settings.Load()),
                Mode.Configure => RunConfigure(),
                _ => 0,
            };
        }
        catch (Exception ex)
        {
            AppLog.Log(
                "Startup exception:" +
                Environment.NewLine +
                ex);
            return 1;
        }
    }

    static Mode ParseMode(string arg)
    {
        var first = arg.TrimStart('/', '-').ToLowerInvariant();
        return first switch { "s" => Mode.Run, "p" => Mode.Preview, "c" => Mode.Configure, _ => Mode.Configure };
    }

    static IntPtr ParseHwnd(string[] args)
    {
        if (args.Length >= 1 && args[0].Contains(':'))
        {
            var parts = args[0].Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[1], out var h1)) return new IntPtr(h1);
        }
        if (args.Length >= 2 && int.TryParse(args[1], out var h2)) return new IntPtr(h2);
        return IntPtr.Zero;
    }

    static int RunScreensaver(SettingsData settings)
    {
        var forms = new List<ScreensaverForm>();
        var screens = Screen.AllScreens;
        if (screens.Length == 0) return 0;

        var exitWatcher = new ExitWatcher();
        // One shared seed for all monitors — every full-screen window
        // displays a viewport into the same virtual-desktop aquarium.
        int sharedSeed = Environment.TickCount;

        foreach (var screen in screens)
        {
            forms.Add(new ScreensaverForm(screen, exitWatcher, sharedSeed, settings));
        }

        var context = new ApplicationContext();
        foreach (var form in forms)
        {
            form.FormClosed += (_, _) =>
            {
                forms.Remove(form);
                if (forms.Count == 0) context.ExitThread();
            };
            form.Show();
        }

        Application.Run(context);
        return 0;
    }

    static int RunPreview(IntPtr parentHwnd, SettingsData settings)
    {
        if (parentHwnd == IntPtr.Zero) return RunScreensaver(settings);
        var form = new PreviewForm(parentHwnd, settings);
        Application.Run(form);
        return 0;
    }

    static int RunConfigure()
    {
        new ConfigForm().ShowDialog();
        return 0;
    }

    static void RunWindowed(SettingsData settings)
    {
        // Windowed dev mode: create a form without a Screen so InitScene
        // picks the three-argument (local aquarium) constructor.
        var form = new ScreensaverForm(null, null, 42, settings);
        form.FormBorderStyle = FormBorderStyle.Sizable;
        form.Text = "AquariumSaver (Windowed Debug Mode)";
        form.StartPosition = FormStartPosition.CenterScreen;
        form.TopMost = false;
        Application.Run(form);
    }

    enum Mode { None, Run, Preview, Configure }
}
