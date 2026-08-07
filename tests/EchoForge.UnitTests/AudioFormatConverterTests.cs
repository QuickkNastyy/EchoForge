using System.Globalization;
using EchoForge.App;
using EchoForge.Contracts.Audio;

namespace EchoForge.UnitTests;

/// <summary>
/// How a device row renders its negotiated format.
///
/// <para>
/// The bug this guards against is a device picker that fell back to the record's own
/// <c>ToString</c> and printed "AudioEndpointInfo { Id = … }". The converter is what the row binds
/// to instead, and the one thing it must never do is emit a DTO dump for anything it does not
/// understand.
/// </para>
/// </summary>
public sealed class AudioFormatConverterTests
{
    private static string Format(CaptureFormat format) =>
        (string)new AudioFormatConverter().Convert(format, typeof(string), null, CultureInfo.InvariantCulture);

    [Fact]
    public void RendersTheMockupsFormat()
    {
        Assert.Equal("48 kHz · stereo · 16-bit", Format(new CaptureFormat(48000, 2, 16)));
        Assert.Equal("16 kHz · mono · 16-bit", Format(new CaptureFormat(16000, 1, 16)));
        Assert.Equal("44.1 kHz · stereo · 24-bit", Format(new CaptureFormat(44100, 2, 24)));
        Assert.Equal("48 kHz · 6 ch · 32-bit", Format(new CaptureFormat(48000, 6, 32)));
    }

    [Fact]
    public void NeverEmitsARawDtoForSomethingItCannotFormat()
    {
        AudioFormatConverter converter = new();

        // A whole endpoint, a null, a string — none of these should ever reach the UI as a dump.
        object endpoint = new AudioEndpointInfo("id", "Headphones (Astro A50 Game)", true, new CaptureFormat(48000, 2, 16));

        Assert.Equal(string.Empty, converter.Convert(endpoint, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal(string.Empty, converter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal(string.Empty, converter.Convert("whatever", typeof(string), null, CultureInfo.InvariantCulture));
    }
}
