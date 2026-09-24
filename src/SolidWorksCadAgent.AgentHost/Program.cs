using System;
using System.IO;
using System.Threading;
using SolidWorksCadAgent.AgentHost.Host;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.SolidWorksBridge.Session;

namespace SolidWorksCadAgent.AgentHost
{
    internal static class Program
    {
        private static int Main()
        {
            var settings = new AgentSettings();
            var dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SolidWorksCadAgent");
            var databasePath = Path.Combine(dataDirectory, "jobs.db");

            using (var repository = new SqliteJobRepository(databasePath))
            using (var solidWorks = new SolidWorksSession())
            using (var server = new LocalHttpServer(
                settings.HostPrefix,
                new AgentRoutes(repository, solidWorks)))
            using (var shutdown = new CancellationTokenSource())
            {
                repository.InitializeAsync().GetAwaiter().GetResult();
                Console.CancelKeyPress += (sender, args) =>
                {
                    args.Cancel = true;
                    shutdown.Cancel();
                };

                Console.WriteLine("SolidWorks CAD Agent Host listening on " + settings.HostPrefix);
                server.RunAsync(shutdown.Token).GetAwaiter().GetResult();
            }

            return 0;
        }
    }
}
