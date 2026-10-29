# 圖片編輯器回歸測試

這是無測試框架依賴的 console regression harness。以 `ProjectReference` 呼叫產品本身的模型、渲染、合圖操作與 `.snipflow` 專案保存，不複製裁剪實作。

目前 0.8.9 的完整執行預期包含 204 項（保留原 191 項，加上 `SNIPFLOW-TEXT-FONT-009` contract、10 項字型案例及 2 項最後工具偏好案例）。這是測試組成說明，實際通過／失敗數、產品 DLL hash 與平台證據以各次輸出的 `results.json` 為準；`--model-only`、probe 與 fixture-only 模式不代表完整 204 項回歸。

在 repository 根目錄執行：

```powershell
dotnet run --project tests/ImageCrop.Regression -c Release
```

Windows 自動選擇 Windows TFM；Linux 選擇 `net10.0`。Linux 的 Avalonia platform 初始化仍需要 X11／XWayland；有虛擬顯示器的環境可執行：

```bash
xvfb-run -a dotnet run --project tests/ImageCrop.Regression -c Release
```

可以指定輸出目錄，或略過圖片裁剪操作測試（仍執行模型、保存及箭頭回歸）：

```powershell
dotnet run --project tests/ImageCrop.Regression -c Release -- --output verification/image-crop/local-run
dotnet run --project tests/ImageCrop.Regression -c Release -- --model-only
```

`results.json`、PNG fixtures 與 `.snipflow` 測試專案寫入 ignored 的 `verification/image-crop/<時間>`。任何失敗均回傳非零 exit code。使用新的輸出目錄可保留每次證據。

驗證範圍包括：

