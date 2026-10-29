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
- 單向／雙向箭頭兩端渲染與 hit-test、方向／粗細／極短線安全、預設套用至真實畫圖 factory、Clone／CopyFrom／ContentEquals、多選樣式與單步 Undo／Redo／no-op、複製／session／旋轉、schema 6 保存與舊 schema 1–5 預設單向、非 Arrow 與 malformed flag 拒絕。
- 全部八個 D4 orientation、選取圖片與整張合圖的旋轉／翻轉、Undo／Redo、四次旋轉回原。
- schema 6 保存／重開保留完整內嵌 PNG 與 viewport；schema 3 不帶 crop、schema 4 嚴格 0..1 crop 與 schema 5 留白 viewport 的相容性；舊 schema 帶新留白 viewport、不安全座標、非圖片帶 crop 與篡改檔拒絕，失敗保留開啟文件。
- 裁剪草稿與正式文件隔離、改 frame、frame 內平移、完成／取消／重設、單一步 Undo／Redo、匯出／session snapshot 提交、載圖取消、Enter／Escape handler、裁剪後排列維持既有 frame 比例與 viewport。
- 固定 frame 的 1%–1600% 縮放、中心點、放大真實像素、縮小透明留白、移動露白、重設依原圖比例置中 contain；取消／復原／重做／保存重開、旋轉翻轉、無效值與有限極值、同 scale 不變更、舊極端 zoom 可在同一次 session 放大／縮小後返回、contain 小於 1% 的 slider 同步與 session 範圍重設。
- 直接測試 OnMouseDown 使用的真實 TryBeginImageCropAt 分流：圖片雙擊啟動、Ctrl／Shift／空白／文字不被誤攔截；透明圖與上層標註的 preview／export 逐像素一致；一來源像素窄框與部分超出畫布的邊界。

harness 建立空 Avalonia Application 並使用 `SetupWithoutStarting`，不建立 Avalonia Window，不啟動 `SnipFlow.App`／`MainWindow`，不發送 OS 鍵鼠事件或 routed events，不讀寫剪貼簿、不註冊全域快捷鍵、不安裝或部署。Enter／Escape、slider 導覽鍵與 Ctrl + 滾輪都是直接傳入測試 argument 呼叫原 handler；slider.Value callback 直接變更離屏控件屬性，檢查 frame、取消與同步防重入。另驗證同一次 session 只擴張 legacy zoom 範圍、結束後重設，以及同 scale 不產生編輯。這不能證明真實雙擊、pointer capture、視窗排版、IME 或各桌面 DPI 的互動行為。

`SaveProject`／`RestoreProject` 使用產品原本的 checkpoint 機制：在 `SettingsStore.DataRoot/UpdateSessions` 短暫建立本次專用的 GUID 資料夾，完成後產品會清除；測試檢查既有 checkpoint 目錄清單保持不變。不修改使用者設定或既有專案。

## 舊版失敗證據

保留 `SNIPFLOW-IMAGE-CROP-001` 與 `SNIPFLOW-IMAGE-CROP-ZOOM-002`，新增 `SNIPFLOW-DOUBLE-ARROW-003`。所有新增 API 都透過 reflection 取得；0.8.2 應通過既有 crop／zoom contract，並因缺少 `AnnotationItem.DoubleEndedArrow` 而在雙向箭頭 contract 明確失敗、產生結果並回傳 1。不得把 binder 載入失敗當成有效的舊紅新綠證據。

先正常建置 harness，將其完整 output 複製到新的 ignored 驗證子目錄，再只把該副本的 `SnipFlow.dll` 換成已驗證的 0.8.2（有裁剪／縮放、沒有雙向箭頭）assembly。若舊版版本號低於 harness 編譯時的 assembly identity，應在隔離的舊 source 目錄以 `-p:Version=<目前版本>` 統一 metadata；記錄這個唯一 override，保留舊 product logic。不要把 binder 的 FileNotFoundException 當成裁剪回歸的有效紅測。執行副本及正常新版各一次、使用不同 `--output` 目錄，保留 exit code 與 `productAssemblySha256`。不要覆寫產品建置目錄、不要修改 repository 工作檔，也不要在其他工作正在 build 時重建共同輸出。
