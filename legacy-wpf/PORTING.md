# WPF → Avalonia porting guide (internal, deleted after the port)

`legacy-wpf/` holds the original WPF sources for reference. They are not compiled. Ported code goes to
the same relative path under `src/` (e.g. `legacy-wpf/Editor/EditorView.cs` → `src/Editor/EditorView.cs`).
Keep behavior, comments, naming and code style of the original — this is a port, not a redesign.
Keep public/internal member names and shapes so other modules can keep calling them; only substitute types.

## Build & run
- SDK: `export PATH=/root/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1`
- Linux: `dotnet build src/SnipFlow.csproj -f net10.0`
- Windows (compile check works on Linux): `dotnet build src/SnipFlow.csproj -f net10.0-windows10.0.19041.0`
- Both targets must compile. Errors in other modules' files that are not ported yet are expected — only fix your own.
- GUI tests: `Xvfb :N -screen 0 1600x1000x24 &` then `DISPLAY=:N dotnet src/bin/Debug/net10.0/SnipFlow.dll`,
  screenshot with `import -window root out.png` (ImageMagick). Use your own display number.
- Full public Avalonia 12.1.3 API list (grep it, don't guess): `/tmp/claude-0/-home-user-SnipFlow/1b96a968-778c-5d92-aa49-747fb77c856b/scratchpad/avalonia-12.1.3-api.txt`
  (format: `class Full.Name : Base` followed by `  prop/field/event/method` lines).
- Tmds.DBus.Protocol API: `.../scratchpad/tmds-dbus-api.txt` (same directory).

## Platform split
- Target frameworks: `net10.0` = Linux, `net10.0-windows10.0.19041.0` = Windows (defines `WINDOWS`; Linux defines `LINUX`).
- Platform files: `Platform/Windows/**` and `*.Windows.cs` compile only for Windows; `Platform/Linux/**` and
  `*.Linux.cs` compile only for Linux. Prefer partial classes/partial methods split across `X.cs` / `X.Windows.cs` /
  `X.Linux.cs` over `#if`. Small `#if WINDOWS` blocks are fine.
- WinRT APIs (Windows.Media.Ocr, Windows.Graphics.Capture, Windows.Media.Editing…) and ScreenRecorderLib, Win32
  P/Invoke: Windows target only. Keep the original Windows behavior intact.
- `PlatformInfo.IsWindows / IsLinux / IsWayland / IsX11 / IsGnome / PathComparison / PathComparer`.
- Linux session model: SnipFlow's own windows always run on X11 (XWayland in Wayland sessions). In a **Wayland**
  session, other apps' pixels/windows and global key grabs are NOT reachable through X11 — use xdg-desktop-portal
  (`Platform/Linux/Portal/DesktopPortal.cs`: `RequestAsync`, `CallAsync`, `TryGetPropertyAsync`, `LocalPath`).
  In an **X11** session use Xlib (`Platform/Linux/X11/XLib.cs`, `XDisplay.TryOpen()`); add declarations in your own
  partial file `Platform/Linux/X11/XLib.<Area>.cs`. Never call XSetErrorHandler (Avalonia's non-fatal one is installed).
- CLI tools (ffmpeg, ffprobe, tesseract, gsettings, gst-launch-1.0…): `ExternalTool.Find / IsAvailable / RunAsync /
  RunBinaryAsync / CreateStartInfo`. Missing tools must produce a translated, actionable message
  (e.g. "請安裝 ffmpeg：sudo apt install ffmpeg"), never a crash.
- Data paths: `SettingsStore.DataRoot` (Windows `%LocalAppData%\SnipFlowData`, Linux `~/.local/share/SnipFlow`),
  `LinuxPaths.DataHome/ConfigHome/CacheHome/RuntimeDirectory`. Compare paths with `PlatformInfo.PathComparison`.

## Foundation you can use (do not modify; report needed changes instead)
- `Ui/Modal.cs`: `Modal.ShowDialog(window, owner)` (blocking, nested dispatcher frame, like WPF ShowDialog),
  `Modal.Wait(task)`, `Modal.DefaultOwner()`. `DialogWindow : Window` adds WPF `DialogResult` (setting it closes)
  and `ShowModal(owner)`. Windows that used `ShowDialog()`/`DialogResult` derive from `DialogWindow`.
- `Ui/FileDialogs.cs`: `OpenFiles/OpenFile/SaveFile/PickFolder(owner, title, wpfFilter, ...)` — blocking, accepts
  WPF filter strings `"PNG|*.png|All|*.*"`.
- `Services/AppDialog.cs`: `MessageBox.Show(owner, text, caption, MessageBoxButton.YesNo, MessageBoxImage.Question)`
  (enums `MessageBoxButton/MessageBoxResult/MessageBoxImage` live in `SnipFlow.Ui`).
- `Ui/UiThemes.cs`: `control.WithTheme("Primary" | "QuietButton" | "NavigationButton" | "WindowControl" |
  "ColorSwatch" | "ToggleSwitch")` replaces `Style = FindResource("…")`; `UiThemes.AppFont`, `UiThemes.MonoFont`
  (use instead of "Segoe UI"/"Consolas"). `WindowChrome.EnableTitleBarDrag(window, titleBar)` and
  `WindowChrome.EnableResizeBorder(window)` for frameless windows.
- `Services/ThemeService.cs`: `ThemeService.Brush("Panel")` returns the shared brush whose color changes with the
  theme (keys: Bg Panel Raised Line Ink Muted Accent AccentInk Hover Selected Soft PrimaryHover PrimaryPressed
  TextSelection ControlBorder SwitchOff ScrollGrip* TrackFilled TrackEmpty Error Warning SelectionBorder
  SelectionHandle SelectionFill Badge OverlayPanel OverlayInk OverlayMuted OverlayLine). `ThemeService.GetColor(key)`,
  `IsDark`, `Changed`. (Old `ThemeService.Colors[key]` → `GetColor(key)`.)
- `Ui/Icons.cs`: `Icons.Create(IconKind.X, size)` / `Icons.WithText(...)` replace Segoe Fluent Icons glyphs
  (E710 Add, E70D ChevronDown, E722 Camera, E8CB ScrollCapture, E714 Video, E8E5 FolderOpen, E77F ClipboardPaste,
  E8A9 Grid, E713 Settings, E7E8 Power, E7C9 Cursor, E70F Edit, E7E6 Highlight, E74D Delete, E8BB Dismiss,
  E921 Subtract, E922 Maximize, E895 Sync, E765 Keyboard, EB9F Image; also Play Pause Stop Record Mic Speaker Copy
  Save Window Desktop Code TextT). Need another icon? Report it.
- `Services/TrExtension.cs`: XAML `{ui:Tr '文字'}`; code `Tr.Bind(control, TextBlock.TextProperty, "文字")`.
- `Services/I18n.cs`: `I18n.T/F`, `I18n.Changed`. Add new strings in your own file `Services/I18n.<Area>.cs`:
  ```csharp
  public static partial class I18n {
      static readonly Dictionary<string,(string English,string Simplified)> AreaTranslations = new(StringComparer.Ordinal) { ["中文"] = ("English", "简体") };
      [System.Runtime.CompilerServices.ModuleInitializer] internal static void RegisterArea() => Register(AreaTranslations);
  }
  ```
  Every user-visible string goes through I18n with zh-TW source text + English + Simplified Chinese.
- `Imaging/ImageTools.cs` + `Imaging/PixelBuffer.cs`: `Bitmap` is the image type (replaces BitmapSource). All bitmaps are
  96 DPI so 1 px = 1 DIP. `ImageTools.Load/Decode/LoadThumbnail/EncodePng/SavePng/Crop/Render(size, draw)/Render(visual,size)/
  Scale/Fit/FromBgra/Solid`. `PixelBuffer.FromBitmap(bmp)` gives BGRA premultiplied bytes (stride = Width*4),
  `.ToBitmap()`, `.Crop`, `.Blit`. Bitmaps are thread-agile (no Freeze needed; drop `.Freeze()` calls).
- `Services/SettingsStore.cs`, `HistoryStore.cs` (`HistoryItem.Thumbnail` is `Bitmap?`, `Read(path)` → `Bitmap`),
  `CaptureFileStore.cs`, `StartupService.cs`, `HotkeyGesture.cs` (Avalonia `KeyModifiers`/`Key`; `.VirtualKey`,
  `.X11KeySym`, `.GtkAccelerator`), `WindowAppearance.Apply(window)`, `WindowBoundsService.GetWorkArea(window)` (DIPs),
  `GetWorkAreaPixels`, `GetScaling`, `Attach`.
- `Platform/ExternalTool.cs`, `Platform/PlatformInfo.cs`, `Platform/Linux/X11/XLib.cs`, `Platform/Linux/Portal/DesktopPortal.cs`.
- Global usings (`GlobalUsings.cs`): System.*, Avalonia, Avalonia.Controls/.Interactivity/.Layout/.Media/.Media.Imaging/
  .Threading, SnipFlow.Imaging/.Platform/.Services/.Ui, `MessageBox` alias.

## API mapping cheat sheet
| WPF | Avalonia |
|---|---|
| `Visibility = Collapsed/Visible` | `IsVisible = false/true` |
| `Dispatcher.BeginInvoke(a)` / `Dispatcher.InvokeAsync` / `CheckAccess` | `Dispatcher.UIThread.Post(a)` / `.InvokeAsync` / `.CheckAccess()` |
| `DispatcherTimer`, `Dispatcher.Yield(DispatcherPriority.Render)` | same type in `Avalonia.Threading`; `await Dispatcher.UIThread.InvokeAsync(() => {}, DispatcherPriority.Render)` |
| `UserControl/FrameworkElement.OnRender(dc)` | `Control.Render(DrawingContext)` override; `InvalidateVisual()` |
| `MouseLeftButtonDown/MouseMove/MouseUp/MouseWheel` | `PointerPressed/PointerMoved/PointerReleased/PointerWheelChanged`; `e.GetCurrentPoint(c).Properties.IsLeftButtonPressed`, `e.InitialPressMouseButton` on release, `e.ClickCount`, `e.Delta.Y` |
| `Preview*` events | `AddHandler(XEvent, handler, RoutingStrategies.Tunnel)` |
| `CaptureMouse()/ReleaseMouseCapture()` | `e.Pointer.Capture(control)` / `e.Pointer.Capture(null)` (keep the IPointer) |
| `Keyboard.Modifiers` | `e.KeyModifiers` (KeyModifiers.Control/Shift/Alt/Meta) |
| `Keyboard.FocusedElement` | `TopLevel.GetTopLevel(c)?.FocusManager?.GetFocusedElement()` |
| `IsKeyboardFocusWithin` | `IsKeyboardFocusWithin` (exists) |
| `Focusable`, `Focus()` | same |
| `Cursor = Cursors.Cross` | `Cursor = new Cursor(StandardCursorType.Cross)` |
| `ToolTip = "x"` | `ToolTip.SetTip(control, "x")` |
| `AutomationProperties.Name` | `Avalonia.Automation.AutomationProperties.SetName` |
| `Grid.RowDefinitions.Add(...)` | `new RowDefinitions("Auto,*,26")` or `.Add(new RowDefinition(GridLength.Auto))` |
| `ColorConverter.ConvertFromString` | `Color.Parse`, `Brush.Parse` |
| `FormattedText(text, culture, flow, typeface, size, brush, pixelsPerDip)` | `new FormattedText(text, culture, flow, typeface, size, brush)` |
| `Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal)` | `new Typeface(family, FontStyle.Normal, FontWeight.SemiBold)` |
| `dc.PushTransform(t); … dc.Pop()` | `using (dc.PushTransform(matrix)) { … }` (also PushClip/PushOpacity) |
| `Pen(brush, w) { StartLineCap… }` | `new Pen(brush, w, dashStyle, PenLineCap.Round, PenLineJoin.Round)` |
| `BitmapSource`, `PixelWidth` | `Bitmap`, `PixelSize.Width` |
| `RenderTargetBitmap(w,h,96,96,Pbgra32).Render(visual)` | `ImageTools.Render(visual, size)` / `ImageTools.Render(size, dc => …)` |
| `CroppedBitmap`, `FormatConvertedBitmap`, `CopyPixels` | `ImageTools.Crop`, `PixelBuffer.FromBitmap` |
| `Window.ShowDialog()` + `DialogResult` | derive from `DialogWindow`; `ShowModal(owner)` |
| `Owner = x` | pass owner to `ShowModal(owner)` / `Modal.ShowDialog(w, owner)`, or `Show(owner)` for modeless owned windows |
| `WindowStyle=None` + `WindowChrome` | `WindowDecorations = WindowDecorations.None` + `WindowChrome.EnableTitleBarDrag/EnableResizeBorder` |
| `ResizeMode=NoResize/CanMinimize` | `CanResize=false`, `CanMinimize`, `CanMaximize` |
| `AllowsTransparency=true` | `TransparencyLevelHint = [WindowTransparencyLevel.Transparent]; Background = Brushes.Transparent` (check `ActualTransparencyLevel`; X11 without compositor has none) |
| `Left/Top` | `Position` (`PixelPoint`, **physical pixels**); `Width/Height` are DIPs (divide physical by `RenderScaling`/screen scaling) |
| `SourceInitialized`, `WindowInteropHelper.Handle` | `Opened`; `TryGetPlatformHandle()?.Handle` (HWND on Windows, X11 window id on Linux) |
| `ContentRendered` | `Opened` + `Dispatcher.UIThread.Post(..., DispatcherPriority.Background)` |
| `Closing (CancelEventArgs)` | `Closing (WindowClosingEventArgs, e.Cancel)` |
| `SystemParameters.WorkArea/VirtualScreen*` | `window.Screens.All` (`Bounds`, `WorkingArea` in pixels, `Scaling`) |
| `Popup { StaysOpen=false, Placement=Bottom }` | `Avalonia.Controls.Primitives.Popup { IsLightDismissEnabled = true, Placement = PlacementMode.Bottom, PlacementTarget = … }` |
| `ContextMenu { PlacementTarget, IsOpen=true }` | `new ContextMenu { ItemsSource/Items… }; menu.Open(control)`; `MenuItem.Header`, `Separator` |
| `ItemTemplate DataTemplate` | `new FuncDataTemplate<T>((item, _) => control)` |
| `TextTrimming.CharacterEllipsis` | same |
| `Clipboard.SetImage/GetImage/ContainsText/GetText` | `TopLevel.GetTopLevel(c)!.Clipboard!` + `SetBitmapAsync`, `TryGetBitmapAsync`, `TryGetTextAsync`, `TryGetFilesAsync`, `GetDataFormatsAsync` (`Avalonia.Input.Platform.ClipboardExtensions`) |
| `DragDrop` / `DataObject` / `DataFormats.FileDrop` | `DragDrop.SetAllowDrop(c, true)`, `AddHandler(DragDrop.DragOverEvent/DropEvent, …)`, `e.DataTransfer.TryGetFiles()`, `DragDrop.DoDragDropAsync(pointerPressedArgs, dataTransfer, effects)`; custom formats via `DataFormat.CreateInProcessFormat<T>(id)` |
| `MediaElement` | none built in — see the video module |
| `Microsoft.Win32.OpenFileDialog/SaveFileDialog` | `FileDialogs.*` |
| `Application.Current.Shutdown()` | `((IClassicDesktopStyleApplicationLifetime)Application.Current.ApplicationLifetime).Shutdown()` |

## Linux UX conventions
- Overlays that must cover a whole monitor (selection, boundary pick): one frameless window per monitor, positioned on
  that monitor then `WindowState = WindowState.FullScreen`, `Topmost = true`, `ShowInTaskbar = false`. GNOME keeps
  non-fullscreen windows below its top bar.
- There is no capture exclusion on Linux (no `SetWindowDisplayAffinity`): place toolbars and frames **outside** the
  captured/recorded rectangle instead.
- Keep Windows-specific wording out of Linux UI ("Windows OCR language pack", "登入 Windows 時啟動"): pick a neutral
  or Linux-specific translated string on Linux.
- Never block the UI thread on long work; use async/Task.Run like the original.

## Reporting
Commit your work in your worktree (`git add -A && git commit`). In your final message list: files ported, new
files, anything you could not port or verify, any foundation change you needed (with the exact diff/reason),
cross-module API changes callers must adapt to, and Linux runtime dependencies (apt packages).
