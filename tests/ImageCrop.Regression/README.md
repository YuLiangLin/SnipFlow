# 圖片編輯器回歸測試

這是無測試框架依賴的 console regression harness。以 `ProjectReference` 呼叫產品本身的模型、渲染、合圖操作與 `.snipflow` 專案保存，不複製裁剪實作。

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
- 單向／雙向箭頭兩端渲染與 hit-test、方向／粗細／極短線安全、預設套用至真實畫圖 factory、Clone／CopyFrom／ContentEquals、多選樣式與單步 Undo／Redo／no-op、複製／session／旋轉、schema 7 寫入完整樣式、舊 schema 1–5 預設單向與 schema 6 boolean 映射、非 Arrow 與 malformed flag 拒絕。
- 全部八個 D4 orientation、選取圖片與整張合圖的旋轉／翻轉、Undo／Redo、四次旋轉回原。
- schema 7 保存／重開保留完整內嵌 PNG 與 viewport；schema 3 不帶 crop、schema 4 嚴格 0..1 crop 與 schema 5 留白 viewport 的相容性；舊 schema 帶新留白 viewport、不安全座標、非圖片帶 crop 與篡改檔拒絕，失敗保留開啟文件。
- 裁剪草稿與正式文件隔離、改 frame、frame 內平移、完成／取消／重設、單一步 Undo／Redo、匯出／session snapshot 提交、載圖取消、Enter／Escape handler、裁剪後排列維持既有 frame 比例與 viewport。
- 固定 frame 的 1%–1600% 縮放、中心點、放大真實像素、縮小透明留白、移動露白、重設依原圖比例置中 contain；取消／復原／重做／保存重開、旋轉翻轉、無效值與有限極值、同 scale 不變更、舊極端 zoom 可在同一次 session 放大／縮小後返回、contain 小於 1% 的 slider 同步與 session 範圍重設。
- 直接測試 OnMouseDown 使用的真實 TryBeginImageCropAt 分流：圖片雙擊啟動、Ctrl／Shift／空白／文字不被誤攔截；透明圖與上層標註的 preview／export 逐像素一致；一來源像素窄框與部分超出畫布的邊界。

harness 建立空 Avalonia Application 並使用 `SetupWithoutStarting`，不建立 Avalonia Window，不啟動 `SnipFlow.App`／`MainWindow`，不發送 OS 鍵鼠事件或 routed events，不讀寫剪貼簿、不註冊全域快捷鍵、不安裝或部署。Enter／Escape、slider 導覽鍵與 Ctrl + 滾輪都是直接傳入測試 argument 呼叫原 handler；slider.Value callback 直接變更離屏控件屬性，檢查 frame、取消與同步防重入。另驗證同一次 session 只擴張 legacy zoom 範圍、結束後重設，以及同 scale 不產生編輯。這不能證明真實雙擊、pointer capture、視窗排版、IME 或各桌面 DPI 的互動行為。

測試 Application 提供只有 `TargetType=Button` 的最小 `QuietButton` theme placeholder，讓箭頭設定面板的同步／preset intent 測試可建構；不建立 template，也不以此宣稱真實面板主題或排版已通過視覺驗證。標註像素仍由真實產品 renderer 產生。

`SaveProject`／`RestoreProject` 使用產品原本的 checkpoint 機制：在 `SettingsStore.DataRoot/UpdateSessions` 短暫建立本次專用的 GUID 資料夾，完成後產品會清除；測試檢查既有 checkpoint 目錄清單保持不變。不修改使用者設定或既有專案。

設定偏好測試只呼叫 `ReadSettingsFile(path)`／`WriteSettingsFile(path, settings)`，檔案位於本次輸出的 `arrow-preferences/`；以 reflection 檢查 `Lazy<UserSettings>.IsValueCreated`，不讀取 `SettingsStore.Current`、不呼叫 `Save()`、不寫入 live settings 或 registry。MainWindow 的啟動套用與使用者事件保存分流不在本 harness 建構範圍，須另做原始碼檢查或經授權的互動驗證。

可單獨執行重新啟動探針（提供測試 JSON，不能用 live settings 路徑）：

```powershell
dotnet tests/ImageCrop.Regression/bin/Release/net10.0-windows10.0.19041.0/ImageCrop.Regression.dll --arrow-preference-probe verification/arrow-preferences/test-settings.json --output verification/arrow-preferences/probe
```

探針輸出 `arrow-preference-probe.json`，包含新 process ID、產品 DLL hash、輸入設定 hash、lazy Current 前後狀態與 settings／Editor／factory 三階段的五欄位。此模式標記 `fullRegressionRun=false`；它只證明指定檔案的重新讀取與新 Editor 預設，不是完整回歸，也不啟動 SnipFlow。正常測試以相同 host 自動跑兩次探針並保存各自的 log／exit code：父行程若由 global `dotnet` 啟動就沿用該 host，self-contained Linux 則沿用 harness apphost，繼承 `DISPLAY`／`LD_LIBRARY_PATH`。

## 舊版失敗證據

保留 `SNIPFLOW-IMAGE-CROP-001`、`SNIPFLOW-IMAGE-CROP-ZOOM-002`、`SNIPFLOW-DOUBLE-ARROW-003`、`SNIPFLOW-ARROW-STYLES-004`，新增 `SNIPFLOW-ARROW-PREFERENCE-005`。所有新增 API 都透過 reflection 取得；0.8.4 應通過前四個 contract，並因缺少 `UserSettings.ArrowStyle` 而在偏好 contract 明確失敗、產生結果並回傳 1。更早的 0.8.3 仍會在樣式 contract 因缺少 `AnnotationItem.ArrowStyle` 失敗。不得把 binder 載入失敗當成有效的舊紅新綠證據。

先正常建置 harness，將其完整 output 複製到新的 ignored 驗證子目錄，再只把該副本的 `SnipFlow.dll` 換成已驗證的 0.8.4（有完整樣式、尚未保存箭頭偏好）assembly。若舊版版本號低於 harness 編譯時的 assembly identity，應在隔離的舊 source 目錄以 `-p:Version=<目前版本>` 統一 metadata；記錄這個唯一 override，保留舊 product logic。不要把 binder 的 FileNotFoundException 當成裁剪回歸的有效紅測。執行副本及正常新版各一次、使用不同 `--output` 目錄，保留 exit code 與 `productAssemblySha256`。不要覆寫產品建置目錄、不要修改 repository 工作檔，也不要在其他工作正在 build 時重建共同輸出。

## 舊版預設箭頭像素對照

```powershell
dotnet run --project tests/ImageCrop.Regression -c Release -- --legacy-arrow-fixtures --output verification/arrow-styles/legacy-new
```

此模式只使用舊版已有的 `DoubleEndedArrow` API，略過所有 contract guards 與回歸測試。輸出 `legacy-arrow-fixtures.json`：水平／斜線／短線 × 粗細 1／4／32 × 單向／雙向，共 18 個 `name`／`pixelSha256`；僅保存兩張代表 PNG。JSON 明確標記 `mode=fixture-only`、`fullRegressionRun=false`，不得把此模式當成完整 green。

把同一份 harness output 複製到新的 ignored 子目錄，僅 overlay 版本身分已匹配的 0.8.3 DLL，再以 global `dotnet ImageCrop.Regression.dll --legacy-arrow-fixtures --output <舊版證據目錄>` 執行。以 fixture 名稱配對，要求新舊 18 個像素 hash 完全一致。這會直接比較兩份真實產品 renderer，不複製舊繪圖實作，也不啟動 Window 或 OS 輸入。
