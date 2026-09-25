using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using SolidWorksCadAgent.AgentHost.Ai;
using SolidWorksCadAgent.AgentHost.Host;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.AgentHost.Security;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Workspace;
using SolidWorksCadAgent.SolidWorksBridge;
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
            using (var bridge = new SolidWorksBridgeFacade(
                solidWorks,
                new WorkspacePolicy(settings.WorkspaceRoot)))
            using (var httpClient = new HttpClient())
            using (var server = new LocalHttpServer(
                settings.HostPrefix,
                AgentHostComposition.CreateRoutes(
                    repository,
                    solidWorks,
                    new OpenAiCadPlanningProvider(
                        httpClient,
                        new WindowsCredentialStore(),
                        settings.OpenAiModel),
                    bridge,
                    settings)))
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
