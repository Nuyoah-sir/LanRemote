namespace LanRemote.IsolatedAcceptance;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new LanRemote.Acceptance.App(isolatedOnly: true);
        app.InitializeComponent();
        app.Run();
    }
}
