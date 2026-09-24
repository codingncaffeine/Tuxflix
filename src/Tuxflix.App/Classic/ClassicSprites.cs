using Avalonia;

namespace Tuxflix.App.Classic;

/// <summary>A rectangle cut from one of a classic skin's sheets.</summary>
public readonly record struct Sprite(string Sheet, int X, int Y, int Width, int Height)
{
    public Rect Source => new(X, Y, Width, Height);
}

/// <summary>
/// Where every part of a classic (Winamp 2) skin sits on its sheets, and where it goes in the
/// windows. The coordinates follow Webamp's sprite map and stylesheets (MIT licence, see NOTICES).
/// </summary>
public static class ClassicSprites
{
    public const int MainWidth = 275;
    public const int MainHeight = 116;
    public const int EqHeight = 116;
    public const int PlaylistHeight = 232;

    // Sheets, by the lower-case file name the skin keeps them in.
    public const string Main = "main";
    public const string Cbuttons = "cbuttons";
    public const string Titlebar = "titlebar";
    public const string Shufrep = "shufrep";
    public const string Posbar = "posbar";
    public const string Volume = "volume";
    public const string Balance = "balance";
    public const string Numbers = "numbers";
    public const string NumsEx = "nums_ex";
    public const string Text = "text";
    public const string Playpaus = "playpaus";
    public const string Monoster = "monoster";
    public const string Eqmain = "eqmain";
    public const string EqEx = "eq_ex";
    public const string Pledit = "pledit";

    public static readonly string[] Sheets = [Main, Cbuttons, Titlebar, Shufrep, Posbar, Volume, Balance, Numbers, NumsEx, Text, Playpaus, Monoster, Eqmain, EqEx, Pledit];

    // ===== Main window =====
    public static readonly Sprite MainBackground = new(Main, 0, 0, 275, 116);
    public static readonly Sprite TitleBar = new(Titlebar, 27, 15, 275, 14);
    public static readonly Sprite TitleBarActive = new(Titlebar, 27, 0, 275, 14);
    public static readonly Sprite OptionsButton = new(Titlebar, 0, 0, 9, 9);
    public static readonly Sprite OptionsButtonDown = new(Titlebar, 0, 9, 9, 9);
    public static readonly Sprite MinimizeButton = new(Titlebar, 9, 0, 9, 9);
    public static readonly Sprite MinimizeButtonDown = new(Titlebar, 9, 9, 9, 9);
    public static readonly Sprite ShadeButton = new(Titlebar, 0, 18, 9, 9);
    public static readonly Sprite ShadeButtonDown = new(Titlebar, 9, 18, 9, 9);
    public static readonly Sprite CloseButton = new(Titlebar, 18, 0, 9, 9);
    public static readonly Sprite CloseButtonDown = new(Titlebar, 18, 9, 9, 9);
    public static readonly Sprite ClutterBar = new(Titlebar, 304, 0, 8, 43);
    public static readonly Sprite ClutterA = new(Titlebar, 312, 55, 8, 7);
    public static readonly Sprite ClutterD = new(Titlebar, 328, 69, 8, 8);
    public static readonly Sprite ShadeBackground = new(Titlebar, 27, 42, 275, 14);
    public static readonly Sprite ShadeBackgroundActive = new(Titlebar, 27, 29, 275, 14);
    public static readonly Sprite UnshadeButton = new(Titlebar, 0, 27, 9, 9);
    public static readonly Sprite UnshadeButtonDown = new(Titlebar, 9, 27, 9, 9);
    public static readonly Sprite ShadePositionBackground = new(Titlebar, 0, 36, 17, 7);
    public static readonly Sprite ShadePositionThumbLeft = new(Titlebar, 17, 36, 3, 7);
    public static readonly Sprite ShadePositionThumb = new(Titlebar, 20, 36, 3, 7);
    public static readonly Sprite ShadePositionThumbRight = new(Titlebar, 23, 36, 3, 7);

    public static readonly Sprite Previous = new(Cbuttons, 0, 0, 23, 18);
    public static readonly Sprite PreviousDown = new(Cbuttons, 0, 18, 23, 18);
    public static readonly Sprite Play = new(Cbuttons, 23, 0, 23, 18);
    public static readonly Sprite PlayDown = new(Cbuttons, 23, 18, 23, 18);
    public static readonly Sprite Pause = new(Cbuttons, 46, 0, 23, 18);
    public static readonly Sprite PauseDown = new(Cbuttons, 46, 18, 23, 18);
    public static readonly Sprite Stop = new(Cbuttons, 69, 0, 23, 18);
    public static readonly Sprite StopDown = new(Cbuttons, 69, 18, 23, 18);
    public static readonly Sprite Next = new(Cbuttons, 92, 0, 22, 18);
    public static readonly Sprite NextDown = new(Cbuttons, 92, 18, 22, 18);
    public static readonly Sprite Eject = new(Cbuttons, 114, 0, 22, 16);
    public static readonly Sprite EjectDown = new(Cbuttons, 114, 16, 22, 16);

