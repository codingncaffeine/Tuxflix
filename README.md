![Tuxflix](assets/branding/tuxflix-banner.jpg)

# Tuxflix

The modern standard for viewing Plex content on Linux.

A native desktop client for Plex Media Server, built with .NET and Avalonia, laid out like a game
library: a rail of every title, shelves of artwork, and a page for each film and series.

## Status

In early development. Signing in to a server and playback are not built yet. A built-in demo
library of invented titles, with artwork drawn in code, shows the interface in the meantime.

## Building

Requires the .NET 10 SDK.

```
dotnet build Tuxflix.slnx -c Release
src/Tuxflix.App/bin/Release/net10.0/tuxflix --demo
```

`--portable DIR` keeps settings, cache and logs in `DIR` instead of the XDG folders.

## Third-party material

See [NOTICES.txt](NOTICES.txt).
