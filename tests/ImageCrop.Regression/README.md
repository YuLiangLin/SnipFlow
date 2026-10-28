# 合圖圖片裁剪回歸測試

這是無測試框架依賴的 console regression harness。以 `ProjectReference` 呼叫產品本身的模型、渲染、合圖操作與 `.snipflow` 專案保存，不複製裁剪實作。

在 repository 根目錄執行：

```powershell
dotnet run --project tests/ImageCrop.Regression -c Release
```

Windows 自動選擇 Windows TFM；Linux 選擇 `net10.0`。Linux 的 Avalonia platform 初始化仍需要 X11／XWayland；有虛擬顯示器的環境可執行：

```bash
xvfb-run -a dotnet run --project tests/ImageCrop.Regression -c Release
```

可以指定輸出目錄，或只測模型／保存：

```powershell
dotnet run --project tests/ImageCrop.Regression -c Release -- --output verification/image-crop/local-run
dotnet run --project tests/ImageCrop.Regression -c Release -- --model-only
```

`results.json`、PNG fixtures 與 `.snipflow` 測試專案寫入 ignored 的 `verification/image-crop/<時間>`。任何失敗均回傳非零 exit code。使用新的輸出目錄可保留每次證據。

驗證範圍包括：

- normalized source crop、192 DPI source rectangle、四象限來源的像素、Clone／CopyFrom／ContentEquals、完整來源不被破壞。
- 全部八個 D4 orientation、選取圖片與整張合圖的旋轉／翻轉、Undo／Redo、四次旋轉回原。
- schema 4 保存／重開保留完整內嵌 PNG 與 crop；schema 3 不帶 crop 的相容性；舊 schema 帶 crop、無效座標、非圖片帶 crop 與篡改檔拒絕，失敗保留開啟文件。
- 裁剪草稿與正式文件隔離、改 frame、frame 內平移、完成／取消／重設、單一步 Undo／Redo、匯出／session snapshot 提交、載圖取消、Enter／Escape handler、裁剪後排列維持可見圖片比例。
- 直接測試 OnMouseDown 使用的真實 TryBeginImageCropAt 分流：圖片雙擊啟動、Ctrl／Shift／空白／文字不被誤攔截；透明圖與上層標註的 preview／export 逐像素一致；一來源像素窄框與部分超出畫布的邊界。

harness 建立空 Avalonia Application 並使用 `SetupWithoutStarting`，不建立 Avalonia Window，不啟動 `SnipFlow.App`／`MainWindow`，不發送 OS 鍵鼠事件或 routed events，不讀寫剪貼簿、不註冊全域快捷鍵、不安裝或部署。Enter／Escape 是直接呼叫 handler 的物件層測試。這不能證明真實雙擊、pointer capture、視窗排版、IME 或各桌面 DPI 的互動行為。

`SaveProject`／`RestoreProject` 使用產品原本的 checkpoint 機制：在 `SettingsStore.DataRoot/UpdateSessions` 短暫建立本次專用的 GUID 資料夾，完成後產品會清除；測試檢查既有 checkpoint 目錄清單保持不變。不修改使用者設定或既有專案。

## 舊版失敗證據

guard ID 是 `SNIPFLOW-IMAGE-CROP-001`。所有新增模型 API 都透過 reflection 取得，harness 可載入尚無 ImageCrop 的舊版 assembly；第一個 contract test 會明確失敗、產生結果並回傳 1，不會因為缺少方法而讓測試誤判通過。

先正常建置 harness，將其完整 output 複製到新的 ignored 驗證子目錄，再只把該副本的 `SnipFlow.dll` 換成已驗證的裁剪前 assembly。若舊版版本號低於 harness 編譯時的 assembly identity，應在隔離的舊 source 目錄以 `-p:Version=<目前版本>` 統一 metadata；記錄這個唯一 override，保留舊 product logic。不要把 binder 的 FileNotFoundException 當成裁剪回歸的有效紅測。執行副本及正常新版各一次、使用不同 `--output` 目錄，保留 exit code 與 `productAssemblySha256`。不要覆寫產品建置目錄、不要修改 repository 工作檔，也不要在其他工作正在 build 時重建共同輸出。