- normalized source viewport（允許超出原圖以留白）、192 DPI physical source rectangle、四象限來源的像素、Clone／CopyFrom／ContentEquals、完整來源不被破壞。
- ArrowStyle 五欄位的各端點種類／大小與線型代表矩陣、虛線／點線與實線端點、樣式 Clone／Undo／保存／schema 6 映射、非法 enum/null/衝突 truth 原子拒絕、多選只改差異欄位及同值 preset intent。
- 箭頭五個偏好的真實設定 JSON roundtrip、既有數字 enum 轉存名稱、舊檔缺欄預設、損壞樣式僅回復箭頭預設並保留其他偏好；隔離路徑 I/O 不初始化 lazy Current、不發全域 Changed、不留下暫存檔；兩次全新 console 子行程讀取同檔後套用至離屏 Editor 與真實畫圖 factory。另驗證 selection／restore／undo 與 panel 同步不發出使用者樣式編輯事件。
- 箭頭顏色／粗細分別保存為 opaque `#RRGGBB`（大寫）與 1–32 的有限數字（保留小數），兩欄獨立處理 legacy／null／非法型別／越界與 duplicate last-wins；非法寫入在建立目錄或暫存檔前拒絕，不覆蓋原檔。RestoreArrowAppearance 只改 default，不改選取文件／history；Arrow 與 Pen／Rectangle／Text 的色寬隔離、primary Arrow 編輯會更新 default、Undo／Redo／載入其他文件／新合圖不覆寫已保存偏好。
- Arrow／Rectangle／Ellipse／Pen／Highlight／Text／Mosaic 各有獨立色寬預設；六個非 Arrow entry 使用固定英文工具名存入 `ToolAppearances`，Arrow 沿用既有 `ArrowColor`／`ArrowStrokeWidth` 作唯一來源。各工具逐欄非法型別／顏色／非有限及越界粗細只回復該欄預設，其他六工具、箭頭五個樣式與無關設定保留；工具 block／entry／field 的 duplicate 均驗證 last-wins。忽略 Arrow shadow entry、Select／Image／未知或非固定名稱 key；支持工具的非法寫入在任何 I/O 前拒絕、保留原檔且不讀 Current。
- 工具切換與明確傳入不同 tool 的 factory 不串用外觀；多選時 primary 決定要更新哪一個未來預設，已選文件維持原本批次套色／線寬的規則（Mosaic 不改色、Text 不改文件線寬）。Restore defaults 不改選取像素／revision／undo，Undo／Redo／RestoreSession／LoadImage／NewCollage 保留七工具預設。文字測試實際呼叫 `BeginTextEdit(null, placement, null)`，檢查 draft 與 TextBox foreground，輸入內容後 `CommitTextEdit()`，再檢查已建立 Text 的色寬、內容與單步 Undo／Redo；不是只驗證通用 factory。Text 線寬與 Mosaic 顏色雖測 API／保存，產品 UI 仍分別顯示文字顏色與 Mosaic 粗細。
- `TextSize` 使用 26 作相容預設，驗證 37.5 小數與 12／240 端點的真實設定 roundtrip；缺值、null、字串、bool、object、array、越界與 overflow 只回復該欄，保留七工具色寬、箭頭五樣式與其他偏好，duplicate 依最後值決定。NaN／Infinity／越界寫入在 I/O 前拒絕且不改原檔、不建目錄、不讀 Current。`RestoreTextSize` 只改未來預設，選取既有文字的字級、像素、revision 與 undo 保持；主動 `TextSize` 編輯仍套用選取 Text 並 clamp 到 12–240，Undo／Redo 只還原文件、預設跨工具切換／載入／新合圖保留。真正新增文字 draft、TextBox、commit 與 redo 均保留 37.5，編輯舊文字仍使用該物件原字級。
- 不建構 MainWindow，直接以 reflection 呼叫實際 `TryGetTextSizeChange` 純判定 helper。表格驗證 42.125 顯示為 42.1 或既有文字顯示 32 時不改字級、相同數字的不同格式不變更、空白／非法／NaN／Infinity 拒絕、真正變更及 12–240 clamp；en-US／de-DE／fr-FR／zh-TW 的一位小數 formatter 輸出保持原精度，de-DE／fr-FR 逗號小數依文化解析。此案例驗證保存判定函式，不發送焦點或鍵盤事件，不能替代真實 MainWindow 事件接線的互動驗證。
- 字型目錄與名稱安全驗證：空字串維持舊 AppFont；trim、128 字元上限、control／URI／分隔符拒絕，未安裝的合法名稱仍保存並由真正 resolver 回退 AppFont。以實際安裝、ASCII 字寬不同的兩個字型測 layout cache、不同像素、hit bounds、Clone／CopyFrom／ContentEquals 與各物件獨立；保存本平台使用的字型、度量與像素 hash，不要求 Windows／Linux 字型像素跨平台相同。
- 字型設定的合法／缺值／錯型／非法名稱與 duplicate last-wins、upfront 原子寫入及 Current 隔離；其他七工具色寬、Arrow 七欄與 TextSize 保留。Restore 字型只改預設；主動編輯套用選取 Text 且不覆蓋各自字級，Undo／Redo／載入／新文件保留未來預設；真正 TextBox 同步有效 family／weight／style／stretch，新文字 commit 保存字型、舊文字保留原字型。schema 8 兩個 Text 的獨立字型／字級與同平台像素往返；schema 1–7 缺字型使用舊預設，舊 schema 非空字型、null／錯型／URI／unsafe 或 non-Text 非空字型原子拒絕。
- 最後工具偏好以固定英文 enum 名保存；Select 與七個工具各自往返，既有 defined numeric 值可讀。missing／非法值只回復 Pen，保留字型／字級／七工具與 Arrow 偏好；非法寫入不改原檔、不建目錄、不讀 Current。新程序檢查設定及新 Editor 的初始工具 Text；不以此宣稱 MainWindow 啟動／快捷鍵保存接線已互動驗證。
- 真正 font picker 的 Value／RefreshLanguage 同步不發 FontChanged，實際 ComboBox 改選只通知一次；非法 Value 不改狀態。直接呼叫 MainWindow 的純 `IsFontPickerInput` 判定：選單 choosing 時 None／Shift／Alt 留給字型輸入，Ctrl／Ctrl+Shift／Meta 交回文件快捷鍵；未 choosing 一律不攔截。此處不建立 MainWindow，也不能證明原生 focus 或 routed keyboard 事件。
- 單向／雙向箭頭兩端渲染與 hit-test、方向／粗細／極短線安全、預設套用至真實畫圖 factory、Clone／CopyFrom／ContentEquals、多選樣式與單步 Undo／Redo／no-op、複製／session／旋轉、schema 8 寫入完整樣式、舊 schema 1–5 預設單向與 schema 6 boolean 映射、非 Arrow 與 malformed flag 拒絕。
- 全部八個 D4 orientation、選取圖片與整張合圖的旋轉／翻轉、Undo／Redo、四次旋轉回原。
- schema 8 保存／重開保留完整內嵌 PNG 與 viewport；schema 3 不帶 crop、schema 4 嚴格 0..1 crop 與 schema 5 留白 viewport 的相容性；舊 schema 帶新留白 viewport、不安全座標、非圖片帶 crop 與篡改檔拒絕，失敗保留開啟文件。
- 裁剪草稿與正式文件隔離、改 frame、frame 內平移、完成／取消／重設、單一步 Undo／Redo、匯出／session snapshot 提交、載圖取消、Enter／Escape handler、裁剪後排列維持既有 frame 比例與 viewport。
- 固定 frame 的 1%–1600% 縮放、中心點、放大真實像素、縮小透明留白、移動露白、重設依原圖比例置中 contain；取消／復原／重做／保存重開、旋轉翻轉、無效值與有限極值、同 scale 不變更、舊極端 zoom 可在同一次 session 放大／縮小後返回、contain 小於 1% 的 slider 同步與 session 範圍重設。
- 直接測試 OnMouseDown 使用的真實 TryBeginImageCropAt 分流：圖片雙擊啟動、Ctrl／Shift／空白／文字不被誤攔截；透明圖與上層標註的 preview／export 逐像素一致；一來源像素窄框與部分超出畫布的邊界。

