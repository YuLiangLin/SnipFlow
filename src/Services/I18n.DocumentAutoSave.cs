namespace SnipFlow.Services;

public static partial class I18n
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RegisterDocumentAutoSave() => Register(new Dictionary<string, (string English, string Simplified)>(StringComparer.Ordinal)
    {
        ["覆寫可編輯專案"] = ("Overwrite editable project", "覆盖可编辑项目"),
        ["同名的可編輯專案已存在：{0}\n儲存 PNG 時也會覆寫此專案，要繼續嗎？"] = ("An editable project with the same name already exists: {0}\nSaving the PNG will also overwrite this project. Continue?", "同名的可编辑项目已存在：{0}\n保存 PNG 时也会覆盖此项目，要继续吗？"),
        ["已儲存 PNG 與可編輯專案：{0}"] = ("Saved PNG and editable project: {0}", "已保存 PNG 与可编辑项目：{0}"),
        ["已刪除這張截圖的歷史副本與可編輯專案。"] = ("Deleted this capture's history copy and editable project.", "已删除这张截图的历史副本与可编辑项目。"),
        ["清空最近擷取？截圖歷史副本與其可編輯專案會刪除，錄影僅移除紀錄與縮圖。原影片、另外儲存的圖片／專案與目前編輯內容都會保留。"] = ("Clear recent captures? Screenshot history copies and their editable projects will be deleted; videos will only lose their history entries and thumbnails. Original videos, separately saved images/projects and current edits will be kept.", "清空最近截取？截图历史副本与其可编辑项目会删除，录制视频仅移除记录与缩略图。原视频、另外保存的图片／项目与当前编辑内容都会保留。")
    });
}
