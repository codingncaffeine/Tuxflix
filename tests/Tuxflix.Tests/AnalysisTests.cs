using System.Diagnostics;
using Tuxflix.Player;
using Tuxflix.Player.Analysis;
using Xunit;

namespace Tuxflix.Tests;

/// <summary>The visualizers' analysis: bands, beats, and the silent second decode that feeds them.</summary>
public sealed class AnalysisTests
{
    private static float[] Sine(double hz, double amplitude, int count, int rate = ShadowDecoder.Rate) =>
        [.. Enumerable.Range(0, count).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate)))];

    private static int Loudest(AudioFrame frame) => Array.IndexOf(frame.Bands, frame.Bands.Max());

    [Fact]
    public void AToneLandsInTheBandThatHoldsItsFrequency()
    {
        var analyser = new SpectrumAnalyser(new AnalyserOptions());
        var tone = Sine(1000, 0.5, 4096);
        for (var i = 0; i < 10; i++) analyser.Process(tone, 1f / 60);
        var frame = analyser.Publish(tone, tone, SpectrumAnalyser.ToDb(0.5f));

        var band = Loudest(frame);
        var centres = frame.BandCentres;
        var lower = band == 0 ? 0 : Math.Sqrt(centres[band - 1] * centres[band]);
        var upper = band == centres.Length - 1 ? double.MaxValue : Math.Sqrt(centres[band] * centres[band + 1]);
        Assert.InRange(1000, lower, upper);

        // Half scale is -6 dBFS: 54 dB above a -60 dB floor, 0.9 of the height.
        Assert.InRange(frame.Bands[band], 0.85f, 0.95f);
        Assert.False(frame.Silent);
    }

    [Fact]
    public void ABeatIsARiseNotALoudness()
    {
        var onset = new OnsetDetector(8);
        var steady = Enumerable.Repeat(0.8f, 8).ToArray();
        var quiet = Enumerable.Repeat(0.1f, 8).ToArray();
        var beats = 0;

        // A long loud held note: never a beat.
        for (var i = 0; i < 120; i++)
        {
            onset.Process(steady, 1f / 60);
            if (onset.Beat) beats++;
        }

        Assert.Equal(0, beats);

        // Quiet, then a hit: one beat.
        for (var i = 0; i < 60; i++) onset.Process(quiet, 1f / 60);
        onset.Process(steady, 1f / 60);
        Assert.True(onset.Beat);
    }

    [Fact]
    public async Task TheShadowDecodeHoldsATrackAtItsOwnTime()
    {
        if (!MpvPlayer.IsAvailable) Assert.Skip("libmpv is not installed here.");

        using var decoder = new ShadowDecoder("av://lavfi:sine=frequency=1000:sample_rate=48000", 1.0, new Dictionary<string, string>());
        decoder.Follow(1.0);
        var clock = Stopwatch.StartNew();
        while (decoder.DecodedUntil < 5 && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20, TestContext.Current.CancellationToken);

        var left = new float[4096];
        var right = new float[4096];
        Assert.False(decoder.TryRead(1.02, left, right), "a window reaching back before the start must not read");
        Assert.True(decoder.TryRead(2.0, left, right), $"decoded only to {decoder.DecodedUntil:0.00} s");

        var analyser = new SpectrumAnalyser(new AnalyserOptions());
        for (var i = 0; i < 10; i++) analyser.Process(left, 1f / 60);
        var frame = analyser.Publish(left, right, SpectrumAnalyser.ToDb(left.Max()));
        var band = Loudest(frame);
        Assert.InRange(1000, frame.BandCentres[Math.Max(0, band - 1)], frame.BandCentres[Math.Min(band + 1, frame.BandCentres.Length - 1)]);
    }

    [Fact]
    public async Task TheShadowDecodeRunsNoFurtherAheadThanItIsAllowed()
    {
        if (!MpvPlayer.IsAvailable) Assert.Skip("libmpv is not installed here.");

        // An endless tone decodes hundreds of times faster than real time: held to twenty seconds
        // past the listener, it must stop there, not run on.
        using var decoder = new ShadowDecoder("av://lavfi:sine=frequency=440:sample_rate=48000", 0, new Dictionary<string, string>());
        decoder.Follow(0);
        await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);
        Assert.InRange(decoder.DecodedUntil, 19.0, 22.0);

        // Moving the listener on lets it go on.
        decoder.Follow(10);
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.InRange(decoder.DecodedUntil, 29.0, 32.0);

        // And it winds down promptly while blocked on a full pipe.
        var closing = Stopwatch.StartNew();
        decoder.Dispose();
        Assert.True(closing.Elapsed < TimeSpan.FromSeconds(3), $"closing took {closing.Elapsed.TotalSeconds:0.0} s");
    }
}