harness 建立空 Avalonia Application 並使用 `SetupWithoutStarting`，不建立 Avalonia Window，不啟動 `SnipFlow.App`／`MainWindow`，不發送 OS 鍵鼠事件或 routed events，不讀寫剪貼簿、不註冊全域快捷鍵、不安裝或部署。Enter／Escape、slider 導覽鍵與 Ctrl + 滾輪都是直接傳入測試 argument 呼叫原 handler；slider.Value callback 直接變更離屏控件屬性，檢查 frame、取消與同步防重入。另驗證同一次 session 只擴張 legacy zoom 範圍、結束後重設，以及同 scale 不產生編輯。這不能證明真實雙擊、pointer capture、視窗排版、IME 或各桌面 DPI 的互動行為。

測試 Application 提供只有 `TargetType=Button` 的最小 `QuietButton` theme placeholder，讓箭頭設定面板的同步／preset intent 測試可建構；不建立 template，也不以此宣稱真實面板主題或排版已通過視覺驗證。標註像素仍由真實產品 renderer 產生。

`SaveProject`／`RestoreProject` 使用產品原本的 checkpoint 機制：在 `SettingsStore.DataRoot/UpdateSessions` 短暫建立本次專用的 GUID 資料夾，完成後產品會清除；測試檢查既有 checkpoint 目錄清單保持不變。不修改使用者設定或既有專案。

設定偏好測試只呼叫 `ReadSettingsFile(path)`／`WriteSettingsFile(path, settings)`，檔案位於本次輸出的 `arrow-preferences/`、`arrow-appearance/`、`tool-appearance/`、`text-size/`、`text-font/` 與 `last-tool/`；以 reflection 檢查 `Lazy<UserSettings>.IsValueCreated`，不讀取 `SettingsStore.Current`、不呼叫 `Save()`、不寫入 live settings 或 registry。MainWindow 的啟動套用與使用者事件保存分流不在本 harness 建構範圍，須另做原始碼檢查或經授權的互動驗證。

可單獨執行重新啟動探針（提供測試 JSON，不能用 live settings 路徑）：

