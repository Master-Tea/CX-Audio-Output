using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CXAudioOutput;

internal sealed record SourceFormat(
    int SampleRate,
    int Channels,
    int BitsPerSample,
    bool IsFloat,
    int BlockAlign,
    int BytesPerSample)
{
    public int BytesPerFrame => BlockAlign;
}

internal sealed class WasapiLoopbackSession : IDisposable
{
    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _device;
    private object? _audioClientObject;
    private IAudioClient? _audioClient;
    private object? _captureObject;
    private IAudioCaptureClient? _captureClient;
    private IntPtr _mixFormat;
    private AutoResetEvent? _audioEvent;
    private bool _eventDriven;
    private bool _started;
    private string _deviceId = string.Empty;
    private SourceFormat _format = new(48000, 2, 32, true, 8, 4);
    private byte[] _chunk = Array.Empty<byte>();
    private int _chunkFill;
    private byte[] _scratch = Array.Empty<byte>();
    private float _chunkPeak;
    private long _framesDelivered;
    private long _lastDeviceCheckTicks;
    private long _lastAppendTicks;

    private WasapiLoopbackSession()
    {
    }

    public string DeviceName { get; private set; } = "默认音频输出";
    public SourceFormat Format => _format;
    public long FramesDelivered => Interlocked.Read(ref _framesDelivered);

    public static WasapiLoopbackSession Create()
    {
        var session = new WasapiLoopbackSession();
        try
        {
            session.Initialize();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private void Initialize()
    {
        var enumeratorObject = new WasapiInterop.MmDeviceEnumeratorComObject();
        _enumerator = (IMMDeviceEnumerator)enumeratorObject;

        var hr = _enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out var device);
        if (hr != 0)
        {
            var noDevice = hr is unchecked((int)0x80070490) or unchecked((int)0x80070002)
                or unchecked((int)0x80004005);
            throw WasapiException.FromHr("查找默认音频输出设备", hr, noDevice);
        }

        _device = device;
        hr = device.GetId(out var endpointId);
        if (hr == 0 && !string.IsNullOrWhiteSpace(endpointId))
        {
            _deviceId = endpointId;
        }

        DeviceName = GetFriendlyName(device);
        _format = ActivateAndGetFormat(device);

        _chunk = new byte[Math.Max(1, _format.SampleRate / 50) * 4];
        _scratch = new byte[8192];

        if (!TryInitializeEventDriven())
        {
            InitializePolling();
        }

        var captureIid = WasapiInterop.IidAudioCaptureClient;
        hr = _audioClient!.GetService(ref captureIid, out var captureObject);
        if (hr != 0)
        {
            throw WasapiException.FromHr("打开音频捕获接口", hr);
        }

        _captureObject = captureObject;
        _captureClient = (IAudioCaptureClient)captureObject;
    }

    private SourceFormat ActivateAndGetFormat(IMMDevice device)
    {
        var clientIid = WasapiInterop.IidAudioClient;
        var hr = device.Activate(ref clientIid, WasapiInterop.ClsCtxAll, IntPtr.Zero,
            out var clientObject);
        if (hr != 0)
        {
            throw WasapiException.FromHr("激活音频客户端", hr);
        }

        _audioClientObject = clientObject;
        _audioClient = (IAudioClient)clientObject;

        hr = _audioClient.GetMixFormat(out _mixFormat);
        if (hr != 0 || _mixFormat == IntPtr.Zero)
        {
            throw WasapiException.FromHr("读取系统混音格式", hr);
        }

        var format = SourceFormatParser.Parse(_mixFormat);
        if (format.SampleRate is < 8000 or > 384000 || format.Channels is < 1 or > 32)
        {
            throw new WasapiException($"不支持的音频格式：{format.SampleRate} Hz / {format.Channels} 声道。", 0);
        }

        return format;
    }

    private bool TryInitializeEventDriven()
    {
        var audioEvent = new AutoResetEvent(false);
        var handle = audioEvent.SafeWaitHandle.DangerousGetHandle();
        var hr = _audioClient!.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback,
            1_000_000,
            0,
            _mixFormat,
            IntPtr.Zero);

        if (hr == 0)
        {
            hr = _audioClient.SetEventHandle(handle);
        }

        if (hr == 0)
        {
            _audioEvent = audioEvent;
            _eventDriven = true;
            return true;
        }

        audioEvent.Dispose();
        return false;
    }

