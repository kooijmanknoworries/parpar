using System.Windows;

namespace Par2.App;

public partial class App : Application
{
    public static bool AutoStartMonitor { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        AutoStartMonitor = e.Args.Any(a => a.Equals("--monitor", StringComparison.OrdinalIgnoreCase));
        base.OnStartup(e);
    }
}
