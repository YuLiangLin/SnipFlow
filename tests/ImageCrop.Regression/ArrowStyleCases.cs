using System.IO.Compression;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Media;
using SnipFlow.Editor;
using SnipFlow.Imaging;

namespace SnipFlow.ImageCrop.Regression;

internal static partial class Program
{
    static Type StyleType=>Product.GetType("SnipFlow.Editor.ArrowStyleOptions",true)!;
    static readonly string[] StyleFields={"StartHead","EndHead","StartSize","EndSize","LineStyle"};
    // Always create a fresh record: reflection must never mutate Default or a shared item style.
    static object Style(string start="None",string end="Arrow",string startSize="Medium",string endSize="Medium",string line="Solid")
    {
        var result=Activator.CreateInstance(StyleType)!;var values=new[]{start,end,startSize,endSize,line};
        for(int n=0;n<StyleFields.Length;n++){var property=StyleType.GetProperty(StyleFields[n])!;property.SetValue(result,Enum.Parse(property.PropertyType,values[n]));}
        return result;
    }
    static object StyleCopy(object original,string field,string value)
    {
        var result=Activator.CreateInstance(StyleType)!;
        foreach(var name in StyleFields){var property=StyleType.GetProperty(name)!;property.SetValue(result,name==field?Enum.Parse(property.PropertyType,value):property.GetValue(original));}
        return result;
    }
    static object StyledArrow(object style,Point? start=null,Point? end=null,double width=4)
    {
        var item=Arrow(start??new Point(60,120),end??new Point(260,120),width);Set(item,"ArrowStyle",style);return item;
    }
    static void EqualStyle(object? actual,object expected,string message)=>Check(actual is not null&&actual.Equals(expected),message);
    static List<Point> HeadPaint(PixelBuffer image,Point tip,Vector inward,double width)
    {
        var points=new List<Point>();var side=new Vector(-inward.Y,inward.X);const int radius=80;
        for(int y=Math.Max(0,(int)tip.Y-radius);y<Math.Min(image.Height,(int)tip.Y+radius);y++)
        for(int x=Math.Max(0,(int)tip.X-radius);x<Math.Min(image.Width,(int)tip.X+radius);x++) {
            var point=new Point(x+.5,y+.5);var delta=(Vector)(point-tip);var along=Vector.Dot(delta,inward);
            if(along>=0&&along<=radius&&Math.Abs(Vector.Dot(delta,side))>width/2+1&&image.Pixels[(y*image.Width+x)*4+3]>128)points.Add(point);
        }
        return points;
    }
    static Delegate ObserveStylePanel(object panel,Action<object> observer)
    {
        var changed=panel.GetType().GetEvent("ValueChanged")!;
        var argsType=changed.EventHandlerType!.GetGenericArguments()[0];
        var factory=typeof(Program).GetMethod(nameof(CreateStyleObserver),Any)!.MakeGenericMethod(argsType);
        var handler=(Delegate)factory.Invoke(null,new object[]{observer})!;
        changed.AddEventHandler(panel,handler);return handler;
    }
    static Delegate CreateStyleObserver<T>(Action<object> observer) where T:EventArgs =>new EventHandler<T>((_,args)=>observer(args));
    static void ArrowStyleCases()
    {
        var first=new Point(60,120);var last=new Point(260,120);var complex=Style("Circle","Open","Small","Large","Dash");
        Case("arrow style defaults remain compatible with the original single arrow",()=> {
            var value=Activator.CreateInstance(StyleType)!;EqualStyle(value,Style(),"Arrow style defaults changed");Check((bool)Static(StyleType,"IsValid",value)!,"Default style invalid");var item=StyledArrow(value);using var image=RenderItem(item,320,240);using var original=RenderItem(Arrow(first,last),320,240);Check(PixelHash(image)==PixelHash(original),"Default style changed single-arrow pixels");
        });
        Case("all endpoint kinds render and hit-test independently at both ends",()=> {
            foreach(var kind in new[]{"None","Arrow","Triangle","Open","Diamond","Circle"})foreach(var atStart in new[]{true,false}) {
                var style=atStart?Style(kind,"None"):Style("None",kind);var item=StyledArrow(style);using var image=RenderItem(item,320,240);var points=HeadPaint(Pixels(image),atStart?first:last,atStart?new Vector(1,0):new Vector(-1,0),4);
                Check(OpaqueCount(Pixels(image))>0,"Headless or headed arrow lost its shaft");
                Check(kind=="None"?points.Count==0:points.Count>0,"Endpoint paint missing/extra: "+kind+" start="+atStart);
                foreach(var point in points.Take(8))Check((bool)Call(item,"HitTest",point,.2)!,"Painted "+kind+" endpoint is not selectable");
                if(!atStart&&kind!="None")Save("style-end-"+kind.ToLowerInvariant()+".png",image);
            }
        });
        Case("endpoint size controls independently enlarge each head",()=> {
            var counts=new List<int>();foreach(var size in new[]{"Small","Medium","Large"}){using var image=RenderItem(StyledArrow(Style("None","Triangle","Medium",size)),320,240);counts.Add(HeadPaint(Pixels(image),last,new Vector(-1,0),4).Count);}
            Check(counts[0]>0&&counts[0]<counts[1]&&counts[1]<counts[2],"Endpoint size did not increase visible head area");
            using var leftSmall=RenderItem(StyledArrow(Style("Triangle","Triangle","Small","Large")),320,240);using var rightSmall=RenderItem(StyledArrow(Style("Triangle","Triangle","Large","Small")),320,240);var a=Pixels(leftSmall);var b=Pixels(rightSmall);Check(HeadPaint(a,first,new Vector(1,0),4).Count<HeadPaint(b,first,new Vector(1,0),4).Count&&HeadPaint(a,last,new Vector(-1,0),4).Count>HeadPaint(b,last,new Vector(-1,0),4).Count,"Head sizes are coupled instead of independent");
        });
        Case("solid dash and dot affect shaft while heads remain solid and gaps stay selectable",()=> {
            var counts=new List<int>();var hashes=new List<string>();PixelBuffer? reference=null;List<Point>? headPoints=null;
            foreach(var line in new[]{"Solid","Dash","Dot"}) {
                var item=StyledArrow(Style("None","Diamond","Medium","Large",line));using var image=RenderItem(item,320,240);var pixels=Pixels(image);var count=Enumerable.Range(100,120).Count(x=>At(pixels,x,120).A>128);counts.Add(count);hashes.Add(PixelHash(image));
                if(line=="Solid"){reference=pixels;headPoints=HeadPaint(pixels,last,new Vector(-1,0),4);}else{Check(count>0&&count<counts[0],line+" shaft did not show gaps");var gap=Enumerable.Range(100,120).First(x=>At(pixels,x,120).A<16);Check((bool)Call(item,"HitTest",new Point(gap+.5,120.5),.2)!,"Shaft gap cannot be selected");foreach(var point in headPoints!)Check(At(pixels,(int)point.X,(int)point.Y)==At(reference!,(int)point.X,(int)point.Y),line+" changed solid endpoint paint");}
                Save("style-line-"+line.ToLowerInvariant()+".png",image);
            }
            Check(counts[0]>115&&hashes.Distinct().Count()==3,"Shaft style variants are not distinct");
        });
        Case("representative styled short arrows remain finite and render safely",()=> {
            foreach(var kind in new[]{"Arrow","Triangle","Open","Diamond","Circle"})foreach(var length in new[]{0.0,.5,4.0})foreach(var width in new[]{1.0,32.0}) {
                var item=StyledArrow(Style(kind,kind,"Large","Small","Dot"),new Point(100,100),new Point(100+length,100),width);using var image=RenderItem(item,320,240);Check(image.PixelSize==new PixelSize(320,240),"Short style broke rendering");Check(length<.5?OpaqueCount(Pixels(image))==0:OpaqueCount(Pixels(image))>0,"Short styled arrow paint incorrect: "+kind+"/"+length+"/"+width);
            }
        });
        Case("style clone copy and equality preserve all five immutable fields",()=> {
            var item=StyledArrow(complex);var clone=Call(item,"Clone")!;EqualStyle(Get(clone,"ArrowStyle"),complex,"Clone lost style");Check((bool)Call(item,"ContentEquals",clone)!,"Equal style clone differs");var changed=StyleCopy(complex,"LineStyle","Dot");Set(clone,"ArrowStyle",changed);Check(!(bool)Call(item,"ContentEquals",clone)!,"Style-only change ignored");Call(item,"CopyFrom",clone);EqualStyle(Get(item,"ArrowStyle"),changed,"CopyFrom lost style");EqualStyle(StyleType.GetProperty("Default")!.GetValue(null),Style(),"Style fixture mutated shared Default");
        });
        Case("full style setter updates selection and factory with undo redo",()=> {
            var editor=ArrowEditor(Arrow(first,last));var original=PixelHash(editor.ExportImage());Set(editor,"ArrowStyle",complex);EqualStyle(Get(First(editor),"ArrowStyle"),complex,"Full style setter did not change selected arrow");EqualStyle(Get(editor,"SelectedArrowStyle"),complex,"Selected style unavailable");Check(UndoDepth(editor)==1,"Style setter should add one undo");var factory=Call(editor,"CreateShapeAnnotation",AnnotationTool.Arrow,first)!;EqualStyle(Get(factory,"ArrowStyle"),complex,"Factory lost style default");var after=PixelHash(editor.ExportImage());editor.Undo();Check(PixelHash(editor.ExportImage())==original,"Style undo pixels mismatch");EqualStyle(Get(editor,"ArrowStyle"),complex,"Undo should not revert drawing preference");editor.Redo();Check(PixelHash(editor.ExportImage())==after,"Style redo mismatch");var depth=UndoDepth(editor);Set(editor,"ArrowStyle",Style("Circle","Open","Small","Large","Dash"));Check(UndoDepth(editor)==depth,"Equal record style created history");
        });
        Case("multi-selection line diff preserves each arrow heads and sizes in one undo",()=> {
            var a=Style("Circle","Diamond","Small","Large","Dash");var b=Style("Open","Triangle","Large","Small","Solid");var editor=ArrowEditor(StyledArrow(a,new Point(30,70),new Point(270,70)),StyledArrow(b,new Point(30,170),new Point(270,170)));var next=StyleCopy(b,"LineStyle","Dot");Call(editor,"UpdateArrowStyle",b,next);
            EqualStyle(Get(Items(editor)[0]!,"ArrowStyle"),StyleCopy(a,"LineStyle","Dot"),"Line edit overwrote first arrow fields");EqualStyle(Get(Items(editor)[1]!,"ArrowStyle"),next,"Line edit overwrote second arrow fields");Check(UndoDepth(editor)==1,"Multi-style diff should be atomic");editor.Undo();EqualStyle(Get(Items(editor)[0]!,"ArrowStyle"),a,"Undo lost first mixed style");EqualStyle(Get(Items(editor)[1]!,"ArrowStyle"),b,"Undo lost second mixed style");editor.Redo();Check(UndoDepth(editor)==1,"Style redo history mismatch");var revision=editor.Revision;Call(editor,"UpdateArrowStyle",next,StyleCopy(next,"LineStyle","Dot"));Check(editor.Revision==revision&&UndoDepth(editor)==1,"No-op style diff added history");
        });
        Case("legacy double-arrow API changes heads while preserving sizes and shaft style",()=> {
            var item=StyledArrow(complex);Set(item,"DoubleEndedArrow",true);EqualStyle(Get(item,"ArrowStyle"),Style("Arrow","Arrow","Small","Large","Dash"),"Legacy model toggle reset other fields");Set(item,"DoubleEndedArrow",false);EqualStyle(Get(item,"ArrowStyle"),Style("None","Arrow","Small","Large","Dash"),"Legacy model single toggle reset other fields");var editor=ArrowEditor(StyledArrow(complex));Set(editor,"ArrowStyle",complex);Set(editor,"DoubleEndedArrow",true);EqualStyle(Get(First(editor),"ArrowStyle"),Style("Arrow","Arrow","Small","Large","Dash"),"Legacy editor toggle reset selected style");EqualStyle(Get(editor,"ArrowStyle"),Style("Arrow","Arrow","Small","Large","Dash"),"Legacy editor toggle reset drawing preference");
        });
        Case("styled duplicate session and rotation retain every style field",()=> {
            var editor=ArrowEditor(StyledArrow(complex));var snapshot=Call(editor,"CaptureSession")!;editor.DuplicateSelection();Check(Items(editor).Count==2,"Styled duplicate missing");foreach(var item in Items(editor).Cast<object>())EqualStyle(Get(item,"ArrowStyle"),complex,"Duplicate lost style");var command=Enum.Parse(Product.GetType("SnipFlow.Editor.ImageTransformCommand",true)!,"RotateRight");Call(editor,"ApplyImageTransform",command,true);foreach(var item in Items(editor).Cast<object>())EqualStyle(Get(item,"ArrowStyle"),complex,"Rotate lost style");var captured=(System.Collections.IList)Get(snapshot,"Annotations")!;EqualStyle(Get(captured[0]!,"ArrowStyle"),complex,"Session clone mutated during editing");
        });
        Case("invalid model and editor styles reject null or unsupported enum values atomically",()=> {
            var editor=ArrowEditor(StyledArrow(complex));var before=PixelHash(editor.ExportImage());var initial=Get(editor,"ArrowStyle");var revision=editor.Revision;
            var invalids=new List<object?>{null};foreach(var field in StyleFields){var bad=Style();var property=StyleType.GetProperty(field)!;property.SetValue(bad,Enum.ToObject(property.PropertyType,999));invalids.Add(bad);}
            foreach(var bad in invalids){Check(!(bool)Static(StyleType,"IsValid",bad)!,"Invalid style passed IsValid");Rejected(()=>Set(editor,"ArrowStyle",bad));EqualStyle(Get(editor,"ArrowStyle"),initial!,"Rejected style changed default");Check(editor.Revision==revision&&PixelHash(editor.ExportImage())==before,"Rejected style changed document");var item=StyledArrow(complex);Rejected(()=>Set(item,"ArrowStyle",bad));EqualStyle(Get(item,"ArrowStyle"),complex,"Rejected style changed model");}
        });
        Case("style panel programmatic value and language refresh do not emit edits",()=> {
            var panel=Activator.CreateInstance(Product.GetType("SnipFlow.Editor.ArrowStylePanel",true)!)!;var events=new List<object>();var observer=ObserveStylePanel(panel,args=>events.Add(args));
            Set(panel,"Value",complex);Set(panel,"Value",Style("Circle","Open","Small","Large","Dash"));Call(panel,"RefreshLanguage");Check(events.Count==0,"Panel synchronization emitted user-edit event");GC.KeepAlive(observer);
        });
        Case("direction presets carry explicit intent even when primary already matches",()=> {
            foreach(var (both,primaryStart) in new[]{(true,"Arrow"),(true,"None"),(false,"None")}) {
                var a=Style(both?"None":"Triangle","Circle","Small","Large","Dot");var b=Style(primaryStart,"Arrow","Large","Small","Dash");var editor=ArrowEditor(StyledArrow(a,new Point(30,70),new Point(270,70)),StyledArrow(b,new Point(30,170),new Point(270,170)));
                var panel=Activator.CreateInstance(Product.GetType("SnipFlow.Editor.ArrowStylePanel",true)!)!;var events=new List<object>();var observer=ObserveStylePanel(panel,args=>events.Add(args));Set(panel,"Value",b);Call(panel,"ApplyDirectionPreset",both);
                Check(events.Count==1&&Equals(Get(events[0],"DirectionPreset"),both),"Preset did not emit one explicit direction intent");if((both&&primaryStart=="Arrow")||(!both&&primaryStart=="None"))EqualStyle(Get(events[0],"Value"),Get(events[0],"Previous")!,"Same-value fixture should preserve primary panel value");
                // Exercise the product endpoint-preset API separately from the panel event contract.
                Set(editor,"DoubleEndedArrow",(bool)Get(events[0],"DirectionPreset")!);
                EqualStyle(Get(Items(editor)[0]!,"ArrowStyle"),Style(both?"Arrow":"None","Arrow","Small","Large","Dot"),"Preset missed secondary head or overwrote its size/line");EqualStyle(Get(Items(editor)[1]!,"ArrowStyle"),Style(both?"Arrow":"None","Arrow","Large","Small","Dash"),"Preset overwrote primary size/line");Check(UndoDepth(editor)==1,"Preset should be one document undo");editor.Undo();EqualStyle(Get(Items(editor)[0]!,"ArrowStyle"),a,"Preset undo lost secondary style");EqualStyle(Get(Items(editor)[1]!,"ArrowStyle"),b,"Preset undo lost primary style");GC.KeepAlive(observer);
            }
        });
        var project=Path.Combine(Output,"styled-arrow.snipflow");
        Case("schema 8 stores fixed English style names and reopens exact pixels",()=> {
            var editor=ArrowEditor(StyledArrow(complex));var before=PixelHash(editor.ExportImage());Static(StoreType,"SaveProject",editor,project);using(var archive=ZipFile.OpenRead(project)){var json=ReadManifest(archive);var annotation=json["Annotations"]![0]!.AsObject();var style=annotation["ArrowStyle"]!.AsObject();Check((int)json["SchemaVersion"]! ==8&&style.Count==5&&!annotation.ContainsKey("DoubleEndedArrow"),"Style schema or single source of truth invalid");foreach(var field in StyleFields)Check(style[field]!.GetValue<string>()==Get(complex,field)!.ToString(),"Persisted enum name changed: "+field);}
            var reopened=new EditorView();Static(StoreType,"RestoreProject",reopened,project);EqualStyle(Get(First(reopened),"ArrowStyle"),complex,"Reopened style mismatch");Check(PixelHash(reopened.ExportImage())==before&&!reopened.HasEdits,"Styled project pixels/saved state changed");
        });
        Case("schema 6 legacy double flag maps to medium solid arrows and upgrades on save",()=> {
            foreach(var both in new[]{false,true}){var path=MutateProject(project,"legacy-schema6-arrow-"+both+".snipflow",json=>{json["SchemaVersion"]=6;var data=json["Annotations"]![0]!.AsObject();data.Remove("ArrowStyle");data["DoubleEndedArrow"]=both;});var editor=new EditorView();Static(StoreType,"RestoreProject",editor,path);EqualStyle(Get(First(editor),"ArrowStyle"),Style(both?"Arrow":"None"),"Legacy 6 mapping lost medium/solid defaults");var upgraded=Path.Combine(Output,"upgraded-schema6-arrow-"+both+".snipflow");Static(StoreType,"SaveProject",editor,upgraded);using var archive=ZipFile.OpenRead(upgraded);var json=ReadManifest(archive);Check((int)json["SchemaVersion"]! ==8&&json["Annotations"]![0]!["ArrowStyle"] is not null&&!json["Annotations"]![0]!.AsObject().ContainsKey("DoubleEndedArrow"),"Legacy upgrade wrote two style truths");}
        });
        Case("schemas 1 through 6 reject the new style field including explicit null",()=> {
            foreach(var schema in new[]{1,2,3,4,5,6})foreach(var nullStyle in new[]{false,true}){var path=MutateProject(project,"invalid-old-style-"+schema+"-"+nullStyle+".snipflow",json=>{json["SchemaVersion"]=schema;if(nullStyle)json["Annotations"]![0]!["ArrowStyle"]=null;});var editor=ArrowEditor(Arrow(first,last));var before=PixelHash(editor.ExportImage());Rejected(()=>Static(StoreType,"RestoreProject",editor,path));Check(PixelHash(editor.ExportImage())==before,"Old-schema style field replaced document");}
        });
        Case("malformed style names structures and legacy conflicts reject atomically",()=> {
            var mutations=new (string,Action<JsonObject>)[]{("missing-style",d=>d.Remove("ArrowStyle")),("null-style",d=>d["ArrowStyle"]=null),("scalar-style",d=>d["ArrowStyle"]="Arrow"),("unknown-kind",d=>d["ArrowStyle"]!["StartHead"]="Chevron"),("lowercase-kind",d=>d["ArrowStyle"]!["EndHead"]="arrow"),("numeric-name",d=>d["ArrowStyle"]!["StartHead"]="1"),("null-size",d=>d["ArrowStyle"]!["StartSize"]=null),("missing-size",d=>d["ArrowStyle"]!.AsObject().Remove("EndSize")),("unknown-line",d=>d["ArrowStyle"]!["LineStyle"]="Wavy"),("unknown-property",d=>d["ArrowStyle"]!["Extra"]=true),("legacy-true-conflict",d=>d["DoubleEndedArrow"]=true),("legacy-false-conflict",d=>d["DoubleEndedArrow"]=false)};
            foreach(var (name,mutate) in mutations){var path=MutateProject(project,"invalid-style-"+name+".snipflow",json=>mutate(json["Annotations"]![0]!.AsObject()));var editor=ArrowEditor(Arrow(first,last));var before=PixelHash(editor.ExportImage());Rejected(()=>Static(StoreType,"RestoreProject",editor,path));Check(PixelHash(editor.ExportImage())==before,"Malformed style changed document: "+name);}
        });
        Case("non-arrow styles reject in snapshots and even default style DTOs",()=> {
            var item=StyledArrow(complex,new Point(60,60),new Point(220,180));Set(item,"Tool",AnnotationTool.Rectangle);var path=Path.Combine(Output,"must-not-publish-styled-rectangle.snipflow");Rejected(()=>Static(StoreType,"SaveProject",ArrowEditor(item),path));Check(!File.Exists(path),"Non-arrow style published");
            foreach(var useDefault in new[]{false,true}){var invalid=MutateProject(project,"invalid-style-rectangle-"+useDefault+".snipflow",json=>{var data=json["Annotations"]![0]!.AsObject();data["Tool"]="Rectangle";if(useDefault)data["ArrowStyle"]=new JsonObject{["StartHead"]="None",["EndHead"]="Arrow",["StartSize"]="Medium",["EndSize"]="Medium",["LineStyle"]="Solid"};});var editor=ArrowEditor(Arrow(first,last));var before=PixelHash(editor.ExportImage());Rejected(()=>Static(StoreType,"RestoreProject",editor,invalid));Check(PixelHash(editor.ExportImage())==before,"Non-arrow DTO changed document");}
        });
    }
}
