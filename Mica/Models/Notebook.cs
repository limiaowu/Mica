using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Mica.Models;

// A notebook = one folder the user works in. The list of notebooks plus which one
// is active is persisted in settings.json (see SettingsService). NoteCount is a
// transient UI-only value computed when the home page is shown, not persisted.
public sealed class Notebook : INotifyPropertyChanged
{
    public required string Id { get; set; }

    private string _name = "";
    public required string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnChanged(nameof(Name)); } }
    }

    public required string Path { get; set; }

    // Free-form description shown (truncated) on the card and in full on the
    // detail page. Persisted.
    private string _description = "";
    public string Description
    {
        get => _description;
        set
        {
            if (_description != value)
            {
                _description = value;
                OnChanged(nameof(Description));
                OnChanged(nameof(ShortDescription));
                OnChanged(nameof(HasDescription));
            }
        }
    }

    // Optional absolute path to a cover image. When empty the card/detail page
    // falls back to the initial-letter placeholder. Persisted.
    private string? _coverImagePath;
    public string? CoverImagePath
    {
        get => _coverImagePath;
        set
        {
            if (_coverImagePath != value)
            {
                _coverImagePath = value;
                OnChanged(nameof(CoverImagePath));
                OnChanged(nameof(HasCover));
                OnChanged(nameof(CoverImage));
                OnChanged(nameof(CoverVisibility));
                OnChanged(nameof(PlaceholderVisibility));
            }
        }
    }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastOpened { get; set; }

    // Relative paths (within this notebook's folder) of every note open in a tab when
    // the notebook was last left, in tab order. Restored on launch when "恢复上次笔记本"
    // is on. ActiveFile is the one that was selected.
    public List<string> OpenFiles { get; set; } = [];
    public string? ActiveFile { get; set; }

    private int _noteCount;
    [JsonIgnore]
    public int NoteCount
    {
        get => _noteCount;
        set { if (_noteCount != value) { _noteCount = value; OnChanged(nameof(NoteCount)); OnChanged(nameof(CountText)); } }
    }

    // Friendly first letter for the placeholder cover until a real cover lands.
    [JsonIgnore]
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name.Substring(0, 1).ToUpperInvariant();

    [JsonIgnore]
    public string CountText => $"{NoteCount} 篇笔记";

    [JsonIgnore]
    public bool HasCover => !string.IsNullOrWhiteSpace(CoverImagePath) && File.Exists(CoverImagePath);

    // The cover bitmap (or null → the initial-letter placeholder shows instead).
    [JsonIgnore]
    public ImageSource? CoverImage => HasCover ? new BitmapImage(new Uri(CoverImagePath!)) : null;

    [JsonIgnore]
    public Visibility CoverVisibility => HasCover ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public Visibility PlaceholderVisibility => HasCover ? Visibility.Collapsed : Visibility.Visible;

    [JsonIgnore]
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    // Card-friendly one-liner. The card itself trims with an ellipsis, so we just
    // hand over the text (or a placeholder when empty).
    [JsonIgnore]
    public string ShortDescription => string.IsNullOrWhiteSpace(Description) ? "暂无简介" : Description;

    [JsonIgnore]
    public string CreatedText => CreatedAt == default ? "" : $"创建于 {CreatedAt.LocalDateTime:yyyy-MM-dd}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged(string prop) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
