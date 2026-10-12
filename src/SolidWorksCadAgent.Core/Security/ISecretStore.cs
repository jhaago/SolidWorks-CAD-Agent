namespace SolidWorksCadAgent.Core.Security
{
    public interface ISecretStore
    {
        void Set(string target, string secret);
        string Get(string target);
        bool Exists(string target);
        void Delete(string target);
    }
}
