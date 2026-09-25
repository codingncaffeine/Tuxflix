using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>Subtitles found online: who may look for them, and turning on the one kept.</summary>
public sealed partial class PlayerPageViewModel : ISubtitleHost
{
    private SubtitleFinderViewModel? _finder;

    /// <summary>The server's owner may look for subtitles online and keep one with the item.</summary>
    public bool CanFindSubtitles => session.IsOwner && _part is not null && !IsConverting;

    /// <summary>The finder for this item, made the first time it is opened.</summary>
    public SubtitleFinderViewModel Finder => _finder ??= new SubtitleFinderViewModel(this, session, _item.RatingKey);

    /// <summary>The subtitle streams the item has now, by id.</summary>
    public HashSet<long> SubtitleStreamIds() => [.. (_part?.Stream ?? []).Where(s => s.StreamType == StreamChoice.Subtitle).Select(s => s.Id)];

    /// <summary>A subtitle the server now keeps with the item: the item is read again, and the subtitle loaded and chosen.</summary>
    public void TakeAddedSubtitle(MetadataItem item, MediaPart part, MediaStream added)
    {
        _item = item;
        _part = part;
        _subtitleStreamId = added.Id;
        if (Player is { } shared) LoadSubtitle(shared.Player, added);
        Remember(new TrackOption(this, "sid", string.Empty, added.DisplayTitle ?? "Subtitles", true, added));
    }
}
