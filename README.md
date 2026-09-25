![Tuxflix](assets/branding/tuxflix-banner.jpg)

# Tuxflix

The modern standard for viewing Plex content on Linux.

A native desktop client for Plex Media Server, built with .NET and Avalonia, laid out like a game
library: a rail of every title, shelves of artwork, and a page for each film and series.

## Status

In early development. Tuxflix signs in with Plex, opens your server, and plays films and episodes
straight from it, inside the window: hardware decoding, resume where you left off, progress kept
on the server, and the audio and subtitles the server chose for you; 4K HDR included, and a software
fallback where the GPU cannot share. Intros and credits the server found can be skipped with one
press (or on their own), the next episode is offered as the credits roll, chapters are listed with
their pictures, and a playback menu sets speed, picture fit and shape, subtitle size and timing,
night-time sound and a sleep timer. The server is asked how to play every film and episode, and a
lower quality (Plex's own table, from 20 Mbps down) has it convert the stream on the fly; switching
carries on from the same place. The seek bar shows the time and chapter under the pointer (and the
server's preview pictures when it makes them), picture in picture keeps a small window above the
others, playlists and film collections play through in order or shuffled, and the server's owner
can find subtitles online. The desktop's media controls and your keyboard's media keys
drive whatever plays. Every library opens as a grid you can sort
and filter the ways your server offers, with collections, playlists, a page for each actor and
director, and a search across everything from the title bar. Music plays too: artists,
albums and a gapless, loudness-levelled queue, and a compact player that wears classic Winamp skins
(`.wsz`), with its equalizer, balance, playlist and spectrum analyser working, at any size from the
original to double (drag its bottom right corner). The rest of the plan is still to come. A built-in demo library of invented titles, with artwork
drawn in code, shows the interface without a server.

Installed from a package, Tuxflix runs inside a hardened systemd user unit (see
`packaging/tuxflix-launcher.sh`): its own folders and your media folders are writable, everything
else read-only; `TUXFLIX_NO_SANDBOX=1` starts it without. The unit limits what the application can
damage, but it does not contain one that has been taken over: the session bus it needs for the
keyring, the media controls and the desktop portals also reaches the rest of the desktop.

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
