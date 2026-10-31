namespace SolidWorksCadAgent.Contracts.Cad
{
    public sealed class CadParametricHoleIntent
    {
        public double DiameterMm { get; set; }
        public double Xmm { get; set; }
        public double Ymm { get; set; }
    }
    public sealed class CadParametricHoleObservation : CadParametricSketchObservation { }
}
