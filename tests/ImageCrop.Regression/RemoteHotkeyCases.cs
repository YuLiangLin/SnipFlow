using System.Text.Json;
using Avalonia.Input;
using SnipFlow.Services;

namespace SnipFlow.ImageCrop.Regression;

internal static partial class Program
{
    static readonly HotkeyGesture RemoteTestGesture = new(KeyModifiers.Control | KeyModifiers.Shift, Key.S);
    static readonly HotkeyGesture AlternateTestGesture = new(KeyModifiers.Control | KeyModifiers.Alt, Key.S);
    static Type HotkeyBindingType => Product.GetType("SnipFlow.Services.WindowsHotkeyBinding", true)!;

    static void RemoteHotkeyCases()
    {
        Case("known Windows remote clients match exact executable names", () =>
        {
            foreach (var name in new[] { "mRemoteNG.exe", "VNCVIEWER.EXE", "mstsc.exe", "msrdc.exe" })
                Check((bool)Static(HotkeyBindingType, "IsRemoteClient", name)!, $"Remote client was missed: {name}.");
            foreach (var name in new[] { "not-vncviewer.exe", "vncviewer.exe.bak", "explorer.exe", "SnipFlow.exe", "" })
                Check(!(bool)Static(HotkeyBindingType, "IsRemoteClient", name)!, $"Unrelated executable was matched: {name}.");
        });

        Case("focus in a remote window releases the local registration and discards stale presses", () =>
        {
            var hotkey = new HotkeyBindingFixture();
            Check(hotkey.Set(RemoteTestGesture, yielding: false) == 0, "Initial registration failed.");
            var firstId = hotkey.Registered.Single().Key;
            Check(hotkey.Accepts(firstId), "Initial shortcut is not active.");
            Check(hotkey.Focus(yielding: true) == 0 && hotkey.Registered.Count == 0, "Local shortcut is still reserved while a remote window has focus.");
            Check(hotkey.Paused && hotkey.Requested == RemoteTestGesture && !hotkey.Accepts(firstId), "Pause lost the preference or accepted an old message.");
            Check(hotkey.Focus(yielding: true) == 0 && hotkey.Registered.Count == 0, "Switching between remote viewers reserved the key locally.");
            Check(hotkey.Focus(yielding: false) == 0 && !hotkey.Paused && hotkey.Active == RemoteTestGesture, "Returning did not restore the shortcut.");
            var nextId = hotkey.Registered.Single().Key;
            Check(nextId != firstId && !hotkey.Accepts(firstId) && hotkey.Accepts(nextId), "A queued message from before the pause can still trigger capture.");
            Check(hotkey.Focus(yielding: false) == 0 && hotkey.Registered.Single().Key == nextId, "Ordinary local focus change recreated the shortcut.");
        });

        Case("startup and shortcut edits while a remote window has focus defer reservation", () =>
        {
            var hotkey = new HotkeyBindingFixture();
            Check(hotkey.Set(RemoteTestGesture, yielding: true) == 0, "Startup did not retain the shortcut preference.");
            Check(hotkey.Paused && hotkey.Registered.Count == 0, "Application startup took the key from the remote client.");
            Check(hotkey.Set(AlternateTestGesture, yielding: true) == 0 && hotkey.Registered.Count == 0, "Editing the chord took it from the remote client.");
            Check(hotkey.Focus(yielding: false) == 0 && hotkey.Registered.Single().Value == AlternateTestGesture,
                "Returning to a local window did not register the most recent choice.");
        });

        Case("a shortcut claimed while paused can be restored on a later focus change", () =>
        {
            var hotkey = new HotkeyBindingFixture();
            Check(hotkey.Set(RemoteTestGesture, yielding: true) == 0, "Could not start paused.");
            hotkey.InUse = RemoteTestGesture;
            Check(hotkey.Focus(yielding: false) == GlobalHotkeyService.ErrorInUse, "A resume conflict was not surfaced.");
            Check(hotkey.Active is null && !hotkey.Paused && hotkey.Requested == RemoteTestGesture, "A resume conflict corrupted shortcut state.");
            hotkey.InUse = null;
            Check(hotkey.Focus(yielding: false) == 0 && hotkey.Registered.Single().Value == RemoteTestGesture, "Resume did not retry the saved chord.");
        });

        Case("failed hotkey replacement keeps the previous registration", () =>
        {
            var hotkey = new HotkeyBindingFixture();
            Check(hotkey.Set(RemoteTestGesture, yielding: false) == 0, "Initial shortcut failed.");
            var id = hotkey.Registered.Single().Key;
            hotkey.InUse = AlternateTestGesture;
            Check(hotkey.Set(AlternateTestGesture, yielding: false) == GlobalHotkeyService.ErrorInUse, "The registration error was swallowed.");
            Check(hotkey.Accepts(id) && hotkey.Active == RemoteTestGesture && hotkey.Requested == RemoteTestGesture,
                "The previous shortcut was lost after a replacement failed.");
        });

        Case("the remote shortcut preference defaults on for old settings and remembers opt-out", () =>
        {
            var path = Path.Combine(Output, "remote-hotkey-settings.json");
            var shortcut = RemoteTestGesture.ToString();
            File.WriteAllText(path, JsonSerializer.Serialize(new { Hotkey = shortcut, CopyAfterCapture = false }));
            var settings = ReadPreferences(path);
            const string preference = "PreferRemoteDesktopHotkeys";
            Check(Get(settings, preference) is true, "Existing installations did not get the remote-window behavior.");
            Set(settings, preference, false);
            WritePreferences(path, settings);
            var saved = ReadPreferences(path);
            Check(Get(saved, preference) is false && Equals(Get(saved, "Hotkey"), shortcut) && Get(saved, "CopyAfterCapture") is false,
                "Writing the remote shortcut preference changed an existing setting or lost the opt-out.");
        });
    }

    sealed class HotkeyBindingFixture
    {
        readonly object binding;
        internal readonly Dictionary<int, HotkeyGesture> Registered = new();
        internal HotkeyGesture? InUse;
        internal bool Paused => (bool)Get(binding, "IsPaused")!;
        internal HotkeyGesture? Active => (HotkeyGesture?)Get(binding, "ActiveGesture");
        internal HotkeyGesture? Requested => (HotkeyGesture?)Get(binding, "RequestedGesture");

        internal HotkeyBindingFixture()
        {
            Func<int, HotkeyGesture, int> register = (id, gesture) =>
            {
                if (InUse == gesture || Registered.ContainsValue(gesture)) return GlobalHotkeyService.ErrorInUse;
                Registered.Add(id, gesture);
                return 0;
            };
            Func<int, int> unregister = id => Registered.Remove(id) ? 0 : 1419;
            binding = Activator.CreateInstance(HotkeyBindingType, Any, binder: null, [register, unregister], culture: null)!;
        }

        internal int Set(HotkeyGesture gesture, bool yielding)
        {
            object[] arguments = [gesture, yielding, 0];
            var success = (bool)HotkeyBindingType.GetMethod("TrySetGesture", Any)!.Invoke(binding, arguments)!;
            var error = (int)arguments[2];
            Check(success == (error == 0), "The result disagrees with the registration error.");
            return error;
        }

        internal int Focus(bool yielding) => (int)Call(binding, "ForegroundChanged", yielding)!;
        internal bool Accepts(int id) => (bool)Call(binding, "Accepts", id)!;
    }
}
