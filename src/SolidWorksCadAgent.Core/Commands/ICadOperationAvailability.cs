namespace SolidWorksCadAgent.Core.Commands
{
    /// <summary>Read-only registration check for Host plan preflight; it never invokes a handler.</summary>
    public interface ICadOperationAvailability
    {
        bool SupportsManagedReferences { get; }
        bool IsOperationRegistered(string commandName, int operationVersion);
    }
}
