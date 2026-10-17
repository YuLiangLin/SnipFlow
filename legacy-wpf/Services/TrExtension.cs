using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;

namespace SnipFlow.Services;

/// <summary>Live translation for fixed XAML labels; document text never passes through this binding.</summary>
public sealed class TrExtension : MarkupExtension
{
    public string Key { get; set; }
    public TrExtension(string key) => Key = key;
    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding($"[{Key}]") { Source = Labels.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);

    sealed class Labels : INotifyPropertyChanged
    {
        public static Labels Instance { get; } = new();
        public string this[string key] => I18n.T(key);
        public event PropertyChangedEventHandler? PropertyChanged;
        Labels() => I18n.Changed += (_, _) =>
        {
            void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(Refresh);
            else Refresh();
        };
    }
}
