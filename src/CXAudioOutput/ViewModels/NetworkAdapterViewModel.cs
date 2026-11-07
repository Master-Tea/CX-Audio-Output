using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CXAudioOutput.ViewModels;

public sealed class NetworkAdapterViewModel : INotifyPropertyChanged
{
    private readonly SolidColorBrush _upBrush = new(Color.FromArgb(255, 216, 255, 101));
    private readonly SolidColorBrush _downBrush = new(Color.FromArgb(255, 255, 116, 108));
    private readonly SolidColorBrush _dormantBrush = new(Color.FromArgb(255, 255, 193, 102));
    private readonly SolidColorBrush _upPill = new(Color.FromArgb(28, 216, 255, 101));
    private readonly SolidColorBrush _downPill = new(Color.FromArgb(28, 255, 116, 108));
    private readonly SolidColorBrush _dormantPill = new(Color.FromArgb(28, 255, 193, 102));

    private string _name = string.Empty;
    private string _description = string.Empty;
    private string _typeLabel = string.Empty;
    private string _statusText = string.Empty;
    private string _ipText = string.Empty;
    private string _speedText = string.Empty;
    private string _macText = string.Empty;
    private bool _isUp;
    private bool _isSelected;
    private bool _isVirtual;
    private Brush _statusBrush;
    private Brush _statusPillBrush;

    public NetworkAdapterViewModel()
    {
        _statusBrush = _downBrush;
        _statusPillBrush = _downPill;
    }

    public string Id { get; private set; } = string.Empty;
    public string Name { get => _name; private set => Set(ref _name, value); }
    public string Description { get => _description; private set => Set(ref _description, value); }
    public string TypeLabel { get => _typeLabel; private set => Set(ref _typeLabel, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string IpText { get => _ipText; private set => Set(ref _ipText, value); }
    public string SpeedText { get => _speedText; private set => Set(ref _speedText, value); }
    public string MacText { get => _macText; private set => Set(ref _macText, value); }
    public bool IsUp { get => _isUp; private set => Set(ref _isUp, value); }
    public bool IsSelected { get => _isSelected; private set => Set(ref _isSelected, value); }
    public bool IsVirtual { get => _isVirtual; private set => Set(ref _isVirtual, value); }
    public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }
    public Brush StatusPillBrush { get => _statusPillBrush; private set => Set(ref _statusPillBrush, value); }
    public Visibility VirtualVisibility => IsVirtual ? Visibility.Visible : Visibility.Collapsed;
    public string DescriptionLine => string.IsNullOrWhiteSpace(Description) ? Name : Description;

    public void Update(NetworkAdapterSnapshot snapshot, bool selected)
    {
        Id = snapshot.Id;
        Name = snapshot.Name;
        Description = snapshot.Description;
        TypeLabel = snapshot.TypeLabel;
        StatusText = snapshot.StatusText;
        IpText = snapshot.Ipv4Text;
        SpeedText = snapshot.SpeedText;
        MacText = snapshot.MacAddress;
        IsUp = snapshot.IsUp;
        IsVirtual = snapshot.IsVirtual;
        IsSelected = selected;

        if (snapshot.IsUp)
        {
            StatusBrush = _upBrush;
            StatusPillBrush = _upPill;
        }
        else if (snapshot.StatusText.Contains("休眠", StringComparison.Ordinal))
        {
            StatusBrush = _dormantBrush;
            StatusPillBrush = _dormantPill;
        }
        else
        {
            StatusBrush = _downBrush;
            StatusPillBrush = _downPill;
        }

        OnPropertyChanged(nameof(VirtualVisibility));
        OnPropertyChanged(nameof(DescriptionLine));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
