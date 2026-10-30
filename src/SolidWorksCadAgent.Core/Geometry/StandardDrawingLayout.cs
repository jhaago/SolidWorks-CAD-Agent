// Adapted from soheil-mp/solidworks-mcp f450dab, Services/DrawingLayout.cs (MIT).
// Copyright (c) 2026 Soheil and the solidworks-mcp contributors.
// See third_party/solidworks-mcp/LICENSE. Changes: bounded net48 planning-only contract; no paper enum/native claims.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SolidWorksCadAgent.Core.Geometry
{
    /// <summary>Bounding-box layout prediction. Native view outlines and templates still need independent inspection.</summary>
    public static class StandardDrawingLayout
    {
        private const double Gap=15;
        private static readonly double[] Scales={50,20,10,5,2,1,.5,.2,.1,.05,.02,.01,.005,.002,.001};
        private static readonly string[] Names={"front","back","top","bottom","right","left","iso"};
        private sealed class Slot { internal string View;internal int Col,Row;internal double W,H,X,Y; }
        public static JObject Create(JObject input)
        {
            PlanningInput.Keys(input,"sheetWidthMm","sheetHeightMm","modelSizeMm","views","projection","sheetScale","fixedScale","usableMm","hasFormat");
            double width=PlanningInput.Number(input,"sheetWidthMm",50,5000),height=PlanningInput.Number(input,"sheetHeightMm",50,5000);
            var size=PlanningInput.Array(input["modelSizeMm"],"modelSizeMm",3,1e-6,1e9);
            var views=input["views"] as JArray;
            if(views==null||views.Count<1||views.Count>7||views.Any(v=>v.Type!=JTokenType.String||!Names.Contains((string)v))||views.Select(v=>(string)v).Distinct().Count()!=views.Count)
                throw new ArgumentException("views must contain one to seven unique standard view names.");
            string projection=PlanningInput.Text(input,"projection","thirdAngle");
            if(projection!="firstAngle"&&projection!="thirdAngle")throw new ArgumentException("projection must be firstAngle or thirdAngle.");
            bool firstAngle=projection=="firstAngle",hasFormat=PlanningInput.Boolean(input,"hasFormat",true);
            double sheetScale=PlanningInput.Number(input,"sheetScale",1e-4,1e4,1);
            double? fixedScale=input["fixedScale"]==null?(double?)null:PlanningInput.Number(input,"fixedScale",1e-4,1e4);
            double[] usable;
            if(input["usableMm"]!=null)
            {
                usable=PlanningInput.Array(input["usableMm"],"usableMm",4,0,5000);
                if(usable[0]>=usable[2]||usable[1]>=usable[3]||usable[2]>width||usable[3]>height)
                    throw new ArgumentException("usableMm must be a positive rectangle inside the sheet.");
            }
            else
            {
                double band=hasFormat?Math.Max(40,Math.Min(60,.2*height)):0;
                // Tiny sheets can have no room after the reserved title band; return fits=false with a zero-height area.
                usable=new[]{hasFormat?20.0:10,Math.Min(10+band,height-10),width-10,height-10};
            }
            double uw=usable[2]-usable[0],uh=usable[3]-usable[1];
            var slots=new List<Slot>();var used=new List<(int,int)>();
            var requested=views.Values<string>().ToArray();
            foreach(string name in requested.Where(v=>v!="iso").Concat(requested.Where(v=>v=="iso")))
            {
                var cell=Cell(name,firstAngle,used);used.Add(cell);
                var axes=Axes(name);
                slots.Add(new Slot{View=name,Col=cell.Item1,Row=cell.Item2,W=Extent(axes.Item1,size),H=Extent(axes.Item2,size)});
            }
            slots=requested.Select(v=>slots.Single(s=>s.View==v)).ToList();
            var cols=slots.Select(s=>s.Col).Distinct().OrderBy(c=>c).ToArray();
            var rows=slots.Select(s=>s.Row).Distinct().OrderBy(r=>r).ToArray();
            double w1=cols.Sum(c=>slots.Where(s=>s.Col==c).Max(s=>s.W)),h1=rows.Sum(r=>slots.Where(s=>s.Row==r).Max(s=>s.H));
            double gapW=Gap*(cols.Length-1),gapH=Gap*(rows.Length-1);
            double fit=Math.Max(0,Math.Min((uw-gapW)/w1,(uh-gapH)/h1));
            double scale=fixedScale??(sheetScale<=fit*(1+1e-9)?sheetScale:Scales.Where(s=>s<=fit*(1+1e-9)).DefaultIfEmpty(Scales.Last()).First());
            var colW=cols.ToDictionary(c=>c,c=>slots.Where(s=>s.Col==c).Max(s=>s.W)*scale);
            var rowH=rows.ToDictionary(r=>r,r=>slots.Where(s=>s.Row==r).Max(s=>s.H)*scale);
            double totalW=colW.Values.Sum()+gapW,totalH=rowH.Values.Sum()+gapH;
            double x=(usable[0]+usable[2]-totalW)/2,y=(usable[1]+usable[3]-totalH)/2;
            foreach(int col in cols){foreach(var s in slots.Where(s=>s.Col==col))s.X=x+colW[col]/2;x+=colW[col]+Gap;}
            foreach(int row in rows){foreach(var s in slots.Where(s=>s.Row==row))s.Y=y+rowH[row]/2;y+=rowH[row]+Gap;}
            return new JObject{["unit"]="mm",["projection"]=projection,["scale"]=scale,["fitScale"]=fit,["gapMm"]=Gap,
                ["usableMm"]=new JArray(usable),["fits"]=uw>0&&uh>0&&totalW<=uw+1e-6&&totalH<=uh+1e-6,
                ["planningOnly"]=true,["nativeCreated"]=false,
                ["outlineScope"]="Predicted model bounding boxes only; native view outlines, annotations and actual title-block geometry are not inspected.",
                ["views"]=new JArray(slots.Select(s=>new JObject{["view"]=s.View,["column"]=s.Col,["row"]=s.Row,["centerXmm"]=s.X,["centerYmm"]=s.Y,
                    ["widthMm"]=s.W*scale,["heightMm"]=s.H*scale,["outlineMm"]=new JArray(s.X-s.W*scale/2,s.Y-s.H*scale/2,s.X+s.W*scale/2,s.Y+s.H*scale/2)}))};
        }
        private static (Vec3,Vec3) Axes(string view)
        {
            switch(view)
            {
                case "front":return(Vec3.UnitX,Vec3.UnitY);
                case "back":return(-Vec3.UnitX,Vec3.UnitY);
                case "top":return(Vec3.UnitX,-Vec3.UnitZ);
                case "bottom":return(Vec3.UnitX,Vec3.UnitZ);
                case "right":return(-Vec3.UnitZ,Vec3.UnitY);
                case "left":return(Vec3.UnitZ,Vec3.UnitY);
                default:return(new Vec3(1/Math.Sqrt(2),0,-1/Math.Sqrt(2)),new Vec3(-1/Math.Sqrt(6),2/Math.Sqrt(6),-1/Math.Sqrt(6)));
            }
        }
        private static double Extent(Vec3 u,double[] size)=>Math.Abs(u.X)*size[0]+Math.Abs(u.Y)*size[1]+Math.Abs(u.Z)*size[2];
        private static (int,int) Cell(string view,bool first,ICollection<(int,int)> used)
        {
            int f=first?-1:1;
            switch(view){case "front":return(0,0);case "top":return(0,f);case "bottom":return(0,-f);case "right":return(f,0);case "left":return(-f,0);case "back":return(2,0);}
            if(used.Count>0)
            {
                int c0=used.Min(u=>u.Item1),c1=used.Max(u=>u.Item1),r0=used.Min(u=>u.Item2),r1=used.Max(u=>u.Item2);
                foreach(var corner in new[]{(c1,r1),(c1,r0),(c0,r1),(c0,r0)})if(!used.Contains(corner))return corner;
            }
            foreach(var corner in new[]{(1,1),(1,-1),(-1,1),(-1,-1),(2,1),(2,-1),(0,2),(1,2)})if(!used.Contains(corner))return corner;
            throw new ArgumentException("No standard view grid cell is available.");
        }
    }
}
