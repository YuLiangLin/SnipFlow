# SnipFlow · 快剪

Windows 截圖與標註工具，以 C# / WPF 開發。這是預覽版，目標是讓截圖、標註與文字辨識在同一個工作區完成。

## 使用

從 [GitHub Releases](https://github.com/YuLiangLin/SnipFlow/releases) 下載 `SnipFlow-win-Setup.exe`。安裝版支援自動更新；直接從 ZIP 或 build 資料夾執行的可攜版不會自行安裝更新。

- `Ctrl + Alt + S`：全域框選截圖。可在設定改為 `Ctrl + Shift + S` 或 `Alt + Shift + S`。
- 框選時 `Esc`／右鍵取消；支援多螢幕與跨螢幕範圍。
- `Ctrl + N`：新截圖，`Ctrl + Shift + N`：長頁截圖，`Ctrl + O`：開啟圖片。
- `Ctrl + C`／`Ctrl + S`：複製／存成 PNG，`Ctrl + V`：貼上圖片。
- `Ctrl + Z`／`Ctrl + Y`：復原／重做。
- `V / A / R / E / P / H / T / M`：選取、箭頭、矩形、橢圓、畫筆、螢光筆、文字、馬賽克。
- 滾輪縮放；中鍵或空白鍵拖曳移動畫布。
- 關閉主視窗後留在系統匣；使用系統匣選單或左下角的結束按鈕退出。
- 再次啟動會喚起既有視窗，同一個 Windows 工作階段只保留一個實例。

## OCR 與長頁截圖

OCR 使用本機 `Windows.Media.Ocr` 與 Windows 已安裝的語言套件。沒有安裝對應語言時會提示；截圖與文字不會上傳到 GitHub 或其他辨識服務。

長頁截圖目前採 **手動捲動、加入畫面、重疊對齊後拼接**。請圈選只會捲動的內容區，保持視窗大小與位置固定；每次捲動保留至少約八分之一畫面的重疊。低信心時需要手動調整並查看接縫。固定表頭、動畫與重複段落可能干擾對齊。上限為 20 張、輸出 40 百萬像素。

## 更新

Velopack 從本專案的公開 GitHub Releases 取得版本與套件。預設在啟動時與每 4 小時檢查，找到新版後背景下載；下次啟動會套用，也能在介面按「重新啟動更新」。更新前若有未儲存的標註會提醒先存檔。離線或下載失敗不會阻止使用目前版本。設定頁可關閉自動檢查／下載。

設定、最多 80 張截圖歷史與錯誤紀錄放在 `%LocalAppData%\SnipFlowData`，與會被更新取代的程式目錄分開。停用歷史只會停止記錄新的截圖。截圖、OCR 內容與紀錄都不會由發布腳本上傳。

預覽版尚未使用程式碼簽章；正式公開散布前應配置受信任的 Windows 簽章憑證。更新下載由 Velopack 檢查套件雜湊，更新來源固定為本專案的 HTTPS GitHub URL。

## 開發

需要 Windows 10 2004+ / Windows 11，以及 .NET 10 SDK。

```powershell
dotnet build src/SnipFlow.csproj -c Release
dotnet run --project src/SnipFlow.csproj
```

`build-release.ps1` 建立自含執行環境的 Windows x64 發布目錄與 Velopack 安裝包。它只打包本專案程式檔案，不打包使用者資料。`-Version` 決定應用程式與套件版本。產物在 `release/`；發布到 GitHub 後，已安裝的舊版才能取得更新。

```powershell
./build-release.ps1 -Version 0.2.0
```

## 驗證範圍

建置與預覽版功能的實際驗證結果會記錄在每次 Release 說明。多螢幕混合 DPI、各種網站的長頁拼接與 OCR 語言辨識仍需要依使用情境驗證。
