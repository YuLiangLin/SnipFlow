using System.Globalization;

namespace SnipFlow.Services;

/// <summary>Application text only. OCR language selection and the Windows display language are independent.</summary>
public static class I18n
{
    private static string _languageTag = "zh-TW";

    public static string LanguageTag => Volatile.Read(ref _languageTag);
    public static event EventHandler? Changed;

    /// <summary>Uses zh-TW, en, or zh-CN. Unknown language tags fall back to Traditional Chinese.</summary>
    public static void SetLanguage(string? languageTag)
    {
        var requested = languageTag?.Trim() ?? "";
        var normalized = requested.Equals("en", StringComparison.OrdinalIgnoreCase)
            || requested.StartsWith("en-", StringComparison.OrdinalIgnoreCase) ? "en"
            : requested.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
            || requested.Equals("zh-SG", StringComparison.OrdinalIgnoreCase)
            || requested.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase) ? "zh-CN"
            : "zh-TW";
        if (Interlocked.Exchange(ref _languageTag, normalized) != normalized)
            Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Looks up a Traditional Chinese source string, preserving the source for missing entries.</summary>
    public static string T(string zhTwText)
    {
        return Translate(zhTwText, LanguageTag);
    }

    private static string Translate(string zhTwText, string languageTag)
    {
        ArgumentNullException.ThrowIfNull(zhTwText);
        if (languageTag == "zh-TW" || !Translations.TryGetValue(zhTwText, out var translation))
            return zhTwText;
        return languageTag == "en" ? translation.English : translation.Simplified;
    }

    /// <summary>Formats translated templates. Pass a stable source key, such as "已開啟 {0}".</summary>
    public static string F(string zhTwFormat, params object?[] values)
    {
        var languageTag = LanguageTag;
        return string.Format(CultureInfo.GetCultureInfo(languageTag), Translate(zhTwFormat, languageTag), values);
    }

