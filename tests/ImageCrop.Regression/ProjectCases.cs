using System.IO.Compression;
using System.Text;
using System.Text.Json;
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
    static Type StoreType=>Product.GetType("SnipFlow.Services.EditorSessionStore",true)!;
    static string? projectSeed;
    static void ProjectCases()
    {
        using var source=Quadrants();
        var checkpointRoot=Path.Combine(SettingsStore.DataRoot,"UpdateSessions");
        var existing=CheckpointInventory(checkpointRoot);
        Case("schema 8 project roundtrip stores crop and complete original image",()=> {
            var crop=new Rect(.125,.25,.5,.375);var editor=Editor(source,crop);var before=PixelHash(editor.ExportImage());
            projectSeed=Path.Combine(Output,"crop-roundtrip.snipflow");Static(StoreType,"SaveProject",editor,projectSeed);
            using(var zip=ZipFile.OpenRead(projectSeed)) {
                var json=ReadManifest(zip);Check((int)json["SchemaVersion"]! ==8,"Crop project did not use schema 8");Check(json["Annotations"]![0]!["ImageCrop"] is not null,"Serialized crop missing");
                using var pngStream=zip.GetEntry("image-0000.png")!.Open();using var pngBytes=new MemoryStream();pngStream.CopyTo(pngBytes);using var png=ImageTools.Decode(pngBytes.ToArray());
                Check(png.PixelSize==source.PixelSize && PixelHash(png)==PixelHash(source),"Project flattened/truncated the source image");
            }
            var restored=new EditorView();Static(StoreType,"RestoreProject",restored,projectSeed);EqualRect(Crop(First(restored)),crop,"Reopened crop mismatch");Check(!restored.HasEdits,"Reopened project should be marked saved");Check(PixelHash(restored.ExportImage())==before,"Project export roundtrip pixels differ");
            restored.SelectAll();Set(First(restored),"ImageCrop",null);using var reset=restored.ExportImage();var p=Pixels(reset);Check(At(p,100,80)==Colors.Red && At(p,210,80)==Colors.Lime && At(p,100,160)==Colors.Blue && At(p,210,160)==Colors.Yellow,"Hidden source pixels unavailable after reopen/reset");Save("project-reset-reveals-full-source.png",reset);
        });
        Case("schema 3 project without crop remains compatible and full image",()=> {
            RequireSeed();var path=MutateProject(projectSeed!,"legacy-schema3.snipflow",json=>{json["SchemaVersion"]=3;((JsonObject)json["Annotations"]![0]!).Remove("ImageCrop");});
            var editor=new EditorView();Static(StoreType,"RestoreProject",editor,path);Check(Crop(First(editor)) is null,"Legacy project got cropped");using var rendered=editor.ExportImage();var p=Pixels(rendered);Check(At(p,100,80)==Colors.Red && At(p,210,160)==Colors.Yellow,"Legacy image did not remain complete");
        });
        Case("schema 4 accepts legacy in-unit crop",()=> {
            RequireSeed();var path=MutateProject(projectSeed!,"legacy-schema4.snipflow",json=>json["SchemaVersion"]=4);var editor=new EditorView();Static(StoreType,"RestoreProject",editor,path);EqualRect(Crop(First(editor)),new Rect(.125,.25,.5,.375),"Schema 4 legacy crop changed");
        });
        Case("schema 4 rejects new blank-margin viewport atomically",()=> {
            RequireSeed();var path=MutateProject(projectSeed!,"invalid-schema4-margins.snipflow",json=>{json["SchemaVersion"]=4;json["Annotations"]![0]!["ImageCrop"]!["X"]=-.5;json["Annotations"]![0]!["ImageCrop"]!["Width"]=2.0;});var editor=Editor(source,null);var before=PixelHash(editor.ExportImage());Rejected(()=>Static(StoreType,"RestoreProject",editor,path));Check(PixelHash(editor.ExportImage())==before,"Invalid old-schema viewport changed document");
        });
        Case("schema 5 blank-margin viewport roundtrips with full source payload",()=> {
            var viewport=new Rect(-.5,-.5,2,2);var editor=Editor(source,viewport);var before=PixelHash(editor.ExportImage());var current=Path.Combine(Output,"viewport-margins.snipflow");Static(StoreType,"SaveProject",editor,current);var path=MutateProject(current,"legacy-schema5-margins.snipflow",json=>json["SchemaVersion"]=5);var restored=new EditorView();Static(StoreType,"RestoreProject",restored,path);EqualRect(Crop(First(restored)),viewport,"Schema 5 blank-margin project viewport lost");Check(PixelHash(restored.ExportImage())==before&&PixelHash((Bitmap)Get(First(restored),"Image")!)==PixelHash(source),"Schema 5 blank-margin project lost rendered/source pixels");
        });
        foreach(int schema in new[]{2,3}) Case("old schema "+schema+" rejects crop instead of silently flattening",()=> {
            RequireSeed();var path=MutateProject(projectSeed!,"invalid-schema"+schema+"-crop.snipflow",json=>json["SchemaVersion"]=schema);
            var editor=Editor(source,null);var hash=PixelHash(editor.ExportImage());Rejected(()=>Static(StoreType,"RestoreProject",editor,path));Check(PixelHash(editor.ExportImage())==hash,"Rejected archive changed existing document");
        });
        foreach(var invalid in new[]{("unsafe-negative-x",new Rect(-1000001,0,.5,.5)),("zero-width",new Rect(0,0,0,.5)),("unsafe-overflow",new Rect(1000001,0,1,.5)),("nan",new Rect(double.NaN,0,.5,.5)),("infinity",new Rect(0,0,.5,double.PositiveInfinity))})
        {
            Case("save rejects invalid crop "+invalid.Item1+" before publication",()=> {
                var editor=Editor(source,invalid.Item2);var path=Path.Combine(Output,"must-not-publish-"+invalid.Item1+".snipflow");Rejected(()=>Static(StoreType,"SaveProject",editor,path));Check(!File.Exists(path),"Invalid project was published");
            });
        }
        Case("save rejects crop attached to a non-image annotation",()=> {
            var editor=Editor(source,new Rect(0,0,.5,.5));Set(First(editor),"Tool",AnnotationTool.Rectangle);Set(First(editor),"Image",null);
            var path=Path.Combine(Output,"must-not-publish-non-image.snipflow");Rejected(()=>Static(StoreType,"SaveProject",editor,path));Check(!File.Exists(path),"Non-image crop published");
        });
        foreach(var invalid in new[]{("unsafe-negative-x","X",-1000001.0),("empty-width","Width",0.0),("unsafe-width","Width",2000002.0)})
        {
            Case("restore rejects tampered crop "+invalid.Item1+" atomically",()=> {
                RequireSeed();var path=MutateProject(projectSeed!,"tampered-"+invalid.Item1+".snipflow",json=>json["Annotations"]![0]!["ImageCrop"]![invalid.Item2]=invalid.Item3);
                var editor=Editor(source,null);var hash=PixelHash(editor.ExportImage());Rejected(()=>Static(StoreType,"RestoreProject",editor,path));Check(PixelHash(editor.ExportImage())==hash,"Invalid archive replaced open document");
            });
        }
        Case("project operations preserve pre-existing checkpoint directory inventory",()=> {
            var after=CheckpointInventory(checkpointRoot);Check(existing.SequenceEqual(after),"Project save/restore left checkpoints behind or changed existing entries");
        });
    }
    static string[] CheckpointInventory(string root)=>Directory.Exists(root)?Directory.GetFileSystemEntries(root).Select(path=>Path.GetFileName(path)!).Order(StringComparer.Ordinal).ToArray():Array.Empty<string>();
    static void RequireSeed()=>Check(projectSeed is not null && File.Exists(projectSeed),"Roundtrip project seed unavailable");
    static JsonObject ReadManifest(ZipArchive archive)
    {
        using var stream=archive.GetEntry("manifest.json")!.Open();return JsonNode.Parse(stream)!.AsObject();
    }
    static string MutateProject(string seed,string name,Action<JsonObject> mutation)
    {
        var path=Path.Combine(Output,name);using var source=ZipFile.OpenRead(seed);var json=ReadManifest(source);mutation(json);
        using var file=File.Create(path);using var destination=new ZipArchive(file,ZipArchiveMode.Create);
        foreach(var entry in source.Entries) {
            var output=destination.CreateEntry(entry.FullName,CompressionLevel.Optimal);using var stream=output.Open();
            if(entry.FullName=="manifest.json"){var bytes=Encoding.UTF8.GetBytes(json.ToJsonString());stream.Write(bytes);}
            else {using var input=entry.Open();input.CopyTo(stream);}
        }
        return path;
    }
}
