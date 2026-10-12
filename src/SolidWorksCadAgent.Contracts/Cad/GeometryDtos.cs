namespace SolidWorksCadAgent.Contracts.Cad
{
    public sealed class RectangleParameters
    {
        public double CenterXmm { get; set; }
        public double CenterYmm { get; set; }
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
    }

    public sealed class CircleParameters
    {
        public double CenterXmm { get; set; }
        public double CenterYmm { get; set; }
        public double DiameterMm { get; set; }
    }

    public sealed class ExtrudeParameters
    {
        public double DepthMm { get; set; }
    }
}
