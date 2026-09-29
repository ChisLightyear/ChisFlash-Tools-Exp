using GbaMultiMenuBuilder.Services;

namespace GbaMultiMenuBuilder;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        if (BatterylessExperimentService.TryRun(args))
        {
            return;
        }

        if (GbcMenuRebakeService.TryRun(args))
        {
            return;
        }

        if (RomMetadataWorkerService.TryRun(args))
        {
            return;
        }

        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        Application.Run(new Form1());
    }    
}
