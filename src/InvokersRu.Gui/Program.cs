using System;
using System.Threading;
using System.Windows.Forms;

namespace InvokersRu.Gui;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var running = new Mutex(false, PatcherUpdateProtocol.RunningMutex);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
