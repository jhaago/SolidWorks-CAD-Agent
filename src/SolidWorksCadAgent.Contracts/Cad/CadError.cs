namespace SolidWorksCadAgent.Contracts.Cad
{
    public sealed class CadError
    {
        public string Code { get; set; }
        public string Stage { get; set; }
        public string Message { get; set; }
        public string Detail { get; set; }
    }
}
