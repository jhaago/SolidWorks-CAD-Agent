using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using SolidWorksCadAgent.Core.Remote;
using SolidWorksCadAgent.RemoteAgent.Host;
using SolidWorksCadAgent.RemoteAgent.Platform;
using SolidWorksCadAgent.RemoteAgent.Security;

namespace SolidWorksCadAgent.RemoteAgent
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch (EntryPointNotFoundException) { SetProcessDPIAware(); }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var instance = new Mutex(true, "Local\\SolidWorksCadAgent.RemoteAgent", out bool first))
            {
                if (!first)
                {
                    MessageBox.Show("The Remote Workstation Agent is already running. Open its control window.");
                    return;
                }
                var clock = new RemoteClock();
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolidWorksCadAgent", "Remote", "devices.dat");
                var pairing = new RemotePairingCoordinator(clock, new DpapiDeviceCredentialStore(path));
                var capture = new WindowsDesktopCapture(clock);
                var input = new WindowsInputSink(capture);
                using (var sessions = new RemoteSessionCoordinator(clock, pairing, input))
                using (var watchdog = new System.Threading.Timer(_ => sessions.Tick(), null, 250, 250))
                using (var agentHost = new AgentHostRemoteClient())
                using (var server = new RemoteHttpServer(new RemoteRoutes(pairing, sessions, new RemoteFrameCache(clock, capture.Capture), clock, agentHost.Forward)))
                {
                    try { server.Start(new Uri("http://127.0.0.1:5079/")); }
                    catch (HttpListenerException)
                    {
                        MessageBox.Show("Remote Agent could not reserve its loopback address. Run register-remote-agent-url.ps1 as described in the bundle, or close an older RemoteAgent process. No process was stopped automatically.", "Remote Workstation setup");
                        return;
                    }
                    Application.Run(new MainForm(pairing, sessions));
                }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
    }
}