    public static readonly Sprite PlayingIndicator = new(Playpaus, 0, 0, 9, 9);
    public static readonly Sprite PausedIndicator = new(Playpaus, 9, 0, 9, 9);
    public static readonly Sprite StoppedIndicator = new(Playpaus, 18, 0, 9, 9);
    public static readonly Sprite WorkingIndicator = new(Playpaus, 39, 0, 3, 9);
    public static readonly Sprite NotWorkingIndicator = new(Playpaus, 36, 0, 3, 9);

    public static readonly Sprite Stereo = new(Monoster, 0, 12, 29, 12);
    public static readonly Sprite StereoLit = new(Monoster, 0, 0, 29, 12);
    public static readonly Sprite Mono = new(Monoster, 29, 12, 27, 12);
    public static readonly Sprite MonoLit = new(Monoster, 29, 0, 27, 12);

    public static readonly Sprite PositionBackground = new(Posbar, 0, 0, 248, 10);
    public static readonly Sprite PositionThumb = new(Posbar, 248, 0, 29, 10);
    public static readonly Sprite PositionThumbDown = new(Posbar, 278, 0, 29, 10);

    public static readonly Sprite VolumeThumb = new(Volume, 15, 422, 14, 11);
    public static readonly Sprite VolumeThumbDown = new(Volume, 0, 422, 14, 11);
    public static readonly Sprite BalanceThumb = new(Balance, 15, 422, 14, 11);
    public static readonly Sprite BalanceThumbDown = new(Balance, 0, 422, 14, 11);

    public static readonly Sprite ShuffleOff = new(Shufrep, 28, 0, 47, 15);
    public static readonly Sprite ShuffleOffDown = new(Shufrep, 28, 15, 47, 15);
    public static readonly Sprite ShuffleOn = new(Shufrep, 28, 30, 47, 15);
    public static readonly Sprite ShuffleOnDown = new(Shufrep, 28, 45, 47, 15);
    public static readonly Sprite RepeatOff = new(Shufrep, 0, 0, 28, 15);
    public static readonly Sprite RepeatOffDown = new(Shufrep, 0, 15, 28, 15);
    public static readonly Sprite RepeatOn = new(Shufrep, 0, 30, 28, 15);
    public static readonly Sprite RepeatOnDown = new(Shufrep, 0, 45, 28, 15);
    public static readonly Sprite EqOff = new(Shufrep, 0, 61, 23, 12);
    public static readonly Sprite EqOn = new(Shufrep, 0, 73, 23, 12);
    public static readonly Sprite EqOffDown = new(Shufrep, 46, 61, 23, 12);
    public static readonly Sprite EqOnDown = new(Shufrep, 46, 73, 23, 12);
    public static readonly Sprite PlOff = new(Shufrep, 23, 61, 23, 12);
    public static readonly Sprite PlOn = new(Shufrep, 23, 73, 23, 12);
    public static readonly Sprite PlOffDown = new(Shufrep, 69, 61, 23, 12);
    public static readonly Sprite PlOnDown = new(Shufrep, 69, 73, 23, 12);

    public static Sprite Digit(int d, bool extended) => new(extended ? NumsEx : Numbers, d * 9, 0, 9, 13);

    public static readonly Sprite MinusSign = new(Numbers, 20, 6, 5, 1);
    public static readonly Sprite MinusSignEx = new(NumsEx, 99, 0, 9, 13);

