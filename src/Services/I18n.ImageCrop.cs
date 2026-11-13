namespace SnipFlow.Services;

public static partial class I18n
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RegisterImageCrop() => Register(new Dictionary<string, (string English, string Simplified)>(StringComparer.Ordinal)
    {
        ["裁剪圖片"] = ("Crop image", "裁剪图片"),
        ["圖片縮放"] = ("Image zoom", "图片缩放"),
        ["縮小圖片"] = ("Zoom image out", "缩小图片"),
        ["放大圖片"] = ("Zoom image in", "放大图片"),
        ["裁剪框固定，縮小圖片可留白；Ctrl + 滾輪也可縮放"] = ("The crop frame stays fixed. Smaller images can leave blank space. Ctrl + wheel also zooms the image.", "裁剪框固定，缩小图片可留白；Ctrl + 滚轮也可缩放"),
        ["選取一張圖片，或雙擊圖片進入裁剪"] = ("Select one image, or double-click an image to crop it", "选取一张图片，或双击图片进入裁剪"),
        ["重設圖片"] = ("Reset image", "重置图片"),
        ["取消裁剪"] = ("Cancel crop", "取消裁剪"),
        ["完成裁剪"] = ("Apply crop", "完成裁剪"),
        ["可見範圍 {0} × {1} px"] = ("Visible area {0} × {1} px", "可见范围 {0} × {1} px"),
        ["裁剪框 {0} × {1} px"] = ("Crop frame {0} × {1} px", "裁剪框 {0} × {1} px"),
        ["拖曳黑色邊界裁剪 · 拖曳圖片調整內容 · Enter 完成 · Esc 取消"] = ("Drag the black handles to crop · Drag the image to reposition · Enter to apply · Esc to cancel", "拖动黑色边界裁剪 · 拖动图片调整内容 · Enter 完成 · Esc 取消"),
        ["拖曳黑色邊界裁剪 · 拖曳圖片調整內容 · Ctrl + 滾輪縮放圖片 · Enter 完成 · Esc 取消"] = ("Drag black handles to crop · Drag the image to reposition · Ctrl + wheel to zoom the image · Enter to apply · Esc to cancel", "拖动黑色边界裁剪 · 拖动图片调整内容 · Ctrl + 滚轮缩放图片 · Enter 完成 · Esc 取消"),
        ["Ctrl / Shift 多選 · 雙擊圖片裁剪 · 拖曳空白處框選 · 方向鍵微調"] = ("Ctrl / Shift to select multiple · Double-click an image to crop · Drag empty space to select · Arrow keys to move", "Ctrl / Shift 多选 · 双击图片裁剪 · 拖动空白处框选 · 方向键微调")
    });
}
