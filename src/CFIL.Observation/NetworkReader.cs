using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace CFIL.Observation;

public static class NetworkReader
{
    public static NetworkView Read(string path)
    {
        using var reader=XmlReader.Create(path,new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null });
        var document=XDocument.Load(reader); var edges=new List<NetworkEdge>();
        foreach(var edge in document.Descendants("edge"))
        {
            var lanes=edge.Elements("lane").ToArray(); var shape=lanes.FirstOrDefault()?.Attribute("shape")?.Value;
            if(shape is null) continue;
            var points=shape.Split(' ',StringSplitOptions.RemoveEmptyEntries).Select(p=>p.Split(',')).Select(p=>new Point(
                double.Parse(p[0],CultureInfo.InvariantCulture),double.Parse(p[1],CultureInfo.InvariantCulture))).ToArray();
            edges.Add(new(edge.Attribute("id")!.Value,points,lanes.Length,edge.Attribute("function")?.Value=="internal"));
        }
        var all=edges.SelectMany(e=>e.Shape).ToArray();
        if(all.Length==0) throw new InvalidDataException("Network has no recorded geometry.");
        return new(edges.ToArray(),all.Min(p=>p.X),all.Min(p=>p.Y),all.Max(p=>p.X),all.Max(p=>p.Y));
    }
    public static (double X,double Y,double Heading) Locate(NetworkView network,string edgeId,double longitudinalX)
    {
        var edge=network.Edges.FirstOrDefault(e=>e.Id==edgeId);
        if(edge is null) return (longitudinalX,network.MaxY,0);
        // The archive contains X and edge identity, not a lane/Y observation. Use
        // recorded geometry only for an explicitly labelled longitudinal projection.
        var best=(X:edge.Shape[0].X,Y:edge.Shape[0].Y,Heading:0d); var distance=double.MaxValue;
        for(var i=1;i<edge.Shape.Length;i++)
        {
            var a=edge.Shape[i-1];var b=edge.Shape[i]; var dx=b.X-a.X;
            var fraction=Math.Abs(dx)<.0001?0:Math.Clamp((longitudinalX-a.X)/dx,0,1);
            var x=a.X+fraction*dx;var delta=Math.Abs(x-longitudinalX);
            if(delta<distance) {distance=delta;best=(x,a.Y+fraction*(b.Y-a.Y),Math.Atan2(b.Y-a.Y,dx));}
        }
        return best;
    }
}