```powershell
dotnet tests/ImageCrop.Regression/bin/Release/net10.0-windows10.0.19041.0/ImageCrop.Regression.dll --arrow-preference-probe verification/arrow-preferences/test-settings.json --output verification/arrow-preferences/probe
```

探針輸出 `arrow-preference-probe.json`，包含新 process ID、產品 DLL hash、輸入設定 hash、lazy Current 前後狀態與 settings／Editor／factory 三階段的箭頭五個樣式欄位及顏色／粗細，共七個既有偏好；另有 `toolSettingsAppearance`／`toolEditorAppearance`／`toolFactoryAppearance`，分別由真實設定 getter、Editor getter 與 factory 建立物件讀出七工具色寬。父行程直接對照下表的獨立預定值，不只檢查 probe 自報 `success` 或三階段彼此相等；每個 probe 也刻意切到另一 active tool 再建立各工具標註。

兩次探針也各自把非預設字級 37.5 從設定套用到新的 Editor，讀取真正 Text factory，並直接 `BeginTextEdit`／設定文字／`CommitTextEdit`。JSON 的 `settingsTextSize`、`editorTextSize`、`factoryTextSize`、`draftTextSize`、`textEditorFontSize`、`committedTextSize` 六個階段都由父行程對照固定 37.5；既有七工具與箭頭七欄檢查仍完整保留。此處建立的是離屏控制項，不能據此宣稱 MainWindow 的字級欄位、焦點與 Enter／Escape 使用者事件保存已實際操作驗證。

字型由父行程從已安裝目錄選出明確非空名稱（優先 DejaVu Sans Mono／Consolas 等），寫入指定設定檔後，對兩個新程序的 `settingsTextFontFamily`／`editorTextFontFamily`／`factoryTextFontFamily`／`draftTextFontFamily`／`textEditorFontFamily`／`committedTextFontFamily` 六階段比對該名稱。`textEditorFontFamily` 是有效 typeface cascade 的第一個 family，完整 cascade 另記於 `textEditorEffectiveFamily`，且實際 TextBox 四個 typeface 屬性必須匹配產品 resolver。缺少安裝字型的回退另有專項，不能丟棄 requested family 來讓測試通過。`settingsLastAnnotationTool`／`restoredLastAnnotationTool` 必須為 Text。

| 工具 | 預定顏色 | 預定粗細 |
| --- | --- | --- |
| Arrow | `#A1B2C3` | 7.25 |
| Rectangle | `#D05060` | 2.5 |
| Ellipse | `#22AA44` | 3.75 |
| Pen | `#4433CC` | 5.5 |
| Highlight | `#EEDD11` | 9.125 |
| Text | `#09AABB` | 6.25 |
| Mosaic | `#997766` | 13.5 |

此模式標記 `fullRegressionRun=false`；它只證明指定檔案的重新讀取與新 Editor 預設，不是完整回歸，也不啟動 SnipFlow。正常測試以相同 host 自動跑兩次探針，要求兩個 PID 彼此不同且不同於父行程、產品 hash 相同、lazy Current 前後皆為 false、輸入設定 hash 不變，並保存各自 log／exit code：父行程若由 global `dotnet` 啟動就沿用該 host，self-contained Linux 則沿用 harness apphost，繼承 `DISPLAY`／`LD_LIBRARY_PATH`。

## 舊版失敗證據

保留既有 `SNIPFLOW-IMAGE-CROP-001` 至 `SNIPFLOW-TEXT-SIZE-008`，新增 `SNIPFLOW-TEXT-FONT-009`。所有新增 API 都透過 reflection 取得；真實 0.8.8 應通過前八個 contract，並因缺少 `UserSettings.TextFontFamily` 在第九個 contract 明確失敗、產生結果並回傳 1，之後不執行測試案例或讀取 lazy Current。真實 0.8.7 仍因缺少 `UserSettings.TextSize` 在第八個失敗；更早版本仍由各自缺少 API 的 contract 明確拒絕。不得把 binder 載入失敗當成有效的舊紅新綠證據。

