using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CXAudioOutput.ViewModels;

public sealed class ReceiverViewModel : INotifyPropertyChanged
{
    private readonly SolidColorBrush _playingBrush = new(Color.FromArgb(255, 216, 255, 101));
    private readonly SolidColorBrush _bufferingBrush = new(Color.FromArgb(255, 255, 193, 102));
    private readonly SolidColorBrush _mutedBrush = new(Color.FromArgb(255, 114, 123, 111));

    private string _name = string.Empty;
    private string _meta = string.Empty;
    private string _playbackText = string.Empty;
    private string _bufferText = string.Empty;
    private string _rttText = string.Empty;
    private string _volumeText = string.Empty;
    private string _healthText = "稳定";
    private string _initial = "?";
    private string _playback = "buffering";
    private int _volume = 100;
    private bool _muted;
    private Brush _statusBrush;
    private Brush _healthBrush;

    public ReceiverViewModel()
    {
        _statusBrush = _bufferingBrush;
        _healthBrush = _playingBrush;
    }

    public string Id { get; private set; } = string.Empty;
    public string Name { get => _name; private set => Set(ref _name, value); }
    public string Meta { get => _meta; private set => Set(ref _meta, value); }
    public string PlaybackText { get => _playbackText; private set => Set(ref _playbackText, value); }
    public string BufferText { get => _bufferText; private set => Set(ref _bufferText, value); }
    public string RttText { get => _rttText; private set => Set(ref _rttText, value); }
    public string VolumeText { get => _volumeText; private set => Set(ref _volumeText, value); }
    public string HealthText { get => _healthText; private set => Set(ref _healthText, value); }
    public string Initial { get => _initial; private set => Set(ref _initial, value); }
    public string Playback { get => _playback; private set => Set(ref _playback, value); }
    public int Volume { get => _volume; private set => Set(ref _volume, value); }
    public bool Muted { get => _muted; private set => Set(ref _muted, value); }
    public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }
    public Brush HealthBrush { get => _healthBrush; private set => Set(ref _healthBrush, value); }
    public Visibility MutedVisibility => Muted ? Visibility.Visible : Visibility.Collapsed;

    public void Update(ReceiverSnapshot snapshot)
    {
        Id = snapshot.Id;
        Name = snapshot.Name;
        Meta = snapshot.Platform + " · " + snapshot.Address;
        Playback = snapshot.Playback;
        PlaybackText = snapshot.Playback switch
        {
            "playing" => "播放中",
            "paused" => "已暂停",
            _ => "缓冲中"
        };
        BufferText = snapshot.BufferMs > 0 ? snapshot.BufferMs + " ms" : "—";
        RttText = snapshot.RttMs > 0 ? snapshot.RttMs + " ms" : "—";
        Volume = snapshot.Volume;
        VolumeText = snapshot.Volume + "%";
        HealthText = snapshot.Underruns > 0 ? "欠载 " + snapshot.Underruns : "稳定";
        HealthBrush = snapshot.Underruns > 0 ? _bufferingBrush : _playingBrush;
        Muted = snapshot.Muted;
        Initial = FirstGlyph(snapshot.Name);
        StatusBrush = snapshot.Playback switch
        {
            "playing" => _playingBrush,
            "paused" => _mutedBrush,
            _ => _bufferingBrush
        };

        OnPropertyChanged(nameof(MutedVisibility));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string FirstGlyph(string value)
    {
        var chars = value.Trim().ToCharArray();
        return chars.Length == 0 ? "?" : chars[0].ToString();
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