    // Where things go in the main window.
    public static readonly Rect TitleArea = new(0, 0, 275, 14);
    public static readonly Rect OptionsArea = new(6, 3, 9, 9);
    public static readonly Rect MinimizeArea = new(244, 3, 9, 9);
    public static readonly Rect ShadeArea = new(254, 3, 9, 9);
    public static readonly Rect CloseArea = new(264, 3, 9, 9);
    public static readonly Rect ClutterArea = new(10, 22, 8, 43);
    public static readonly Rect ClutterOArea = new(10, 25, 8, 8);
    public static readonly Rect ClutterAArea = new(10, 33, 8, 7);
    public static readonly Rect ClutterIArea = new(10, 40, 8, 7);
    public static readonly Rect ClutterDArea = new(10, 47, 8, 8);
    public static readonly Rect ClutterVArea = new(10, 55, 8, 7);
    public static readonly Rect StatusArea = new(26, 28, 9, 9);
    public static readonly Rect WorkArea = new(24, 28, 3, 9);
    public static readonly Rect TimeArea = new(39, 26, 59, 13);
    public static readonly Rect VisArea = new(24, 43, 76, 16);
    public static readonly Rect MarqueeArea = new(111, 24, 154, 6);
    public static readonly Rect KbpsArea = new(111, 43, 15, 6);
    public static readonly Rect KhzArea = new(156, 43, 10, 6);
    public static readonly Rect MonoArea = new(212, 41, 27, 12);
    public static readonly Rect StereoArea = new(239, 41, 29, 12);
    public static readonly Rect VolumeArea = new(107, 57, 68, 13);
    public static readonly Rect BalanceArea = new(177, 57, 38, 13);
    public static readonly Rect EqButtonArea = new(219, 58, 23, 12);
    public static readonly Rect PlButtonArea = new(242, 58, 23, 12);
    public static readonly Rect PositionArea = new(16, 72, 248, 10);
    public static readonly Rect PreviousArea = new(16, 88, 23, 18);
    public static readonly Rect PlayArea = new(39, 88, 23, 18);
    public static readonly Rect PauseArea = new(62, 88, 23, 18);
    public static readonly Rect StopArea = new(85, 88, 23, 18);
    public static readonly Rect NextArea = new(108, 88, 22, 18);
    public static readonly Rect EjectArea = new(136, 89, 22, 16);
    public static readonly Rect ShuffleArea = new(164, 89, 47, 15);
    public static readonly Rect RepeatArea = new(210, 89, 28, 15);

    // The main window rolled up ("windowshade"): 14 pixels tall.
    public const int ShadeHeight = 14;
    public static readonly Rect ShadeVisArea = new(79, 5, 38, 5);
    public static readonly Rect ShadeTimeArea = new(127, 4, 30, 6);
    public static readonly Rect ShadePositionArea = new(226, 4, 17, 7);
    public static readonly Rect ShadePreviousArea = new(169, 2, 7, 10);
    public static readonly Rect ShadePlayArea = new(176, 2, 10, 10);
    public static readonly Rect ShadePauseArea = new(186, 2, 9, 10);
    public static readonly Rect ShadeStopArea = new(195, 2, 9, 10);
    public static readonly Rect ShadeNextArea = new(204, 2, 10, 10);
    public static readonly Rect ShadeEjectArea = new(215, 2, 10, 10);

    // ===== Equalizer window =====
    public static readonly Sprite EqBackground = new(Eqmain, 0, 0, 275, 116);
    public static readonly Sprite EqTitleBar = new(Eqmain, 0, 149, 275, 14);
    public static readonly Sprite EqTitleBarActive = new(Eqmain, 0, 134, 275, 14);
    public static readonly Sprite EqSliderBackground = new(Eqmain, 13, 164, 209, 129);
    public static readonly Sprite EqThumb = new(Eqmain, 0, 164, 11, 11);
    public static readonly Sprite EqThumbDown = new(Eqmain, 0, 176, 11, 11);
    public static readonly Sprite EqCloseDown = new(Eqmain, 0, 125, 9, 9);
    public static readonly Sprite EqOnButton = new(Eqmain, 10, 119, 26, 12);
    public static readonly Sprite EqOnButtonDown = new(Eqmain, 128, 119, 26, 12);
    public static readonly Sprite EqOnButtonLit = new(Eqmain, 69, 119, 26, 12);
    public static readonly Sprite EqOnButtonLitDown = new(Eqmain, 187, 119, 26, 12);
    public static readonly Sprite EqAutoButton = new(Eqmain, 36, 119, 32, 12);
    public static readonly Sprite EqAutoButtonDown = new(Eqmain, 154, 119, 32, 12);
    public static readonly Sprite EqAutoButtonLit = new(Eqmain, 95, 119, 32, 12);
    public static readonly Sprite EqAutoButtonLitDown = new(Eqmain, 213, 119, 32, 12);
    public static readonly Sprite EqGraphBackground = new(Eqmain, 0, 294, 113, 19);
    public static readonly Sprite EqGraphLineColors = new(Eqmain, 115, 294, 1, 19);
    public static readonly Sprite EqPreampLine = new(Eqmain, 0, 314, 113, 1);
    public static readonly Sprite EqPresetsButton = new(Eqmain, 224, 164, 44, 12);
    public static readonly Sprite EqPresetsButtonDown = new(Eqmain, 224, 176, 44, 12);

    public static readonly Rect EqOnArea = new(14, 18, 26, 12);
    public static readonly Rect EqAutoArea = new(40, 18, 32, 12);
    public static readonly Rect EqPresetsArea = new(217, 18, 44, 12);
    public static readonly Rect EqGraphArea = new(86, 17, 113, 19);
    public static readonly Rect EqPreampArea = new(21, 38, 14, 63);
    public static readonly Rect EqCloseArea = new(264, 3, 9, 9);

