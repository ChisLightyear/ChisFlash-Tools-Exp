using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace ChisFlashWaterClient;

static class Program
{
    private static int unhandledDialogShowing;

    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) => RecoverFromUiThreadException(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => ShowUnhandledException(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            RecoverFromUiThreadException(args.Exception);
            args.SetObserved();
        };

        ApplicationConfiguration.Initialize();
        Application.Run(new Form1());
    }

    private static void RecoverFromUiThreadException(Exception exception)
    {
        Debug.WriteLine(exception);

        try
        {
            Form1? mainForm = Application.OpenForms
                .OfType<Form1>()
                .FirstOrDefault(form => !form.IsDisposed && !form.Disposing);
            if (mainForm is not null)
            {
                mainForm.RecoverFromUiException(exception);
                return;
            }
        }
        catch
        {
        }

        ShowUnhandledException(exception);
    }

    private static void ShowUnhandledException(Exception? exception)
    {
        Exception actual = exception?.GetBaseException() ?? exception ?? new Exception("Unknown error.");
        Debug.WriteLine(actual);

        if (Interlocked.Exchange(ref unhandledDialogShowing, 1) != 0)
        {
            return;
        }

        try
        {
            string message = string.IsNullOrWhiteSpace(actual.Message)
                ? "程序遇到了未处理异常。"
                : actual.Message;

            MessageBox.Show(
                $"程序遇到了未处理异常。\nUnhandled exception:\n\n{message}",
                AppDisplayInfo.EnglishTitle,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
        }
        finally
        {
            Interlocked.Exchange(ref unhandledDialogShowing, 0);
        }
    }
}

internal static class AppDisplayInfo
{
    public static string Version { get; } = ResolveVersion();

    public static string EnglishTitle => $"ChisFlash Water Client v{Version} Test Build";

    public static string ChineseTitle => $"ChisFlash 水线客户端 v{Version} 测试版";

    private static string ResolveVersion()
    {
        string? informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Split('+', 2)[0].Trim();
        }

        string versionText = Application.ProductVersion ?? "0.0";

        versionText = versionText.Split('+', 2)[0];
        if (System.Version.TryParse(versionText, out var version))
        {
            if (version.Build == 0 && version.Revision < 0)
            {
                return $"{version.Major}.{version.Minor}";
            }

            if (version.Revision < 0)
            {
                return $"{version.Major}.{version.Minor}.{version.Build}";
            }
        }

        return versionText;
    }
}
