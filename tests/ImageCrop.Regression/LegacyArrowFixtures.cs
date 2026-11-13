using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Avalonia;

namespace SnipFlow.ImageCrop.Regression;

internal static partial class Program
{
    // Deliberately uses only the pre-0.8.4 bool API so the same harness runs against both binaries.
    static int WriteLegacyArrowFixtures()
    {
        var fixtures=new List<object>();
        var directions=new[]{("horizontal",new Point(60,120),new Point(260,120)),("diagonal",new Point(50,40),new Point(270,200)),("short",new Point(100,100),new Point(104,100))};
        foreach(var (direction,start,end) in directions)foreach(var width in new[]{1.0,4.0,32.0})foreach(var both in new[]{false,true}) {
            var name=direction+"-w"+width.ToString(CultureInfo.InvariantCulture)+(both?"-double":"-single");
            using var image=RenderItem(Arrow(start,end,width,both),320,240);
            fixtures.Add(new{name,direction,width,doubleEnded=both,pixelSha256=PixelHash(image),pixelWidth=image.PixelSize.Width,pixelHeight=image.PixelSize.Height});
            if(direction=="horizontal"&&width==4)Save("legacy-"+name+".png",image);
        }
        var path=Path.Combine(Output,"legacy-arrow-fixtures.json");
        var report=new{mode="fixture-only",fullRegressionRun=false,fixtureCount=fixtures.Count,productAssembly=Product.Location,productAssemblySha256=Hash(File.ReadAllBytes(Product.Location)),productVersion=Product.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,safety="Empty application; no Window, OS or routed input, installation or deployment.",fixtures};
        File.WriteAllText(path,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Legacy arrow fixture-only evidence: {fixtures.Count} pixel hashes. This is not a full regression run. {path}");
        return 0;
    }
}
