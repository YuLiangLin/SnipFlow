using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace SnipFlow.Services;

/// <summary>
/// Live translation for fixed labels: <c>Text="{ui:Tr '框選截圖'}"</c> in XAML, or
/// <see cref="Tr.Bind"/> from code. Document text never passes through this binding.
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public string Key { get; set; }
    public TrExtension(string key) => Key = key;
    public override object ProvideValue(IServiceProvider serviceProvider) => Tr.Observe(Key).ToBinding();
}

public static class Tr
{
    /// <summary>Emits the current translation of <paramref name="key"/>, then again after each language change.</summary>
    public static IObservable<string> Observe(string key) => new TranslationObservable(key);

    /// <summary>Binds a string property, such as <c>TextBlock.TextProperty</c>, to a live translation.</summary>
    public static IDisposable Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target.Bind(property, Observe(key).ToBinding());

    sealed class TranslationObservable(string key) : IObservable<string>
    {
        public IDisposable Subscribe(IObserver<string> observer)
        {
            observer.OnNext(I18n.T(key));
            void Changed(object? sender, EventArgs e)
            {
                if (Dispatcher.UIThread.CheckAccess()) observer.OnNext(I18n.T(key));
                else Dispatcher.UIThread.Post(() => observer.OnNext(I18n.T(key)));
            }
            I18n.Changed += Changed;
            return new Subscription(() => I18n.Changed -= Changed);
        }
    }

    sealed class Subscription(Action dispose) : IDisposable
    {
        Action? dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
    }
}
