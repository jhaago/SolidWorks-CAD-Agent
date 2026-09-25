using System;
using SolidWorksCadAgent.AgentHost.Host;
using SolidWorksCadAgent.AgentHost.Jobs;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Ai;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.SolidWorksBridge.Session;

namespace SolidWorksCadAgent.AgentHost
{
    public static class AgentHostComposition
    {
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
    }
}
