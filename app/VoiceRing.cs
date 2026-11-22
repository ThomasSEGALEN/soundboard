using System.IO.MemoryMappedFiles;

namespace Soundboard;

/// <summary>
/// Reader side of the voice section (apo/SharedRing.h, SmxVoiceHeader): your voice after noise
/// suppression, published by the APO for the "hear myself" option. Read only.
/// </summary>
sealed unsafe class VoiceRing : IDisposable
{
    const string Name = @"Global\SoundMixApo_Voice_v1";
    const uint Magic = 0x31434F56;
    const int Capacity = 65536;
    const long TotalSize = 64 + Capacity * 4L;
    const int TargetLag = 1440;   // 30 ms behind the writer
    const int MaxLag = 9600;      // resync when further behind (200 ms)

    readonly MemoryMappedFile _mmf;
    readonly MemoryMappedViewAccessor _view;
    readonly byte* _base;
    readonly float* _data;
    long _cursor = -1;

    VoiceRing(MemoryMappedFile mmf, MemoryMappedViewAccessor view, byte* p)
    {
        _mmf = mmf; _view = view; _base = p; _data = (float*)(p + 64);
    }

    public static VoiceRing? TryOpen()
    {
        MemoryMappedFile? mmf = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            mmf = MemoryMappedFile.OpenExisting(Name, MemoryMappedFileRights.Read);
            view = mmf.CreateViewAccessor(0, TotalSize, MemoryMappedFileAccess.Read);
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            p += view.PointerOffset;
            if (*(uint*)p != Magic)
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
                view.Dispose(); mmf.Dispose();
                return null;
            }
            return new VoiceRing(mmf, view, p);
        }
        catch
        {
            view?.Dispose(); mmf?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Adds `frames` frames of voice (48 kHz) to an interleaved stereo buffer.
    /// Silence is added when the APO is not publishing or runs at another rate.
    /// </summary>
    public void AddTo(float[] stereo, int frames, float gain)
    {
        uint rate = *(uint*)(_base + 4);
        long owner = Volatile.Read(ref *(long*)(_base + 24));
        long ownerTick = Volatile.Read(ref *(long*)(_base + 32));
        if (owner == 0 || rate != AudioEngine.Rate || Environment.TickCount64 - ownerTick > 300)
        {
            _cursor = -1;
            return;
        }
        long ws = Volatile.Read(ref *(long*)(_base + 16));
        if (_cursor < 0 || _cursor > ws || _cursor < ws - MaxLag) _cursor = Math.Max(0, ws - TargetLag);

        const long mask = Capacity - 1;
        for (int i = 0; i < frames && _cursor < ws; i++, _cursor++)
        {
            float x = _data[_cursor & mask] * gain;
            stereo[i * 2] += x;
            stereo[i * 2 + 1] += x;
        }
    }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _mmf.Dispose();
    }
}
