using System.IO.MemoryMappedFiles;

namespace Soundboard;

/// <summary>
/// Writer side of the shared ring buffer read by the Soundboard APO (see apo/SharedRing.h).
/// The APO creates the section when a program opens the microphone; we only open it.
/// </summary>
sealed unsafe class SharedRing : IDisposable
{
    public const string Name = @"Global\SoundMixApo_Ring_v1";
    const uint Magic = 0x31584D53;
    public const int Capacity = 65536;   // frames
    const int HeaderSize = 64;
    const long TotalSize = HeaderSize + Capacity * 2L * 4L;

    const int OffFlags = 20;
    public const uint FlagDenoise = 0x1;
    public const int LevelShift = 4;        // bits 4-5: denoise level
    public const uint FlagVoiceMonitor = 0x100; // ask the APO to publish the cleaned voice
    const int OffWritePos = 24;
    const int OffHeartbeat = 32;
    const int OffLastRead = 40;

    readonly MemoryMappedFile _mmf;
    readonly MemoryMappedViewAccessor _view;
    readonly byte* _base;
    readonly float* _ring;

    SharedRing(MemoryMappedFile mmf, MemoryMappedViewAccessor view, byte* basePtr)
    {
        _mmf = mmf;
        _view = view;
        _base = basePtr;
        _ring = (float*)(basePtr + HeaderSize);
    }

    public static SharedRing? TryOpen()
    {
        MemoryMappedFile? mmf = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            mmf = MemoryMappedFile.OpenExisting(Name, MemoryMappedFileRights.ReadWrite);
            view = mmf.CreateViewAccessor(0, TotalSize, MemoryMappedFileAccess.ReadWrite);
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            p += view.PointerOffset;
            if (*(uint*)p != Magic)
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
                view.Dispose();
                mmf.Dispose();
                return null;
            }
            return new SharedRing(mmf, view, p);
        }
        catch
        {
            view?.Dispose();
            mmf?.Dispose();
            return null;
        }
    }

    /// <summary>Appends interleaved stereo frames and publishes them to the APO.</summary>
    public void Write(float[] stereo, int frames)
    {
        long pos = Volatile.Read(ref *(long*)(_base + OffWritePos));
        const long mask = Capacity - 1;
        for (int i = 0; i < frames; i++)
        {
            long idx = ((pos + i) & mask) * 2;
            _ring[idx] = stereo[i * 2];
            _ring[idx + 1] = stereo[i * 2 + 1];
        }
        Volatile.Write(ref *(long*)(_base + OffWritePos), pos + frames);
        Volatile.Write(ref *(long*)(_base + OffHeartbeat), Environment.TickCount64);
    }

    /// <summary>Options read by the APO (e.g. voice noise suppression).</summary>
    public uint Flags
    {
        get => Volatile.Read(ref *(uint*)(_base + OffFlags));
        set => Volatile.Write(ref *(uint*)(_base + OffFlags), value);
    }

    const int OffLevels = 60;

    /// <summary>Peaks written by the APO: voice after noise suppression, and the final output.</summary>
    public (float Voice, float Output) ReadLevels()
    {
        uint v = Volatile.Read(ref *(uint*)(_base + OffLevels));
        return ((v & 0xFFFF) / 65535f, (v >> 16) / 65535f);
    }

    /// <summary>True when the APO processed a microphone buffer in the last half second.</summary>
    public bool ReaderActive =>
        Environment.TickCount64 - Volatile.Read(ref *(long*)(_base + OffLastRead)) < 500;

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _mmf.Dispose();
    }
}