    private void InitializePolling()
    {
        ReleaseAudioClient();

        var clientIid = WasapiInterop.IidAudioClient;
        var hr = _device!.Activate(ref clientIid, WasapiInterop.ClsCtxAll, IntPtr.Zero,
            out var clientObject);
        if (hr != 0)
        {
            throw WasapiException.FromHr("重新激活音频客户端", hr);
        }

        _audioClientObject = clientObject;
        _audioClient = (IAudioClient)clientObject;

        hr = _audioClient.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.Loopback,
            1_000_000,
            0,
            _mixFormat,
            IntPtr.Zero);
        if (hr != 0)
        {
            throw WasapiException.FromHr("初始化回环音频流", hr);
        }

        _eventDriven = false;
    }

    public void Start()
    {
        var hr = _audioClient!.Start();
        if (hr != 0)
        {
            throw WasapiException.FromHr("启动音频捕获", hr);
        }

        _started = true;
    }

    public void Run(CancellationToken cancellationToken, AutoResetEvent wakeEvent, Func<bool> isPaused,
        Action<byte[], float> onFrame)
    {
        OnFrameReady = onFrame;
        _lastDeviceCheckTicks = Stopwatch.GetTimestamp();
        var waitHandles = _eventDriven && _audioEvent is not null
            ? new WaitHandle[] { _audioEvent, wakeEvent }
            : new WaitHandle[] { wakeEvent };

        while (!cancellationToken.IsCancellationRequested)
        {
            if (isPaused()) break;

            if (_eventDriven)
            {
                var index = WaitHandle.WaitAny(waitHandles, 25);
                if (index == 1 && (cancellationToken.IsCancellationRequested || isPaused()))
                {
                    break;
                }
            }
            else if (wakeEvent.WaitOne(5))
            {
                if (cancellationToken.IsCancellationRequested || isPaused())
                {
                    break;
                }
            }

            Drain(onFrame);
            FillSilenceIfIdle();

            var now = Stopwatch.GetTimestamp();
            if ((now - _lastDeviceCheckTicks) / (double)Stopwatch.Frequency >= 2.0)
            {
                _lastDeviceCheckTicks = now;
                if (DefaultOutputChanged())
                {
                    break;
                }
            }
        }

        TryStop();
    }

    private bool DefaultOutputChanged()
    {
        if (_enumerator is null || string.IsNullOrEmpty(_deviceId)) return false;
        var hr = _enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out var current);
        if (hr != 0 || current is null) return false;

        try
        {
            var idHr = current.GetId(out var id);
            return idHr == 0 && !string.IsNullOrEmpty(id)
                && !string.Equals(id, _deviceId, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ReleaseCom(current);
        }
    }

    private void Drain(Action<byte[], float> onFrame)
    {
        for (var guard = 0; guard < 256; guard++)
        {
            var hr = _captureClient!.GetNextPacketSize(out var packetFrames);
            if (hr != 0)
            {
                throw WasapiException.FromHr("读取音频包", hr);
            }

            if (packetFrames == 0) break;

            hr = _captureClient.GetBuffer(out var data, out var frames, out var flags,
                out _, out _);
            if (hr != 0)
            {
                throw WasapiException.FromHr("读取音频数据", hr);
            }

            try
            {
                if (frames == 0) continue;
                if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0)
                {
                    _chunkFill = 0;
                    _chunkPeak = 0;
                }

                if ((flags & AudioClientBufferFlags.Silent) != 0 || data == IntPtr.Zero)
                {
                    AppendSilence((int)frames);
                }
                else
                {
                    ConvertAndAppend(data, (int)frames);
                }
            }
            finally
            {
                _captureClient.ReleaseBuffer(frames);
            }
        }
    }

    private unsafe void ConvertAndAppend(IntPtr data, int frames)
    {
        var needed = frames * 4;
        if (_scratch.Length < needed)
        {
            _scratch = new byte[needed + 4096];
        }

        var peak = 0f;
        var channels = _format.Channels;
        var blockAlign = _format.BlockAlign;

        fixed (byte* destination = _scratch)
        {
            var output = (short*)destination;
            var source = (byte*)data;

            for (var frameIndex = 0; frameIndex < frames; frameIndex++)
            {
                var frame = source + (long)frameIndex * blockAlign;
                float left;
                float right;

                if (channels == 1)
                {
                    left = right = ReadChannel(frame, 0);
                }
                else
                {
                    left = ReadChannel(frame, 0);
                    right = ReadChannel(frame, 1);

                    if (channels >= 3)
                    {
                        var center = ReadChannel(frame, 2);
                        left += center * 0.70710678f;
                        right += center * 0.70710678f;
                    }

                    if (channels >= 6)
                    {
                        var lfe = ReadChannel(frame, 3);
                        var backLeft = ReadChannel(frame, 4);
                        var backRight = ReadChannel(frame, 5);
                        left += lfe * 0.5f + backLeft * 0.70710678f;
                        right += lfe * 0.5f + backRight * 0.70710678f;
                    }
                    else if (channels == 4)
                    {
                        left += ReadChannel(frame, 2) * 0.70710678f;
                        right += ReadChannel(frame, 3) * 0.70710678f;
                    }

                    if (channels >= 8)
                    {
                        left += ReadChannel(frame, 6) * 0.5f;
                        right += ReadChannel(frame, 7) * 0.5f;
                    }
                }

                left = Math.Clamp(left, -1f, 1f);
                right = Math.Clamp(right, -1f, 1f);
                peak = MathF.Max(peak, MathF.Max(MathF.Abs(left), MathF.Abs(right)));

                output[frameIndex * 2] = (short)Math.Clamp((int)MathF.Round(left * 32767f), -32768, 32767);
                output[frameIndex * 2 + 1] = (short)Math.Clamp((int)MathF.Round(right * 32767f), -32768, 32767);
            }
        }

        _chunkPeak = MathF.Max(_chunkPeak, peak);
        AppendConverted(needed);
    }

    private unsafe float ReadChannel(byte* frame, int channel)
    {
        var bytesPerSample = _format.BytesPerSample;
        var sample = frame + channel * bytesPerSample;

        if (_format.IsFloat)
        {
            return _format.BitsPerSample switch
            {
                32 => *(float*)sample,
                64 => (float)(*(double*)sample),
                _ => 0f
            };
        }

        return _format.BitsPerSample switch
        {
            8 => (sample[0] - 128) / 128f,
            16 => *(short*)sample / 32768f,
            24 => ReadPcm24(sample) / 8388608f,
            32 => *(int*)sample / 2147483648f,
            _ => 0f
        };
    }

    private static unsafe int ReadPcm24(byte* sample)
    {
        var value = sample[0] | (sample[1] << 8) | (sample[2] << 16);
        if ((value & 0x00800000) != 0)
        {
            value |= unchecked((int)0xFF000000);
        }

        return value;
    }

    private void FillSilenceIfIdle()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = _lastAppendTicks == 0
            ? 0.020
            : (now - _lastAppendTicks) / (double)Stopwatch.Frequency;
        if (elapsed < 0.020) return;

        // WASAPI loopback stops producing packets when the output is idle. Sending
        // exactly the elapsed amount of silence keeps the receiver's jitter buffer
        // primed for instant playback when audio starts again.
        var missingFrames = (int)Math.Round(elapsed * _format.SampleRate);
        missingFrames = Math.Clamp(missingFrames, 1, _format.SampleRate / 10);
        AppendSilence(missingFrames);
    }

    private void AppendSilence(int frames)
    {
        var byteCount = frames * 4;
        if (byteCount <= 0) return;
        if (_scratch.Length < byteCount)
        {
            _scratch = new byte[byteCount + 4096];
        }

        Array.Clear(_scratch, 0, byteCount);
        _chunkPeak = MathF.Max(_chunkPeak, 0f);
        AppendConverted(byteCount);
    }

    private void AppendConverted(int byteCount)
    {
        _lastAppendTicks = Stopwatch.GetTimestamp();
        var offset = 0;
        while (offset < byteCount)
        {
            var room = _chunk.Length - _chunkFill;
            if (room <= 0)
            {
                PublishChunk();
                continue;
            }

            var take = Math.Min(room, byteCount - offset);
            Buffer.BlockCopy(_scratch, offset, _chunk, _chunkFill, take);
            _chunkFill += take;
            offset += take;

            if (_chunkFill >= _chunk.Length)
            {
                PublishChunk();
            }
        }
    }

    private void PublishChunk()
    {
        if (_chunkFill == 0) return;

        var frame = _chunk;
        if (_chunkFill < frame.Length)
        {
            Array.Resize(ref frame, _chunkFill);
        }

        var peak = Math.Clamp(_chunkPeak, 0f, 1f);
        _chunk = new byte[Math.Max(1, _format.SampleRate / 50) * 4];
        _chunkFill = 0;
        _chunkPeak = 0;
        Interlocked.Add(ref _framesDelivered, frame.Length / 4);

        if (OnFrameReady is not null)
        {
            OnFrameReady(frame, peak);
        }
    }

    public Action<byte[], float>? OnFrameReady { get; set; }

    private void TryStop()
    {
        if (!_started || _audioClient is null) return;
        try
        {
            _audioClient.Stop();
        }
        catch
        {
            // Ignore shutdown races in the audio engine.
        }

        _started = false;
    }

    public static string GetFriendlyName(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            if (device.OpenPropertyStore(WasapiInterop.StgmRead, out var propertyStore) != 0)
            {
                return "默认音频输出";
            }

            store = propertyStore;
            var key = WasapiInterop.DeviceFriendlyName;
            if (store.GetValue(ref key, out var value) != 0)
            {
                return "默认音频输出";
            }

            try
            {
                if (value.ValueType == 31 && value.PointerValue != IntPtr.Zero)
                {
                    var name = Marshal.PtrToStringUni(value.PointerValue);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        name = name.Trim();
                        return name.Length <= 90 ? name : name[..90];
                    }
                }
            }
            finally
            {
                WasapiInterop.PropVariantClear(ref value);
            }
        }
        catch
        {
            // A missing friendly name must never prevent audio capture.
        }
        finally
        {
            ReleaseCom(store);
        }

        return "默认音频输出";
    }

    private void ReleaseAudioClient()
    {
        if (_audioClient is not null)
        {
            try
            {
                _audioClient.Stop();
            }
            catch
            {
                // ignored
            }
        }

        ReleaseCom(_audioClientObject);
        _audioClientObject = null;
        _audioClient = null;
        _started = false;
    }

    private static void ReleaseCom(object? value)
    {
        if (value is null) return;
        try
        {
            if (Marshal.IsComObject(value))
            {
                Marshal.FinalReleaseComObject(value);
            }
        }
        catch
        {
            // ignored
        }
    }

    public void Dispose()
    {
        TryStop();
        ReleaseCom(_captureObject);
        _captureObject = null;
        _captureClient = null;
        ReleaseAudioClient();

        if (_mixFormat != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(_mixFormat);
            _mixFormat = IntPtr.Zero;
        }

        if (_audioEvent is not null)
        {
            _audioEvent.Dispose();
            _audioEvent = null;
        }

        ReleaseCom(_device);
        _device = null;
        ReleaseCom(_enumerator);
        _enumerator = null;
    }
}

