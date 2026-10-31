// Adapted from soheil-mp/solidworks-mcp, f450dab, Geometry/AxisSpec.cs (MIT).
// Copyright (c) 2026 Soheil and the solidworks-mcp contributors.
// See third_party/solidworks-mcp/LICENSE. Changes: net48, no System.Text.Json, reject nonfinite/zero.
using System;
using System.Globalization;
using System.Linq;

namespace SolidWorksCadAgent.Core.Geometry
{
    public static class AxisSpec
    {
        public static Vec3 Parse(string text)
        {
            if(string.IsNullOrWhiteSpace(text))throw new ArgumentException("An axis or vector is required.");
            string t=text.Trim().ToLowerInvariant().Replace(" ","");
            if(t.Contains(","))
            {
                var parts=t.Trim('[',']').Split(',');
                if(parts.Length!=3)throw new ArgumentException("Direction must contain three components.");
                var values=parts.Select(p=>double.TryParse(p,NumberStyles.Float,CultureInfo.InvariantCulture,out var n)&&Vec3.Finite(n)?n:throw new ArgumentException("Direction must be finite.")).ToArray();
                return Vec3.From(values).Normalized();
            }
            double sign=1;
            if(t.StartsWith("-",StringComparison.Ordinal)){sign=-1;t=t.Substring(1);}
            else if(t.StartsWith("+",StringComparison.Ordinal))t=t.Substring(1);
            else if(t.EndsWith("-",StringComparison.Ordinal)){sign=-1;t=t.Substring(0,t.Length-1);}
            else if(t.EndsWith("+",StringComparison.Ordinal))t=t.Substring(0,t.Length-1);
            switch(t){case "x":return Vec3.UnitX*sign;case "y":return Vec3.UnitY*sign;case "z":return Vec3.UnitZ*sign;default:throw new ArgumentException("Unsupported axis.");}
        }
    }
}