    private static readonly Dictionary<string, (string English, string Simplified)> Translations = new(StringComparer.Ordinal)
    {
        // Shell, navigation, annotation tools, and common actions.
        ["SnipFlow · 快剪"] = ("SnipFlow · Capture", "SnipFlow · 快剪"),
        ["SnipFlow · 快剪 *"] = ("SnipFlow · Capture *", "SnipFlow · 快剪 *"),
        ["快剪"] = ("Capture", "快剪"),
        ["新截圖"] = ("New capture", "新截图"),
        ["截圖"] = ("Capture", "截图"),
        ["框選截圖"] = ("Capture a region", "框选截图"),
        ["開始框選截圖"] = ("Capture a region", "开始框选截图"),
        ["長截圖"] = ("Scrolling", "长截图"),
        ["長頁捲動截圖"] = ("Scrolling capture", "长页滚动截图"),
        ["開啟"] = ("Open", "打开"),
        ["開啟圖片"] = ("Open image", "打开图片"),
        ["開啟 SnipFlow"] = ("Open SnipFlow", "打开 SnipFlow"),
        ["最小化"] = ("Minimize", "最小化"),
        ["最大化／還原"] = ("Maximize / restore", "最大化／还原"),
        ["隱藏至系統匣"] = ("Hide to tray", "隐藏到系统托盘"),
        ["設定"] = ("Settings", "设置"),
        ["結束"] = ("Exit", "退出"),
        ["結束程式"] = ("Exit SnipFlow", "退出程序"),
        ["複製"] = ("Copy", "复制"),
        ["儲存"] = ("Save", "保存"),
        ["取消"] = ("Cancel", "取消"),
        ["關閉"] = ("Close", "关闭"),
        ["完成"] = ("Done", "完成"),
        ["已複製"] = ("Copied", "已复制"),
        ["選取／移動"] = ("Select / move", "选择／移动"),
        ["選取／移動 (V)"] = ("Select / move (V)", "选择／移动 (V)"),
        ["箭頭"] = ("Arrow", "箭头"),
        ["箭頭 (A)"] = ("Arrow (A)", "箭头 (A)"),
        ["矩形"] = ("Rectangle", "矩形"),
        ["矩形 (R)"] = ("Rectangle (R)", "矩形 (R)"),
        ["橢圓"] = ("Ellipse", "椭圆"),
        ["橢圓 (E)"] = ("Ellipse (E)", "椭圆 (E)"),
        ["畫筆"] = ("Pen", "画笔"),
        ["畫筆 (P)"] = ("Pen (P)", "画笔 (P)"),
        ["螢光筆"] = ("Highlighter", "荧光笔"),
        ["螢光筆 (H)"] = ("Highlighter (H)", "荧光笔 (H)"),
        ["文字"] = ("Text", "文字"),
        ["文字 (T)"] = ("Text (T)", "文字 (T)"),
        ["馬賽克"] = ("Pixelate", "马赛克"),
        ["馬賽克 (M)"] = ("Pixelate (M)", "马赛克 (M)"),
        ["復原 · Ctrl + Z"] = ("Undo · Ctrl + Z", "撤销 · Ctrl + Z"),
        ["重做 · Ctrl + Y"] = ("Redo · Ctrl + Y", "重做 · Ctrl + Y"),
        ["黑色"] = ("Black", "黑色"),
        ["青綠"] = ("Teal", "青绿"),
        ["紅色"] = ("Red", "红色"),
        ["黃色"] = ("Yellow", "黄色"),
        ["白色"] = ("White", "白色"),
        ["粗細"] = ("Stroke", "粗细"),
        ["字級"] = ("Size", "字号"),
        ["文字大小（12–240 px）"] = ("Text size (12–240 px)", "文字大小（12–240 px）"),
        ["最近截圖"] = ("Recent captures", "最近截图"),
        ["尚無截圖"] = ("No captures yet", "暂无截图"),
        ["就緒"] = ("Ready", "就绪"),
        ["截下重點，說得更清楚。"] = ("Capture the point. Make it clear.", "截下重点，说得更清楚。"),
        ["框選畫面，接著標註、複製或擷取文字。"] = ("Capture a region, then annotate, copy, or extract text.", "框选画面，然后标注、复制或提取文字。"),

        // Shell status messages and file dialogs. Dynamic entries use composite-format placeholders.
        ["{0} 已被其他程式使用；可透過按鈕或系統匣截圖。"] = ("{0} is in use by another app. Use the capture button or tray menu.", "{0} 已被其他程序使用；可通过按钮或系统托盘截图。"),
        ["截圖完成，已複製到剪貼簿。"] = ("Captured and copied to the clipboard.", "截图完成，已复制到剪贴板。"),
        ["截圖完成，可以開始標註。"] = ("Captured. Ready to annotate.", "截图完成，可以开始标注。"),
        ["截圖未完成"] = ("Capture failed", "截图未完成"),
        ["已開啟 {0}"] = ("Opened {0}", "已打开 {0}"),
        ["圖片無法開啟"] = ("Could not open the image", "图片无法打开"),
        ["歷史讀取失敗"] = ("Could not load capture history", "历史读取失败"),
        ["截圖已完成，但歷史儲存失敗"] = ("Captured, but could not save to history", "截图已完成，但历史保存失败"),
        ["已複製圖片"] = ("Image copied", "已复制图片"),
        ["複製失敗"] = ("Copy failed", "复制失败"),
        ["PNG 圖片 (*.png)|*.png"] = ("PNG image (*.png)|*.png", "PNG 图片 (*.png)|*.png"),
        ["已儲存 {0}"] = ("Saved {0}", "已保存 {0}"),
        ["儲存失敗"] = ("Save failed", "保存失败"),
        ["目前的標註尚未儲存，要先存成 PNG 嗎？"] = ("Your annotations have not been saved. Save a PNG first?", "当前标注尚未保存，要先保存为 PNG 吗？"),
        ["保留標註"] = ("Save annotations", "保留标注"),
        ["圖片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有檔案|*.*"] = ("Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*", "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*"),
        ["貼上失敗"] = ("Paste failed", "粘贴失败"),
        ["這個操作未完成，詳細資訊已寫入本機紀錄。\n"] = ("This action could not be completed. Details were saved to the local log.\n", "此操作未完成，详细信息已写入本机日志。\n"),
        ["這個操作未完成，詳細資訊已寫入本機紀錄。"] = ("This action could not be completed. Details were saved to the local log.", "此操作未完成，详细信息已写入本机日志。"),

        // Settings, startup, and update controls.
        ["SnipFlow · 設定"] = ("SnipFlow · Settings", "SnipFlow · 设置"),
        ["儲存設定"] = ("Save settings", "保存设置"),
        ["語言"] = ("Language", "语言"),
        ["介面語言"] = ("App language", "界面语言"),
        ["切換後立即套用並儲存。"] = ("Language changes apply and save immediately.", "切换后立即应用并保存。"),
        ["截圖與歷史"] = ("Captures and history", "截图与历史"),
        ["截圖完成後自動複製"] = ("Copy automatically after capture", "截图完成后自动复制"),
        ["保留最近截圖（最多 80 張）"] = ("Keep recent captures (up to 80)", "保留最近截图（最多 80 张）"),
        ["登入 Windows 後常駐系統匣"] = ("Start in the tray when I sign in to Windows", "登录 Windows 后驻留系统托盘"),
        ["框選快捷鍵"] = ("Capture shortcut", "框选快捷键"),
        ["全域截圖快捷鍵"] = ("Global capture shortcut", "全局截图快捷键"),
        ["預設"] = ("Reset", "默认"),
        ["點選欄位後按下組合鍵。字母／數字須搭配 Ctrl、Alt、Shift 中至少兩個鍵；也可使用 F1–F11。"] = ("Click the field and press a shortcut. Letters and digits need at least two of Ctrl, Alt and Shift. F1–F11 also work.", "点击字段后按下组合键。字母／数字需搭配 Ctrl、Alt、Shift 中至少两个键；也可使用 F1–F11。"),
        ["焦點在其他程式、最小化或隱藏至系統匣時也能截圖。"] = ("Works while another app has focus, when minimized, or while hidden in the tray.", "焦点在其他程序、最小化或隐藏至系统托盘时也能截图。"),
        ["目前啟用：{0}"] = ("Active shortcut: {0}", "当前启用：{0}"),
        ["全域快捷鍵未啟用"] = ("Global shortcut inactive", "全局快捷键未启用"),
        ["全域快捷鍵無法啟用。"] = ("Could not enable the global shortcut.", "无法启用全局快捷键。"),
        ["全域截圖快捷鍵：{0}"] = ("Global capture shortcut: {0}", "全局截图快捷键：{0}"),
        ["{0} 無法啟用（Windows 錯誤 {1}）。"] = ("Could not enable {0} (Windows error {1}).", "无法启用 {0}（Windows 错误 {1}）。"),
        ["請使用至少兩個修飾鍵加字母／數字，或 F1–F11。"] = ("Use at least two modifiers with a letter or digit, or F1–F11.", "请使用至少两个修饰键加字母／数字，或 F1–F11。"),
        ["版本與更新"] = ("Version and updates", "版本与更新"),
        ["自動檢查新版本（啟動時與每 4 小時）"] = ("Check for updates at startup and every 4 hours", "自动检查新版本（启动时及每 4 小时）"),
        ["自動下載更新，下次啟動時套用"] = ("Download updates automatically and apply at next launch", "自动下载更新，下次启动时应用"),
        ["檢查更新"] = ("Check for updates", "检查更新"),
        ["下載新版本"] = ("Download update", "下载新版本"),
        ["重新啟動更新"] = ("Restart and update", "重启并更新"),
        ["查看 GitHub 專案與版本紀錄 ↗"] = ("View the project and releases on GitHub ↗", "查看 GitHub 项目与版本记录 ↗"),
        ["更新未完成"] = ("Update failed", "更新未完成"),
        ["這組快捷鍵已被其他程式使用，請換一組。"] = ("This shortcut is in use by another app. Choose a different one.", "此快捷键已被其他程序使用，请换一组。"),
        ["快捷鍵無法設定"] = ("Could not set the shortcut", "快捷键无法设置"),
        ["無法取得程式路徑。"] = ("Could not locate the application executable.", "无法获取程序路径。"),
        ["設定未儲存"] = ("Settings were not saved", "设置未保存"),
        ["語系未儲存"] = ("Language preference was not saved", "语言设置未保存"),
        ["尚未檢查更新"] = ("Updates have not been checked", "尚未检查更新"),
        ["更新來源尚未設定"] = ("Update source is not configured", "更新来源尚未设置"),
        ["可攜版 · 請安裝 Setup 以啟用自動更新"] = ("Portable version · Run Setup to enable automatic updates", "便携版 · 请运行 Setup 以启用自动更新"),
        ["更新已下載，可重新啟動套用"] = ("Update downloaded. Restart to apply it.", "更新已下载，可重启应用"),
        ["讀取更新設定失敗"] = ("Could not read update settings", "读取更新设置失败"),
        ["正在檢查 GitHub Releases…"] = ("Checking GitHub Releases…", "正在检查 GitHub Releases…"),
        ["目前已是最新版本"] = ("You are up to date", "当前已是最新版本"),
        ["發現新版本 {0}"] = ("Version {0} is available", "发现新版本 {0}"),
        ["更新檢查失敗 · 請稍後重試"] = ("Could not check for updates · Try again later", "更新检查失败 · 请稍后重试"),
        ["更新下載失敗 · 目前版本仍可使用"] = ("Download failed · You can keep using this version", "更新下载失败 · 当前版本仍可使用"),
        ["正在下載 {0}…"] = ("Downloading {0}…", "正在下载 {0}…"),
        ["正在下載更新 · {0}%"] = ("Downloading update · {0}%", "正在下载更新 · {0}%"),
        ["{0} 已下載 · 重新啟動以更新"] = ("{0} downloaded · Restart to update", "{0} 已下载 · 重启以更新"),
        ["新版本已下載。完成編輯後即可更新。"] = ("An update is ready. Finish editing, then restart to install it.", "新版本已下载。完成编辑后即可更新。"),
        ["更新尚未套用"] = ("Update has not been applied", "更新尚未应用"),

        // OCR results and local Windows OCR diagnostics.
        ["SnipFlow · 辨識文字"] = ("SnipFlow · Text recognition", "SnipFlow · 文字识别"),
        ["辨識文字"] = ("Recognize text", "识别文字"),
        ["辨識文字  OCR"] = ("Recognize text  OCR", "识别文字  OCR"),
        ["可直接修正辨識結果。"] = ("You can edit the recognized text before copying it.", "可直接修改识别结果。"),
        ["複製文字"] = ("Copy text", "复制文字"),
        ["正在本機辨識文字…"] = ("Recognizing text locally…", "正在本机识别文字…"),
        ["沒有辨識出文字。"] = ("No text was recognized.", "未识别出文字。"),
        ["文字辨識完成。"] = ("Text recognition complete.", "文字识别完成。"),
        ["文字辨識未完成"] = ("Text recognition failed", "文字识别未完成"),
        ["Windows OCR 無法取得可處理的影像大小。"] = ("Windows OCR could not determine the supported image size.", "Windows OCR 无法获取可处理的图像大小。"),
        ["Windows 尚未安裝 {0} 的 OCR 語言套件。請到 Windows「設定 → 時間與語言 → 語言與地區」新增該語言並安裝光學字元辨識 (OCR) 功能，再重新開啟 SnipFlow。"] = ("The Windows OCR language pack for {0} is not installed. Add the language in Windows Settings → Time & language → Language & region, install the Optical character recognition (OCR) feature, then reopen SnipFlow.", "Windows 尚未安装 {0} 的 OCR 语言包。请在 Windows「设置 → 时间和语言 → 语言和区域」中添加该语言并安装光学字符识别 (OCR) 功能，然后重新打开 SnipFlow。"),
        ["Windows 尚未安裝可用的 OCR 語言套件。請到「設定 → 時間與語言 → 語言與地區」新增繁體中文或英文，安裝光學字元辨識 (OCR) 功能後再試一次。文字辨識使用本機 Windows OCR，不會上傳截圖。"] = ("No Windows OCR language pack is installed. Add Traditional Chinese or English in Settings → Time & language → Language & region and install Optical character recognition (OCR), then try again. Recognition uses Windows OCR on this computer; captures are not uploaded.", "Windows 尚未安装可用的 OCR 语言包。请在「设置 → 时间和语言 → 语言和区域」中添加繁体中文或英语并安装光学字符识别 (OCR) 功能后重试。文字识别使用本机 Windows OCR，不会上传截图。"),

        // Region selection and inline annotation editor.
        ["目前找不到可擷取的螢幕。"] = ("No screen is available to capture.", "当前没有可捕获的屏幕。"),
        ["無法取得桌面的繪圖內容。"] = ("Could not access the desktop image.", "无法获取桌面的图像内容。"),
        ["螢幕框選需要在 WPF 應用程式中執行。"] = ("Region selection requires a WPF application.", "屏幕框选需要在 WPF 应用程序中运行。"),
        ["擷取範圍必須有正數的寬度與高度。"] = ("The capture region must have a positive width and height.", "捕获范围的宽度和高度必须为正数。"),
        ["拖曳框選畫面"] = ("Drag to select a region", "拖动框选画面"),
        ["Esc 取消   ·   右鍵取消"] = ("Esc to cancel   ·   Right-click to cancel", "Esc 取消   ·   右键取消"),
        ["拖曳選取範圍"] = ("Drag to select", "拖动选择范围"),
        ["滾輪縮放 · 空白鍵拖曳 · Delete 刪除標註"] = ("Scroll to zoom · Space to pan · Delete to remove annotations", "滚轮缩放 · 空格键拖动 · Delete 删除标注"),
        ["Ctrl + Enter 完成 · Esc 取消 · 點選外部完成"] = ("Ctrl + Enter to finish · Esc to cancel · Click outside to finish", "Ctrl + Enter 完成 · Esc 取消 · 点击外部完成"),
        ["影像尺寸不得為零。"] = ("Image dimensions must be greater than zero.", "图像尺寸不能为零。"),
        ["請先開啟或擷取影像。"] = ("Open or capture an image first.", "请先打开或捕获图像。"),

        // Manual scrolling capture and overlap diagnostics.
        ["SnipFlow · 手動長截圖"] = ("SnipFlow · Manual scrolling capture", "SnipFlow · 手动长截图"),
        ["手動長截圖"] = ("Manual scrolling capture", "手动长截图"),
        ["請選取至少 32 × 48 像素的捲動內容範圍。"] = ("Select a scrolling region of at least 32 × 48 pixels.", "请选择至少 32 × 48 像素的滚动内容范围。"),
        ["選取範圍太大，請縮小範圍後重試。"] = ("The selected region is too large. Select a smaller region and try again.", "选择范围过大，请缩小范围后重试。"),
        ["回到目標頁面向下捲動約半頁，再按「加入畫面」。保留重疊內容，並避開固定標頭、頁尾及動畫。"] = ("Scroll the target page down about half a page, then choose Add frame. Leave overlapping content and avoid fixed headers, footers, and animations.", "回到目标页面向下滚动约半页，再点击「加入画面」。保留重叠内容，并避开固定页眉、页脚及动画。"),
        ["範圍 {0:N0} × {1:N0} px"] = ("Region {0:N0} × {1:N0} px", "范围 {0:N0} × {1:N0} px"),
        ["正在取得第一個畫面…"] = ("Capturing the first frame…", "正在获取第一个画面…"),
        ["檢查上方接縫預覽，調整下一張要略過的頂部高度："] = ("Inspect the join preview above and adjust how much to trim from the next frame:", "检查上方拼接预览，调整下一张要跳过的顶部高度："),
        ["確認接縫"] = ("Confirm join", "确认拼接"),
        ["捨棄此畫面"] = ("Discard frame", "舍弃此画面"),
        ["加入畫面"] = ("Add frame", "加入画面"),
        ["上一步"] = ("Undo", "上一步"),
        ["已達 20 張上限，請按「完成」並分段截取。"] = ("The 20-frame limit has been reached. Choose Done and capture the rest separately.", "已达到 20 张上限，请点击「完成」并分段截取。"),
        ["正在取得畫面…"] = ("Capturing a frame…", "正在获取画面…"),
        ["已達畫面記憶體上限，請按「完成」並分段截取。"] = ("The frame memory limit has been reached. Choose Done and capture the rest separately.", "已达到画面内存上限，请点击「完成」并分段截取。"),
        ["第一張已加入。現在可向下捲動目標頁面。 "] = ("First frame added. You can now scroll the target page down.", "第一张已加入。现在可向下滚动目标页面。"),
        ["第一張已加入。現在可向下捲動目標頁面。"] = ("First frame added. You can now scroll the target page down.", "第一张已加入。现在可向下滚动目标页面。"),
        ["正在比對重疊內容…"] = ("Matching overlapping content…", "正在比对重叠内容…"),
        ["畫面沒有變化，未加入重複頁面。請繼續捲動，或按「完成」。"] = ("The frame has not changed, so it was not added. Continue scrolling or choose Done.", "画面没有变化，未加入重复页面。请继续滚动，或点击「完成」。"),
        ["已加入第 {0} 張 · 自動對齊 {1:N0} px（信心 {2:P0}）。"] = ("Frame {0} added · Matched {1:N0} px overlap ({2:P0} confidence).", "已加入第 {0} 张 · 自动对齐 {1:N0} px（置信度 {2:P0}）。"),
        ["已達輸出大小上限。可增加重疊高度，或捨棄此畫面並完成目前長截圖。"] = ("The output size limit has been reached. Increase the overlap or discard this frame and finish the current capture.", "已达到输出大小上限。可增加重叠高度，或舍弃此画面并完成当前长截图。"),
        ["無法可靠對齊。請確認接縫後加入，或捨棄此畫面並減少捲動距離。"] = ("Could not align the frames reliably. Confirm the join before adding, or discard the frame and scroll a shorter distance.", "无法可靠对齐。请确认拼接后加入，或舍弃此画面并减少滚动距离。"),
        ["擷取失敗：{0}"] = ("Capture failed: {0}", "捕获失败：{0}"),
        ["加入後將超過 40 百萬像素，請增加重疊高度，或完成目前的長截圖。"] = ("Adding this frame would exceed 40 million pixels. Increase the overlap or finish the current capture.", "加入后将超过 4000 万像素，请增加重叠高度，或完成当前长截图。"),
        ["已加入第 {0} 張 · 使用手動確認的接縫。"] = ("Frame {0} added · Using the manually confirmed join.", "已加入第 {0} 张 · 使用手动确认的拼接。"),
        ["已捨棄未對齊的畫面。請少捲動一些，保留較多重疊內容。"] = ("Unaligned frame discarded. Scroll a shorter distance to leave more overlap.", "已舍弃未对齐的画面。请少滚动一些，保留更多重叠内容。"),
        ["已移除最後一張。請回到目標頁面重新調整捲動位置。"] = ("Last frame removed. Adjust the scroll position on the target page.", "已移除最后一张。请回到目标页面重新调整滚动位置。"),
        ["重疊 {0:N0} px · 新增 {1:N0} px"] = ("Overlap {0:N0} px · Add {1:N0} px", "重叠 {0:N0} px · 新增 {1:N0} px"),
        ["正在合成長截圖…"] = ("Assembling the scrolling capture…", "正在合成长截图…"),
        ["合成失敗：{0}"] = ("Could not assemble the capture: {0}", "合成失败：{0}"),
        ["{0} 張 · {1:N0} × {2:N0} px · 接縫預覽"] = ("{0} frames · {1:N0} × {2:N0} px · Join preview", "{0} 张 · {1:N0} × {2:N0} px · 拼接预览"),
        ["拼接畫面必須來自同一個截圖範圍，且大小一致。"] = ("Frames must come from the same capture region and have matching dimensions.", "拼接画面必须来自同一截图范围，并且大小一致。"),
        ["範圍太小，請手動指定重疊位置。"] = ("The region is too small. Set the overlap manually.", "范围过小，请手动指定重叠位置。"),
        ["畫面尚未捲動，或已到達頁面底部。"] = ("The page has not scrolled, or it is already at the bottom.", "画面尚未滚动，或已到达页面底部。"),
        ["已找到穩定的垂直重疊。"] = ("A reliable vertical overlap was found.", "已找到稳定的垂直重叠。"),
        ["無法可靠對齊，請調整重疊高度並確認接縫。"] = ("Could not align the frames reliably. Adjust the overlap and check the join.", "无法可靠对齐，请调整重叠高度并确认拼接。"),
        ["長截圖超過 {0:N0} 百萬像素上限，請分段截取。"] = ("The scrolling capture exceeds the {0:N0}-million-pixel limit. Capture it in sections.", "长截图超过 {0:N0} 百万像素上限，请分段截取。"),
        ["請先加入至少一個畫面。"] = ("Add at least one frame first.", "请先加入至少一个画面。"),
        ["每兩個連續畫面都必須指定一個重疊高度。"] = ("Set an overlap height between each pair of consecutive frames.", "每两个连续画面都必须指定一个重叠高度。"),
        ["每個畫面的寬度必須一致。"] = ("All frames must have the same width.", "每个画面的宽度必须一致。"),
        ["重疊高度必須小於畫面高度。"] = ("The overlap must be smaller than the frame height.", "重叠高度必须小于画面高度。"),

        // Recording and compact launcher labels used by the shell.
        ["錄影"] = ("Record", "录制"),
        ["螢幕錄影"] = ("Screen recording", "屏幕录制"),
        ["開始錄影"] = ("Start recording", "开始录制"),
        ["停止錄影"] = ("Stop recording", "停止录制"),
        ["暫停"] = ("Pause", "暂停"),
        ["繼續"] = ("Resume", "继续"),
        ["麥克風"] = ("Microphone", "麦克风"),
        ["系統音訊"] = ("System audio", "系统音频"),
        ["錄製中"] = ("Recording", "录制中"),
        ["錄影完成"] = ("Recording complete", "录制完成"),
        ["錄影未完成"] = ("Recording failed", "录制未完成"),
        ["展開編輯器"] = ("Open editor", "展开编辑器"),
        ["擷取"] = ("Capture", "捕获"),
        ["工具"] = ("Tools", "工具"),
        ["截圖與錄影"] = ("Capture and record", "截图与录制"),
        ["選擇範圍"] = ("Select a region", "选择范围"),
        ["回到編輯器"] = ("Back to editor", "返回编辑器"),
        ["小型面板"] = ("Compact panel", "小型面板"),
        ["編輯器"] = ("Editor", "编辑器"),
        ["錄影已儲存：{0}"] = ("Recording saved: {0}", "录制已保存：{0}"),

        // Recording controls and diagnostics. Existing shared labels retain their translations above.
        ["準備錄影"] = ("Ready to record", "准备录制"),
        ["系統聲音"] = ("System audio", "系统声音"),
        ["滑鼠游標"] = ("Mouse cursor", "鼠标指针"),
        ["為符合影片尺寸，右或下邊緣最多內縮 1 像素。"] = ("The right or bottom edge is trimmed by at most one pixel to fit the video format.", "为适应视频尺寸，右侧或下侧边缘最多向内缩小 1 像素。"),
        ["按下開始後才會錄製，停止後儲存為 MP4。"] = ("Recording begins when you press Start. Stop to save an MP4 video.", "点击开始后才会录制，停止后保存为 MP4。"),
        ["儲存位置在開始錄影前選擇"] = ("Choose a save location before recording", "在开始录制前选择保存位置"),
        ["無法排除錄影操作條，請重新開啟 SnipFlow 後再試。"] = ("The recording toolbar cannot be excluded from capture. Restart SnipFlow and try again.", "无法从录制画面中排除操作栏，请重新打开 SnipFlow 后重试。"),
        ["儲存錄影"] = ("Save recording", "保存录制"),
        ["MP4 影片"] = ("MP4 video", "MP4 视频"),
        ["正在啟動錄影…"] = ("Starting recording…", "正在启动录制…"),
        ["停止並儲存"] = ("Stop and save", "停止并保存"),
        ["放棄錄影"] = ("Discard recording", "放弃录制"),
        ["正在錄製所選範圍。停止後即可播放影片。"] = ("Recording the selected area. Stop to save and play the video.", "正在录制所选范围。停止后即可播放视频。"),
        ["正在完成影片，請稍候…"] = ("Finalizing the video. Please wait…", "正在完成视频，请稍候…"),
        ["正在停止錄影並清除未完成檔案…"] = ("Stopping recording and removing the unfinished file…", "正在停止录制并清除未完成文件…"),
        ["未能完成錄影清理，請重試關閉。"] = ("Recording cleanup did not finish. Try closing again.", "录制清理未完成，请重新尝试关闭。"),
        ["正在啟動"] = ("Starting", "正在启动"),
        ["錄影中"] = ("Recording", "录制中"),
        ["正在完成影片"] = ("Finalizing video", "正在完成视频"),
        ["已儲存"] = ("Saved", "已保存"),
        ["已取消"] = ("Cancelled", "已取消"),
        ["錄影失敗"] = ("Recording failed", "录制失败"),
        ["已有錄影正在進行。"] = ("A recording is already in progress.", "已有录制正在进行。"),
        ["螢幕錄影需要 Windows 10 2004 或更新版本。"] = ("Screen recording requires Windows 10 version 2004 or later.", "屏幕录制需要 Windows 10 2004 或更新版本。"),
        ["請使用 SnipFlow 的 Windows x64 版本錄影。"] = ("Use the Windows x64 version of SnipFlow to record.", "请使用 SnipFlow 的 Windows x64 版本进行录制。"),
        ["目前 Windows 工作階段不支援螢幕錄影。請確認顯示卡驅動程式及遠端桌面設定。"] = ("This Windows session does not support screen recording. Check the graphics driver and Remote Desktop settings.", "当前 Windows 会话不支持屏幕录制。请检查显卡驱动程序和远程桌面设置。"),
        ["尚未開始錄影。"] = ("Recording has not started.", "尚未开始录制。"),
        ["錄影已取消，未儲存影片。"] = ("Recording was cancelled. No video was saved.", "录制已取消，未保存视频。"),
        ["選取範圍不在任何可錄製的螢幕內，請重新框選。"] = ("The selected area is outside the available displays. Select an area again.", "所选范围不在任何可录制的屏幕内，请重新框选。"),
        ["找不到系統聲音輸出裝置。請連接喇叭或耳機，或關閉系統聲音。"] = ("No audio output device was found. Connect speakers or headphones, or turn off system audio.", "找不到系统声音输出设备。请连接扬声器或耳机，或关闭系统声音。"),
        ["找不到麥克風。請連接麥克風並允許桌面應用程式存取，或關閉麥克風。"] = ("No microphone was found. Connect a microphone and allow desktop apps to access it, or turn off the microphone.", "找不到麦克风。请连接麦克风并允许桌面应用访问，或关闭麦克风。"),
        ["錄影元件無法釋放，請重新啟動 SnipFlow。"] = ("The recorder could not shut down. Restart SnipFlow.", "录制组件无法释放，请重新启动 SnipFlow。"),
        ["錄影沒有產生有效影片。請等待畫面開始錄製後再停止。"] = ("No valid video was produced. Wait until recording begins before stopping.", "录制未生成有效视频。请等待画面开始录制后再停止。"),
        ["無法清除未完成影片。"] = ("The unfinished video could not be removed.", "无法清除未完成的视频。"),
        ["影片收尾逾時，正在停止錄影並清除未完成檔案。"] = ("Video finalization timed out. Stopping the recorder and removing the unfinished file.", "视频收尾超时，正在停止录制并清除未完成文件。"),
        ["錄影失敗，請檢查顯示卡、音訊裝置及儲存位置。"] = ("Recording failed. Check the graphics driver, audio devices and save location.", "录制失败，请检查显卡、音频设备和保存位置。"),
        ["請選擇影片儲存位置。"] = ("Choose a video save location.", "请选择视频保存位置。"),
        ["影片副檔名必須是 .mp4。"] = ("The video file must use the .mp4 extension.", "视频文件扩展名必须为 .mp4。"),
        ["影片檔案已存在，請改用另一個檔名。"] = ("The video file already exists. Choose another name.", "视频文件已存在，请使用其他文件名。"),
        ["錄影元件無法載入。請安裝 Visual C++ 2015–2022 x64 執行階段並重新開啟 SnipFlow。"] = ("The recording component could not load. Install the Visual C++ 2015–2022 x64 runtime, then restart SnipFlow.", "无法加载录制组件。请安装 Visual C++ 2015–2022 x64 运行时并重新打开 SnipFlow。"),
        ["錄影範圍至少需要 2 × 2 像素。"] = ("The recording area must be at least 2 × 2 pixels.", "录制范围至少需要 2 × 2 像素。"),
        ["錄影範圍過大，請選取較小的範圍。"] = ("The recording area is too large. Select a smaller area.", "录制范围过大，请选择较小的范围。")
    };
}