internal static class SourceFormatParser
{
    public static SourceFormat Parse(IntPtr formatPointer)
    {
        var formatTag = (ushort)Marshal.ReadInt16(formatPointer, 0);
        var channels = (ushort)Marshal.ReadInt16(formatPointer, 2);
        var sampleRate = Marshal.ReadInt32(formatPointer, 4);
        var blockAlign = (ushort)Marshal.ReadInt16(formatPointer, 12);
        var bitsPerSample = (ushort)Marshal.ReadInt16(formatPointer, 14);
        var isFloat = formatTag == 3;

        if (formatTag == 0xFFFE)
        {
            var subFormatTag = Marshal.ReadInt32(formatPointer, 24);
            isFloat = subFormatTag == 3;
        }

        var bytesPerSample = Math.Max(1, (bitsPerSample + 7) / 8);
        if (blockAlign == 0)
        {
            blockAlign = (ushort)(bytesPerSample * channels);
        }

        if (isFloat && bitsPerSample is not (32 or 64))
        {
            throw new WasapiException($"暂不支持 {bitsPerSample}-bit 浮点音频。", 0);
        }

        if (!isFloat && bitsPerSample is not (8 or 16 or 24 or 32))
        {
            throw new WasapiException($"暂不支持 {bitsPerSample}-bit PCM 音频。", 0);
        }

        if (blockAlign < bytesPerSample * channels)
        {
            blockAlign = (ushort)(bytesPerSample * channels);
        }

        return new SourceFormat(sampleRate, channels, bitsPerSample, isFloat, blockAlign, bytesPerSample);
    }
}
