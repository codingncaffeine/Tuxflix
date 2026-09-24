![Tuxflix](assets/branding/tuxflix-banner.jpg)

# Tuxflix

The modern standard for viewing Plex content on Linux.

A native desktop client for Plex Media Server, built with .NET and Avalonia, laid out like a game
library: a rail of every title, shelves of artwork, and a page for each film and series.

## Status

In early development. Tuxflix signs in with Plex, opens your server, and plays films and episodes
straight from it, inside the window: hardware decoding, resume where you left off, progress kept
on the server, and the audio and subtitles the server chose for you. Music plays too: artists,
albums and a gapless, loudness-levelled queue, and a compact player that wears classic Winamp skins
(`.wsz`), with its equalizer, balance, playlist and spectrum analyser working. Transcoding, library
grids, filters and search are still to come. A built-in demo library of invented titles, with artwork
drawn in code, shows the interface without a server.

## Building

Requires the .NET 10 SDK. Playback needs libmpv, which comes with the mpv package on most
distributions.

```
dotnet build Tuxflix.slnx -c Release
src/Tuxflix.App/bin/Release/net10.0/tuxflix --demo
```

`--portable DIR` keeps settings, cache and logs in `DIR` instead of the XDG folders.

`tools/install-desktop-entry.sh` adds a desktop entry and icons for the build to `~/.local/share`,
so the taskbar, the window switcher and the application menu show Tuxflix's name and icon;
`--remove` takes them out again.

## Licence

Tuxflix is free software: you can redistribute it and modify it under the terms of the
[GNU General Public License](LICENSE), version 3 or (at your option) any later version. It comes
with no warranty.

Third-party material keeps its own licence; see [NOTICES.txt](NOTICES.txt).
