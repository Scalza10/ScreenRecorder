using ScreenRecorder.Core.Devices;
using ScreenRecorder.Core.Tests.Infrastructure;

namespace ScreenRecorder.Core.Tests.Recording;

public class AudioDevicesTests
{
    [Fact]
    public void Parses_audio_devices_from_dshow_listing()
    {
        string[] lines =
        [
            "[in#0 @ 0000024219f34c00] \"Integrated Camera\" (video)",
            "[in#0 @ 0000024219f34c00]   Alternative name \"@device_pnp_\\\\?\\usb#vid_30c9\"",
            "[in#0 @ 0000024219f34c00] \"OBS Virtual Camera\" (none)",
            "[in#0 @ 0000024219f34c00] \"Microphone Array (Realtek(R) Audio)\" (audio)",
            "[in#0 @ 0000024219f34c00]   Alternative name \"@device_cm_{33D9A762}\\wave_{AF76A5B3}\"",
            "[in#0 @ 0000024219f34c00] \"Microphone (Virtual Desktop Audio)\" (audio)",
            "Error opening input file dummy.",
        ];

        var devices = AudioDevices.Parse(lines);

        Assert.Equal(["Microphone Array (Realtek(R) Audio)", "Microphone (Virtual Desktop Audio)"], devices);
    }

    [Fact]
    public async Task Lists_devices_without_throwing()
    {
        // The machine may or may not have a microphone; this only proves the ffmpeg call and parsing work end to end.
        var devices = await AudioDevices.ListAsync(TestMedia.Ffmpeg);

        Assert.NotNull(devices);
    }
}
