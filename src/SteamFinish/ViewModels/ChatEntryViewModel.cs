using SteamFinish.Core.Localization;
using SteamFinish.Core.Notifications;

namespace SteamFinish.ViewModels;

/// <summary>
/// One configured Telegram chat. The id is what actually gets messaged; the name and kind are looked
/// up for display and cached, so the list reads "Gaming Nights · group" instead of "-1001234567890".
/// </summary>
public sealed class ChatEntryViewModel(string chatId, string? label = null) : ObservableObject
{
    private string? _label = label;

    public string ChatId { get; } = chatId;

    /// <summary>"Name (kind)" once resolved, otherwise <c>null</c>.</summary>
    public string? Label
    {
        get => _label;
        private set
        {
            if (SetProperty(ref _label, value))
            {
                OnPropertyChanged(nameof(Title), nameof(Subtitle));
            }
        }
    }

    /// <summary>The chat name when it is known, falling back to the raw id.</summary>
    public string Title => string.IsNullOrWhiteSpace(_label) ? ChatId : _label;

    /// <summary>Always shows the id, so it stays verifiable even when a name is displayed.</summary>
    public string Subtitle =>
        string.IsNullOrWhiteSpace(_label) ? Loc.Get("Telegram.NotIdentified") : ChatId;

    public void Describe(DiscoveredChat chat) => Label = chat.Describe();

    public void Describe(string label) => Label = label;

    /// <summary>Re-reads the translated fallback after a language switch.</summary>
    public void RefreshLabel() => OnPropertyChanged(nameof(Title), nameof(Subtitle));
}
