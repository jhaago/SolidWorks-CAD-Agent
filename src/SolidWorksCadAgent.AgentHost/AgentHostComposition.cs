using System;
using SolidWorksCadAgent.AgentHost.Configuration;
using SolidWorksCadAgent.AgentHost.Host;
using SolidWorksCadAgent.AgentHost.Jobs;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.AgentHost.Planning;
using SolidWorksCadAgent.AgentHost.Simulation;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Ai;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.Security;
using SolidWorksCadAgent.SolidWorksBridge.Session;

namespace SolidWorksCadAgent.AgentHost
{
    public static class AgentHostComposition
    {
        public static AgentRoutes CreateRoutesForMode(
            SqliteJobRepository repository,
            AgentSettings settings,
            JsonAgentSettingsStore settingsStore,
            ISecretStore secretStore,
            Func<ISolidWorksSession> realSessionFactory,
            Func<ICadPlanningProvider> realPlanningProviderFactory,
            Func<ISolidWorksSession, ICadCommandExecutor> realCommandExecutorFactory)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (settingsStore == null) throw new ArgumentNullException(nameof(settingsStore));
            if (secretStore == null) throw new ArgumentNullException(nameof(secretStore));

            if (settings.ExecutionMode == ExecutionMode.Simulation)
            {
                return CreateRoutes(
                    repository,
                    new SimulatedSolidWorksSession(),
                    new DeterministicCadPlanningProvider(),
                    new SimulatedCadCommandExecutor(),
                    settings,
                    settingsStore,
                    secretStore);
            }

            if (realSessionFactory == null) throw new ArgumentNullException(nameof(realSessionFactory));
            if (realPlanningProviderFactory == null) throw new ArgumentNullException(nameof(realPlanningProviderFactory));
            if (realCommandExecutorFactory == null) throw new ArgumentNullException(nameof(realCommandExecutorFactory));

            var solidWorks = realSessionFactory();
            if (solidWorks == null) throw new InvalidOperationException("The real SOLIDWORKS session factory returned null.");
            var planningProvider = realPlanningProviderFactory();
            if (planningProvider == null) throw new InvalidOperationException("The real planning provider factory returned null.");
            var commandExecutor = realCommandExecutorFactory(solidWorks);
            if (commandExecutor == null) throw new InvalidOperationException("The real CAD executor factory returned null.");

            return CreateRoutes(
                repository,
                solidWorks,
                planningProvider,
                commandExecutor,
                settings,
                settingsStore,
                secretStore);
        }

        public static AgentRoutes CreateRoutes(
            SqliteJobRepository repository,
            ISolidWorksSession solidWorks,
            ICadPlanningProvider planningProvider,
            ICadCommandExecutor commandExecutor,
            AgentSettings settings)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));
            if (solidWorks == null) throw new ArgumentNullException(nameof(solidWorks));
            if (planningProvider == null) throw new ArgumentNullException(nameof(planningProvider));
            if (commandExecutor == null) throw new ArgumentNullException(nameof(commandExecutor));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            var coordinator = new JobCoordinator(repository, planningProvider, commandExecutor, settings);
            return new AgentRoutes(repository, solidWorks, coordinator);
        }

        public static AgentRoutes CreateRoutes(
            SqliteJobRepository repository,
            ISolidWorksSession solidWorks,
            ICadPlanningProvider planningProvider,
            ICadCommandExecutor commandExecutor,
            AgentSettings settings,
            JsonAgentSettingsStore settingsStore,
            ISecretStore secretStore)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));
            if (solidWorks == null) throw new ArgumentNullException(nameof(solidWorks));
            if (planningProvider == null) throw new ArgumentNullException(nameof(planningProvider));
            if (commandExecutor == null) throw new ArgumentNullException(nameof(commandExecutor));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (settingsStore == null) throw new ArgumentNullException(nameof(settingsStore));
            if (secretStore == null) throw new ArgumentNullException(nameof(secretStore));

            var coordinator = new JobCoordinator(repository, planningProvider, commandExecutor, settings);
            var settingsService = new AgentSettingsService(settings, settingsStore, secretStore, repository);
            return new AgentRoutes(repository, solidWorks, coordinator, settingsService);
        }
    }
}