    /// <summary>The ten bands' sliders, at Winamp's classic centres.</summary>
    public static Rect EqBandArea(int band) => new(78 + (18 * band), 38, 14, 63);

    public static readonly int[] EqFrequencies = [60, 170, 310, 600, 1000, 3000, 6000, 12000, 14000, 16000];

    // ===== Playlist window =====
    public static readonly Sprite PlTopLeft = new(Pledit, 0, 21, 25, 20);
    public static readonly Sprite PlTopLeftActive = new(Pledit, 0, 0, 25, 20);
    public static readonly Sprite PlTitle = new(Pledit, 26, 21, 100, 20);
    public static readonly Sprite PlTitleActive = new(Pledit, 26, 0, 100, 20);
    public static readonly Sprite PlTopTile = new(Pledit, 127, 21, 25, 20);
    public static readonly Sprite PlTopTileActive = new(Pledit, 127, 0, 25, 20);
    public static readonly Sprite PlTopRight = new(Pledit, 153, 21, 25, 20);
    public static readonly Sprite PlTopRightActive = new(Pledit, 153, 0, 25, 20);
    public static readonly Sprite PlLeftTile = new(Pledit, 0, 42, 12, 29);
    public static readonly Sprite PlRightTile = new(Pledit, 31, 42, 20, 29);
    public static readonly Sprite PlBottomLeft = new(Pledit, 0, 72, 125, 38);
    public static readonly Sprite PlBottomRight = new(Pledit, 126, 72, 150, 38);
    public static readonly Sprite PlBottomTile = new(Pledit, 179, 0, 25, 38);
    public static readonly Sprite PlScrollHandle = new(Pledit, 52, 53, 8, 18);
    public static readonly Sprite PlCloseDown = new(Pledit, 52, 42, 9, 9);

    // ===== The 5x6 font of text.bmp =====
    private static readonly Dictionary<char, (int Row, int Column)> Font = BuildFont();

    public static Sprite Character(char c)
    {
        var lower = char.ToLowerInvariant(c);
        var (row, column) = Font.TryGetValue(lower, out var at) || Font.TryGetValue(Fold(lower), out at) ? at : Font[' '];
        return new Sprite(Text, column * 5, row * 6, 5, 6);
    }

    /// <summary>
    /// The nearest character the skin font has: typographic quotes and dashes to plain ones, an
    /// accented letter to its base letter. Anything else is drawn as a space.
    /// </summary>
    private static char Fold(char c)
    {
        switch ((int)c)
        {
            case 0x2018 or 0x2019 or 0x201A or 0x2032 or 0x00B4 or 0x0060: return '\'';
            case 0x201C or 0x201D or 0x201E or 0x2033: return '"';
            case 0x2010 or 0x2011 or 0x2012 or 0x2013 or 0x2014 or 0x2015 or 0x2212: return '-';
        }

        var decomposed = c.ToString().Normalize(System.Text.NormalizationForm.FormD);
        return decomposed.Length > 0 && decomposed[0] < 128 ? char.ToLowerInvariant(decomposed[0]) : ' ';
    }

    private static Dictionary<char, (int, int)> BuildFont()
    {
        var font = new Dictionary<char, (int, int)>();
        for (var i = 0; i < 26; i++) font[(char)('a' + i)] = (0, i);
        font['"'] = (0, 26);
        font['@'] = (0, 27);
        font[' '] = (0, 30);
        for (var i = 0; i < 10; i++) font[(char)('0' + i)] = (1, i);
        font[(char)0x2026] = (1, 10);
        font['.'] = (1, 11);
        font[':'] = (1, 12);
        font['('] = (1, 13);
        font[')'] = (1, 14);
        font['-'] = (1, 15);
        font['\''] = (1, 16);
        font['!'] = (1, 17);
        font['_'] = (1, 18);
        font['+'] = (1, 19);
        font[(char)92] = (1, 20);
        font['/'] = (1, 21);
        font['['] = (1, 22);
        font[']'] = (1, 23);
        font['^'] = (1, 24);
        font['&'] = (1, 25);
        font['%'] = (1, 26);
        font[','] = (1, 27);
        font['='] = (1, 28);
        font['$'] = (1, 29);
        font['#'] = (1, 30);
        font[(char)0xE5] = (2, 0);
        font[(char)0xF6] = (2, 1);
        font[(char)0xE4] = (2, 2);
        font['?'] = (2, 3);
        font['*'] = (2, 4);
        font['<'] = (1, 22);
        font['>'] = (1, 23);
        font['{'] = (1, 22);
        font['}'] = (1, 23);
        return font;
    }
}
