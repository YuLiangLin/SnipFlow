using System.IO.Compression;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SnipFlow.Editor;
using SnipFlow.Imaging;
using SnipFlow.Services;

namespace SnipFlow.ImageCrop.Regression;

internal static partial class Program
{
    static object Arrow(Point start,Point end,double width=4,bool doubleEnded=false)
    {
        var item=Activator.CreateInstance(ItemType,true)!;
        Set(item,"Tool",AnnotationTool.Arrow);Set(item,"Start",start);Set(item,"End",end);Set(item,"Width",width);Set(item,"Color",Colors.Red);Set(item,"DoubleEndedArrow",doubleEnded);
        return item;
    }
    static bool DoubleArrow(object item)=>(bool)Get(item,"DoubleEndedArrow")!;
    static EditorView ArrowEditor(params object[] annotations)
    {
        var editor=new EditorView();var board=ImageTools.Solid(320,240,Colors.White);var items=Array.CreateInstance(ItemType,annotations.Length);
        for(int n=0;n<annotations.Length;n++)items.SetValue(annotations[n],n);
        Call(editor,"RestoreSession",board,items,false,false);editor.SelectAll();return editor;
    }
    static int OpaqueCount(PixelBuffer image)=>Enumerable.Range(0,image.Width*image.Height).Count(i=>image.Pixels[i*4+3]>16);
    static int HeadPixels(PixelBuffer image,Point tip,Vector inward,double radius,double shaftWidth)
    {
        var side=new Vector(-inward.Y,inward.X);var count=0;
        for(int y=Math.Max(0,(int)Math.Floor(tip.Y-radius));y<Math.Min(image.Height,(int)Math.Ceiling(tip.Y+radius));y++)
        for(int x=Math.Max(0,(int)Math.Floor(tip.X-radius));x<Math.Min(image.Width,(int)Math.Ceiling(tip.X+radius));x++) {
            var delta=(Vector)(new Point(x+.5,y+.5)-tip);var along=Vector.Dot(delta,inward);var across=Math.Abs(Vector.Dot(delta,side));
            if(along>=0&&along<=radius&&across>shaftWidth/2+1&&image.Pixels[(y*image.Width+x)*4+3]>16)count++;
        }
        return count;
    }
    static void ArrowCases()
    {
        var checkpointRoot=Path.Combine(SettingsStore.DataRoot,"UpdateSessions");var previousCheckpoints=CheckpointInventory(checkpointRoot);
        var start=new Point(60,120);var end=new Point(260,120);
        Case("double arrow clone copy and equality preserve per-item style",()=> {
            var original=Arrow(start,end,4,true);var copy=Call(original,"Clone")!;
            Check(DoubleArrow(copy)&&(bool)Call(original,"ContentEquals",copy)!,"Clone lost arrow style");Set(copy,"DoubleEndedArrow",false);Check(!(bool)Call(original,"ContentEquals",copy)!,"Style-only change ignored by equality");Call(original,"CopyFrom",copy);Check(!DoubleArrow(original)&&(bool)Call(original,"ContentEquals",copy)!,"CopyFrom lost single-ended style");Set(copy,"DoubleEndedArrow",true);Call(original,"CopyFrom",copy);Check(DoubleArrow(original),"CopyFrom lost double-ended style");
        });
        Case("single arrow remains single and double arrow adds only the start head",()=> {
            var single=Arrow(start,end);var explicitSingle=Arrow(start,end,4,false);var both=Arrow(start,end,4,true);
            using var original=RenderItem(single,320,240);using var unchanged=RenderItem(explicitSingle,320,240);using var doubleImage=RenderItem(both,320,240);var oldPixels=Pixels(original);var newPixels=Pixels(doubleImage);
            Check(PixelHash(original)==PixelHash(unchanged),"Default arrow style changed");Check(HeadPixels(oldPixels,start,new Vector(1,0),24,4)==0,"Single arrow gained a start head");Check(HeadPixels(newPixels,start,new Vector(1,0),24,4)>0,"Double arrow has no start head");Check(HeadPixels(oldPixels,end,new Vector(-1,0),24,4)>0&&HeadPixels(newPixels,end,new Vector(-1,0),24,4)>0,"End head missing");
            for(int y=96;y<144;y++)for(int x=236;x<284;x++)Check(At(oldPixels,x,y)==At(newPixels,x,y),"Existing end head rendering changed on a long arrow");
            Save("arrow-single.png",original);Save("arrow-double.png",doubleImage);
        });
        Case("double arrow heads render and hit-test in both directions at supported widths",()=> {
            var directions=new[]{(new Point(60,120),new Point(260,120)),(new Point(260,120),new Point(60,120)),(new Point(160,30),new Point(160,210)),(new Point(160,210),new Point(160,30)),(new Point(50,40),new Point(270,200)),(new Point(270,200),new Point(50,40)),(new Point(50,200),new Point(270,40)),(new Point(270,40),new Point(50,200))};
            foreach(var width in new[]{1.0,4.0,12.0,32.0})foreach(var (first,last) in directions) {
                var item=Arrow(first,last,width,true);var single=Arrow(first,last,width,false);var delta=(Vector)(last-first);var direction=delta.Normalize();var perpendicular=new Vector(-direction.Y,direction.X);var head=Math.Min(delta.Length*.4,Math.Max(12,width*4));
                using var image=RenderItem(item,320,240);var pixels=Pixels(image);Check(HeadPixels(pixels,first,direction,head+2,width)>0&&HeadPixels(pixels,last,-direction,head+2,width)>0,"Head missing at width/direction "+width+" "+first+" -> "+last);
                var firstHead=first+direction*head*.85+perpendicular*head*.33;var lastHead=last-direction*head*.85+perpendicular*head*.33;
                Check((bool)Call(item,"HitTest",firstHead,.2)!&&(bool)Call(item,"HitTest",lastHead,.2)!,"New head not selectable at "+width+" "+first+" -> "+last);
                // A thick single end-head can legitimately reach this probe on a short shaft.
                // Keep the negative control to fixtures whose end-head hit region is separate.
                if(width<=12)Check(!(bool)Call(single,"HitTest",firstHead,.2)!,"Single arrow incorrectly has a start-head hit target");
            }
        });
        Case("short and zero-length double arrows render safely at different widths",()=> {
            foreach(var length in new[]{0.0,.1,.5,2.0,10.0})foreach(var width in new[]{1.0,4.0,32.0}) {
                var item=Arrow(new Point(100,100),new Point(100+length,100),width,true);using var image=RenderItem(item,320,240);var count=OpaqueCount(Pixels(image));Check(length<.5?count==0:count>0,"Unexpected short-arrow rendering at length/width "+length+"/"+width);
            }
        });
        Case("short arrow hit-test rejects the old uncapped phantom head region",()=> {
            foreach(var doubleEnded in new[]{false,true}) {
                var item=Arrow(new Point(100,100),new Point(104,100),1,doubleEnded);
                Check(!(bool)Call(item,"HitTest",new Point(92,106),.2)!,"Short arrow selected a phantom head outside its capped geometry");
            }
        });
        Case("new-drawing arrow factory inherits default without contaminating other tools",()=> {
            var editor=ArrowEditor();Check(!(bool)Get(editor,"DoubleEndedArrow")!,"New editor should default to single arrow");Set(editor,"DoubleEndedArrow",true);Check(!editor.HasEdits&&UndoDepth(editor)==0,"Changing default without selection dirtied document");
            var item=Call(editor,"CreateShapeAnnotation",AnnotationTool.Arrow,start)!;Set(item,"End",end);Check(DoubleArrow(item),"Real drawing factory did not inherit double-ended default");var rectangle=Call(editor,"CreateShapeAnnotation",AnnotationTool.Rectangle,start)!;Check(!DoubleArrow(rectangle),"Arrow default leaked to rectangle");
            typeof(EditorView).GetField("_pending",Any)!.SetValue(editor,item);Call(editor,"CompleteGesture");Check(Items(editor).Count==1&&DoubleArrow(First(editor)),"Committing new arrow lost style");editor.Undo();Check(Items(editor).Count==0&&(bool)Get(editor,"DoubleEndedArrow")!,"Undo should restore document without reverting tool default");editor.Redo();Check(DoubleArrow(First(editor)),"New arrow redo lost style");
        });
        Case("selected arrow style changes once and supports undo redo and no-op",()=> {
            var editor=ArrowEditor(Arrow(start,end));var original=PixelHash(editor.ExportImage());Check(Get(editor,"SelectedDoubleEndedArrow") is false,"Single-arrow selection style unavailable");Set(editor,"DoubleEndedArrow",true);Check(DoubleArrow(First(editor))&&Get(editor,"SelectedDoubleEndedArrow") is true&&UndoDepth(editor)==1,"Style setter did not mutate selected arrow once");var styled=PixelHash(editor.ExportImage());Check(styled!=original,"Double-ended style did not change rendered pixels");
            var revision=editor.Revision;Set(editor,"DoubleEndedArrow",true);Check(UndoDepth(editor)==1&&editor.Revision==revision,"Setting existing arrow style added history");editor.Undo();Check(!DoubleArrow(First(editor))&&PixelHash(editor.ExportImage())==original&&(bool)Get(editor,"DoubleEndedArrow")!,"Arrow style undo/default semantics");editor.Redo();Check(DoubleArrow(First(editor))&&PixelHash(editor.ExportImage())==styled,"Arrow style redo pixels mismatch");
        });
        Case("multiple selection changes arrow styles only in one undo step",()=> {
            var rectangle=Activator.CreateInstance(ItemType,true)!;Set(rectangle,"Tool",AnnotationTool.Rectangle);Set(rectangle,"Start",new Point(230,40));Set(rectangle,"End",new Point(290,190));Set(rectangle,"Color",Colors.Blue);Set(rectangle,"Width",3.0);
            var editor=ArrowEditor(Arrow(new Point(40,60),new Point(190,60),4,true),Arrow(new Point(40,160),new Point(190,160)),rectangle);var previous=Call(Items(editor)[2]!,"Clone")!;Check(Get(editor,"SelectedDoubleEndedArrow") is null,"Non-arrow selection should have no arrow style");Set(editor,"DoubleEndedArrow",true);
            Check(DoubleArrow(Items(editor)[0]!)&&DoubleArrow(Items(editor)[1]!)&&!DoubleArrow(Items(editor)[2]!)&&UndoDepth(editor)==1,"Multiple-arrow mutation was not atomic or changed rectangle");Check((bool)Call(Items(editor)[2]!,"ContentEquals",previous)!,"Arrow property changed non-arrow content");editor.Undo();Check(DoubleArrow(Items(editor)[0]!)&&!DoubleArrow(Items(editor)[1]!)&&!DoubleArrow(Items(editor)[2]!),"Multi-arrow undo lost original mixed state");editor.Redo();Check(DoubleArrow(Items(editor)[0]!)&&DoubleArrow(Items(editor)[1]!),"Multi-arrow redo failed");
        });
        Case("grouped arrow property edit is undoable and net no-op creates no history",()=> {
            var editor=ArrowEditor(Arrow(start,end));editor.BeginPropertyEdit();Set(editor,"DoubleEndedArrow",true);Check(UndoDepth(editor)==0&&editor.HasEdits,"Property draft should stay grouped");editor.EndPropertyEdit();Check(UndoDepth(editor)==1,"Property group added incorrect history");editor.Undo();Check(!DoubleArrow(First(editor)),"Grouped arrow style undo failed");
            var noOp=ArrowEditor(Arrow(start,end));noOp.BeginPropertyEdit();Set(noOp,"DoubleEndedArrow",true);Set(noOp,"DoubleEndedArrow",false);noOp.EndPropertyEdit();Check(UndoDepth(noOp)==0&&!noOp.HasEdits,"Net no-op property edit added undo entry");
        });
        Case("duplicate and session snapshot retain per-arrow direction style",()=> {
            var editor=ArrowEditor(Arrow(start,end,4,true));var snapshot=Call(editor,"CaptureSession")!;var snapshots=(System.Collections.IList)Get(snapshot,"Annotations")!;editor.DuplicateSelection();Check(Items(editor).Count==2&&Items(editor).Cast<object>().All(DoubleArrow),"Duplicated arrow lost style");editor.Undo();Check(Items(editor).Count==1&&DoubleArrow(First(editor)),"Duplicate undo lost original style");editor.Redo();Check(Items(editor).Count==2&&Items(editor).Cast<object>().All(DoubleArrow),"Duplicate redo lost style");Set(editor,"DoubleEndedArrow",false);Check(DoubleArrow(snapshots[0]!),"Editing document mutated captured session clone");
        });
        Case("whole-canvas rotation preserves double-arrow style and undo",()=> {
            var editor=ArrowEditor(Arrow(start,end,4,true));var before=PixelHash(editor.ExportImage());var command=Enum.Parse(Product.GetType("SnipFlow.Editor.ImageTransformCommand",true)!,"RotateRight");Call(editor,"ApplyImageTransform",command,true);Check(DoubleArrow(First(editor)),"Rotated arrow lost style");var after=PixelHash(editor.ExportImage());editor.Undo();Check(DoubleArrow(First(editor))&&PixelHash(editor.ExportImage())==before,"Rotated arrow undo failed");editor.Redo();Check(DoubleArrow(First(editor))&&PixelHash(editor.ExportImage())==after,"Rotated arrow redo failed");
        });
        var doubleProject=Path.Combine(Output,"double-arrow.snipflow");var singleProject=Path.Combine(Output,"single-arrow.snipflow");
        Case("schema 6 double arrow save and reopen preserve style and exact pixels",()=> {
            var editor=ArrowEditor(Arrow(start,end,4,true));var expected=PixelHash(editor.ExportImage());Static(StoreType,"SaveProject",editor,doubleProject);using(var archive=ZipFile.OpenRead(doubleProject)){var json=ReadManifest(archive);Check((int)json["SchemaVersion"]! ==6&&(bool)json["Annotations"]![0]!["DoubleEndedArrow"]!,"Double-arrow manifest lost schema/style");}
            var reopened=new EditorView();Static(StoreType,"RestoreProject",reopened,doubleProject);Check(DoubleArrow(First(reopened))&&!reopened.HasEdits&&PixelHash(reopened.ExportImage())==expected,"Reopened double arrow lost style/output");
        });
        Case("single arrow false field is omitted from schema 6 manifest",()=> {
            var editor=ArrowEditor(Arrow(start,end));Static(StoreType,"SaveProject",editor,singleProject);using var archive=ZipFile.OpenRead(singleProject);var json=ReadManifest(archive);Check((int)json["SchemaVersion"]! ==6&&!json["Annotations"]![0]!.AsObject().ContainsKey("DoubleEndedArrow"),"Default false arrow field should be omitted");
        });
        Case("schemas 1 through 5 reopen legacy arrows as single-ended",()=> {
            Check(File.Exists(singleProject),"Single-arrow fixture missing");var expected=PixelHash(ArrowEditor(Arrow(start,end)).ExportImage());foreach(var schema in new[]{1,2,3,4,5}) {
                var path=MutateProject(singleProject,"legacy-single-arrow-"+schema+".snipflow",json=>json["SchemaVersion"]=schema);var editor=new EditorView();Static(StoreType,"RestoreProject",editor,path);Check(!DoubleArrow(First(editor))&&PixelHash(editor.ExportImage())==expected,"Legacy schema "+schema+" changed single-arrow rendering");
            }
        });
        Case("schemas 1 through 5 reject double-arrow true without replacing open document",()=> {
            Check(File.Exists(doubleProject),"Double-arrow fixture missing");foreach(var schema in new[]{1,2,3,4,5}) {
                var path=MutateProject(doubleProject,"invalid-legacy-double-arrow-"+schema+".snipflow",json=>json["SchemaVersion"]=schema);var editor=ArrowEditor(Arrow(start,end));var before=PixelHash(editor.ExportImage());Rejected(()=>Static(StoreType,"RestoreProject",editor,path));Check(!DoubleArrow(First(editor))&&PixelHash(editor.ExportImage())==before,"Rejected schema "+schema+" replaced document");
            }
        });
        Case("save rejects double-ended flag on non-arrow annotation before publication",()=> {
            var item=Arrow(new Point(40,40),new Point(160,160),4,true);Set(item,"Tool",AnnotationTool.Rectangle);var editor=ArrowEditor(item);var path=Path.Combine(Output,"must-not-publish-double-rectangle.snipflow");Rejected(()=>Static(StoreType,"SaveProject",editor,path));Check(!File.Exists(path),"Non-arrow style was published");
        });
        Case("schema 6 rejects double-ended flag on non-arrow data atomically",()=> {
            Check(File.Exists(doubleProject),"Double-arrow fixture missing");var path=MutateProject(doubleProject,"invalid-double-rectangle.snipflow",json=>json["Annotations"]![0]!["Tool"]="Rectangle");var editor=ArrowEditor(Arrow(start,end));var before=PixelHash(editor.ExportImage());Rejected(()=>Static(StoreType,"RestoreProject",editor,path));Check(PixelHash(editor.ExportImage())==before,"Invalid arrow style changed document");
        });
        Case("malformed double-arrow JSON flag values are rejected atomically",()=> {
            Check(File.Exists(doubleProject),"Double-arrow fixture missing");var values=new JsonNode?[]{JsonValue.Create("true"),JsonValue.Create(1),null};for(int n=0;n<values.Length;n++) {
                var value=values[n];var path=MutateProject(doubleProject,"invalid-arrow-flag-"+n+".snipflow",json=>json["Annotations"]![0]!["DoubleEndedArrow"]=value?.DeepClone());var editor=ArrowEditor(Arrow(start,end));var before=PixelHash(editor.ExportImage());Rejected(()=>Static(StoreType,"RestoreProject",editor,path));Check(PixelHash(editor.ExportImage())==before,"Malformed arrow flag changed document");
            }
        });
        Case("arrow project operations preserve pre-existing checkpoint inventory",()=>Check(previousCheckpoints.SequenceEqual(CheckpointInventory(checkpointRoot)),"Arrow persistence left temporary checkpoints behind"));
    }
}
