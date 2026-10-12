using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using SolidWorksCadAgent.AgentHost.Ai;
using SolidWorksCadAgent.AgentHost.Configuration;
using SolidWorksCadAgent.AgentHost.Host;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.AgentHost.Security;
using SolidWorksCadAgent.AgentHost.Design;
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
            using (var instance = new Mutex(true, "Local\\SolidWorksCadAgent.AgentHost", out var first))
            {
                if (!first)
                {
                    Console.Error.WriteLine("The CAD Agent Host is already running. No stored jobs were changed.");
                    return 1;
                }
                return Run();
            }
        }

        private static int Run()
        {
            var dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SolidWorksCadAgent");
            var databasePath = Path.Combine(dataDirectory, "jobs.db");
            var settingsStore = new JsonAgentSettingsStore(Path.Combine(dataDirectory, "settings.json"));
            var settings = settingsStore.Load();
            var secretStore = new WindowsCredentialStore();

            ISolidWorksSession realSession = null;
            IDisposable realExecutorLifetime = null;
            using (var repository = new SqliteJobRepository(databasePath))
            using (var httpClient = new HttpClient())
            using (var shutdown = new CancellationTokenSource())
            {
                try
                {
                    repository.InitializeAsync().GetAwaiter().GetResult();
                    var interrupted = repository.RecoverInterruptedJobsAsync().GetAwaiter().GetResult();
                    if (interrupted > 0) Console.WriteLine("Marked " + interrupted + " interrupted jobs as failed; no CAD commands were replayed.");
                    var routes = AgentHostComposition.CreateRoutesForMode(
                        repository,
                        settings,
                        settingsStore,
                        secretStore,
                        () => realSession = new SolidWorksSession(settings.SolidWorksExecutablePath),
                        () => new OpenAiCadPlanningProvider(
                            httpClient,
                            secretStore,
                            settings.OpenAiModel),
                        session =>
                        {
                            var bridge = new SolidWorksBridgeFacade(
                                session,
                                new WorkspacePolicy(settings.WorkspaceRoot),
                                repository);
                            realExecutorLifetime = bridge;
                            return bridge;
                        });

                    var designRepository = new DesignSessionRepository(Path.Combine(dataDirectory, "design-intake.db"));
                    designRepository.Initialize();
                    var imageStore = new ReferenceImageStore(settings.WorkspaceRoot);
                    var designService = new DesignIntakeService(designRepository, imageStore,
                        new OpenAiImageDesignInterpreter(httpClient,
                            () => secretStore.Get(OpenAiCadPlanningProvider.OpenAiCredentialTarget), settings.OpenAiModel),
                        routes.PlanApprovedDesignAsync);
                    routes.ConfigureDesignIntake(new DesignRoutes(designService, imageStore));

                    using (var server = new LocalHttpServer(settings.HostPrefix, routes))
                    {
                        Console.CancelKeyPress += (sender, args) =>
                        {
                            args.Cancel = true;
                            shutdown.Cancel();
                        };

                        Console.WriteLine(
                            "SolidWorks CAD Agent Host listening on " + settings.HostPrefix +
                            " in " + settings.ExecutionMode + " mode.");
                        server.RunAsync(shutdown.Token).GetAwaiter().GetResult();
                    }
                }
                finally
                {
                    realExecutorLifetime?.Dispose();
                    realSession?.Dispose();
                }
            }

            return 0;
        }
    }
}
