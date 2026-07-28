namespace InputExecutor.Dashboard;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new DashboardForm(args.Contains("--diagnostic", StringComparer.OrdinalIgnoreCase)));
    }
}