先正常建置 harness，將其完整 output 複製到新的 ignored 驗證子目錄，再只把該副本的 `SnipFlow.dll` 換成已驗證的 0.8.8（保存各工具色寬與字級、尚無字型／最後工具偏好）assembly。若舊版版本號低於 harness 編譯時的 assembly identity，應在隔離的舊 source 目錄以 `-p:Version=0.8.9` 統一 metadata；記錄這個唯一 override，保留舊 product logic。不要把 binder 的 FileNotFoundException 當成有效紅測。執行副本及正常新版各一次、使用不同 `--output` 目錄，保留 exit code 與 `productAssemblySha256`。不要覆寫產品建置目錄、不要修改 repository 工作檔，也不要在其他工作正在 build 時重建共同輸出。

## 離屏字型選單視覺檢查

```powershell
dotnet tests/ImageCrop.Regression/bin/Release/net10.0-windows10.0.19041.0/ImageCrop.Regression.dll --font-picker-visual --app-theme-source src/App.axaml --output verification/text-font/picker-visual
```

此獨立模式使用實際 `TextFontPicker`，載入真正 FluentTheme、指定 App.axaml 的 FontFamily／CornerRadius 資源及 ComboBox／ComboBoxItem／TextBlock scalar styles，再使用產品 ThemeService 本身的 palette／shared brushes／Fluent aliases；不呼叫會讀 Current 的 ThemeService.Initialize／Apply，也不使用 placeholder 宣稱字型選單外觀通過。輸出 36 張 PNG 與 `font-picker-visual.json`：Light／Dark × zh-TW／en-US × 120／150／180px slot × 預設／已安裝／長未安裝名稱。記錄真實來源 App.axaml hash、控件 bounds、label 及圖片 hash；模板未產生可見文字或控件溢出 slot 會失敗。

離屏控制項沒有視窗 logical root，因此 fixture 以真正 ThemeVariantScope 提供來源樣式與資源，繼承 App.axaml 的 Window 字級及 AppFont／Ink context。依序 BeginInit／EndInit、從真 Fluent 資源取得 ControlTheme、ApplyStyling／ApplyTemplate、ContentPresenter.UpdateChild，再 Measure／Arrange；UpdateChild 是 Avalonia 提供給 unattached 控制項測試的子控件建立方式，不替換產品控制項或模板。另斷言實際 CornerRadius 等於來源 setter（目前為 9）、Foreground 等於當前真 palette、所有 Popup 保持關閉，並保留 host／picker／ComboBox／所有 presenter 的 Bounds、DesiredSize 與模板診斷。

模式標記 `fullRegressionRun=false`，只驗閉合的共用 picker；不建立 Window／Popup／MainWindow、不展開 ComboBox、不送 OS 或 routed input。其餘 App.axaml styles 未載入，不能宣稱整個 MainWindow 的窄視窗排列、popup 原生定位、鍵盤 focus／字母搜尋、IME 或桌面 DPI 已互動驗證。PNG 仍須目視檢查，bounds 與像素檔存在本身不等於視覺品質通過。

## 舊版預設箭頭像素對照

```powershell
dotnet run --project tests/ImageCrop.Regression -c Release -- --legacy-arrow-fixtures --output verification/arrow-styles/legacy-new
```

此模式只使用舊版已有的 `DoubleEndedArrow` API，略過所有 contract guards 與回歸測試。輸出 `legacy-arrow-fixtures.json`：水平／斜線／短線 × 粗細 1／4／32 × 單向／雙向，共 18 個 `name`／`pixelSha256`；僅保存兩張代表 PNG。JSON 明確標記 `mode=fixture-only`、`fullRegressionRun=false`，不得把此模式當成完整 green。

把同一份 harness output 複製到新的 ignored 子目錄，僅 overlay 版本身分已匹配的 0.8.3 DLL，再以 global `dotnet ImageCrop.Regression.dll --legacy-arrow-fixtures --output <舊版證據目錄>` 執行。以 fixture 名稱配對，要求新舊 18 個像素 hash 完全一致。這會直接比較兩份真實產品 renderer，不複製舊繪圖實作，也不啟動 Window 或 OS 輸入。
