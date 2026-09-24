![Tuxflix](assets/branding/tuxflix-banner.jpg)

# Tuxflix

The modern standard for viewing Plex content on Linux.

A native desktop client for Plex Media Server, built with .NET and Avalonia, laid out like a game
library: a rail of every title, shelves of artwork, and a page for each film and series.

## Status

In early development. Tuxflix signs in with Plex, opens your server, and plays films and episodes
straight from it, inside the window: hardware decoding, resume where you left off, progress kept
on the server, and the audio and subtitles the server chose for you. Transcoding, library grids,
filters and search are still to come. A built-in demo library of invented titles, with artwork
drawn in code, shows the interface without a server.

## Building

Requires the .NET 10 SDK. Playback needs libmpv, which comes with the mpv package on most
distributions.

```
dotnet build Tuxflix.slnx -c Release
src/Tuxflix.App/bin/Release/net10.0/tuxflix --demo
```

`--portable DIR` keeps settings, cache and logs in `DIR` instead of the XDG folders.

## Third-party material

See [NOTICES.txt](NOTICES.txt).
