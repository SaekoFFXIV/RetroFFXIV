using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using RetroXIV.Emulation;

namespace RetroXIV;

// Plays the core's decoded audio through WASAPI. Volume is applied as a gain on the samples in the
// wave provider (reliable) rather than via the WASAPI session volume, which did not take effect.
public sealed class AudioPlayer : IDisposable
{
    private readonly RetroCore core;
    private readonly Configuration config;
    private WasapiOut? output;
    private CoreWaveProvider? provider;

    public AudioPlayer(RetroCore core, Configuration config)
    {
        this.core = core;
        this.config = config;
    }

    public void Start()
    {
        var rate = (int)Math.Round(core.SampleRate);
        if (rate <= 0)
        {
            rate = 32000;
        }

        var format = new WaveFormat(rate, 16, 2);
        provider = new CoreWaveProvider(core, format)
        {
            Volume = Math.Clamp(config.Volume, 0f, 1f),
        };

        output = new WasapiOut(AudioClientShareMode.Shared, 40);
        output.Init(provider);
        output.Play();
    }

    public void SetVolume(float volume)
    {
        if (provider != null)
        {
            provider.Volume = Math.Clamp(volume, 0f, 1f);
        }
    }

    public void Dispose()
    {
        output?.Dispose();
        output = null;
        provider = null;
    }
}

// Feeds WASAPI from the core's audio ring buffer. Returns silence on underrun so playback keeps
// running instead of stalling when the emulator momentarily produces no samples.
public sealed class CoreWaveProvider : IWaveProvider
{
    private readonly RetroCore core;
    private short[] scratch = Array.Empty<short>();

    public WaveFormat WaveFormat { get; }

    public float Volume { get; set; } = 1.0f;

    public CoreWaveProvider(RetroCore core, WaveFormat format)
    {
        this.core = core;
        WaveFormat = format;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var maxFrames = count / 4; // 16-bit stereo = 4 bytes per frame
        if (scratch.Length < maxFrames * 2)
        {
            scratch = new short[maxFrames * 2];
        }

        var frames = core.ReadAudio(scratch, maxFrames);
        var bytes = frames * 4;
        Buffer.BlockCopy(scratch, 0, buffer, offset, bytes);

        var volume = Volume;
        if (volume < 1.0f)
        {
            for (var i = 0; i < bytes; i += 2)
            {
                var sample = (short)(buffer[offset + i] | (buffer[offset + i + 1] << 8));
                sample = (short)(sample * volume);
                buffer[offset + i] = (byte)(sample & 0xFF);
                buffer[offset + i + 1] = (byte)((sample >> 8) & 0xFF);
            }
        }

        if (bytes < count)
        {
            Array.Clear(buffer, offset + bytes, count - bytes);
        }

        return count;
    }
}
