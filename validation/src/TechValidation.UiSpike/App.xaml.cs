using System.Windows;

namespace TechValidation.UiSpike;

public partial class App : Application
{
    /// <summary>--selftest &lt;結果.json&gt; [--exit]：自動執行全部檢查並寫出結果。</summary>
    public static string? SelfTestOutput { get; private set; }
    public static bool ExitAfterSelfTest { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--selftest") SelfTestOutput = i + 1 < e.Args.Length && !e.Args[i + 1].StartsWith("--") ? e.Args[++i] : "selftest-result.json";
            else if (e.Args[i] == "--exit") ExitAfterSelfTest = true;
        }
        base.OnStartup(e);
        new MainWindow().Show();
    }
}
