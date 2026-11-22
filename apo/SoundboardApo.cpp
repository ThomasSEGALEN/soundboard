// Soundboard APO - mixes the soundboard ring buffer into a microphone capture stream.
// Runs inside audiodg.exe. No injection into games or chat apps.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <objbase.h>
#include <sddl.h>
#include <mmreg.h>
#include <audioenginebaseapo.h>
#include <audiomediatype.h>
#include <atomic>
#include <new>
#include <string.h>
#include <wchar.h>
#include <stdio.h>
#include <stdarg.h>
#include "SharedRing.h"

extern "C" { // RNNOISE_EXPORT is defined empty on the command line (static link, nothing exported)
#include "rnnoise.h"
}

static const UINT32 DN_FRAME = 480;        // RNNoise works on 10 ms frames at 48 kHz
struct DenoiseLevel
{
    float strength;  // exponent on RNNoise band gains: higher removes more non-voice sound while talking
    float floor;     // gate gain when no voice is detected
    float vadOn;     // voice probability that opens the gate
    int   hold;      // 10 ms blocks the gate stays open after the last voice
};
static const DenoiseLevel DN_LEVELS[3] = {
    { 1.0f, 0.25f, 0.60f, 25 },  // light  : original RNNoise, gate -12 dB, 250 ms hold
    { 1.6f, 0.12f, 0.60f, 15 },  // normal : gate -18 dB, 150 ms hold
    { 2.5f, 0.05f, 0.70f, 10 },  // strong : gate -26 dB, 100 ms hold
};

// {7C3E5A21-9B4D-4F6E-A1C2-5D8E3F907B14}
static const CLSID CLSID_SoundboardApo =
    { 0x7c3e5a21, 0x9b4d, 0x4f6e, { 0xa1, 0xc2, 0x5d, 0x8e, 0x3f, 0x90, 0x7b, 0x14 } };
static const wchar_t* CLSID_SoundboardApo_Str = L"{7C3E5A21-9B4D-4F6E-A1C2-5D8E3F907B14}";

// KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
static const GUID SMX_SUBTYPE_FLOAT =
    { 0x00000003, 0x0000, 0x0010, { 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71 } };

static HMODULE g_module = nullptr;
static std::atomic<long> g_objects{ 0 };
static std::atomic<long> g_locks{ 0 };

// Diagnostic log (never called from the real-time path).
static void Log(const char* fmt, ...)
{
    char msg[512];
    SYSTEMTIME st;
    GetLocalTime(&st);
    int n = sprintf_s(msg, "%02u:%02u:%02u.%03u [%lu] ", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
                      GetCurrentProcessId());
    va_list ap;
    va_start(ap, fmt);
    n += vsprintf_s(msg + n, sizeof(msg) - n - 2, fmt, ap);
    va_end(ap);
    msg[n++] = '\r';
    msg[n++] = '\n';
    HANDLE f = CreateFileW(L"C:\\ProgramData\\Soundboard\\logs\\apo.log", FILE_APPEND_DATA,
                           FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return;
    LARGE_INTEGER size;
    if (GetFileSizeEx(f, &size) && size.QuadPart > 512 * 1024) { CloseHandle(f); return; } // cap the log
    DWORD w;
    WriteFile(f, msg, DWORD(n), &w, nullptr);
    CloseHandle(f);
}

static void LogFormat(const char* what, IAudioMediaType* t)
{
    UNCOMPRESSEDAUDIOFORMAT f = {};
    if (!t || FAILED(t->GetUncompressedAudioFormat(&f))) { Log("%s: <none>", what); return; }
    Log("%s: type=%08lX ch=%lu bytes=%lu bits=%lu rate=%.0f", what, f.guidFormatType.Data1,
        f.dwSamplesPerFrame, f.dwBytesPerSampleContainer, f.dwValidBitsPerSample, f.fFramesPerSecond);
}

static APO_REG_PROPERTIES g_regProps = {
    CLSID_SoundboardApo,
    APO_FLAG_DEFAULT, // in-place, frames/sec, bits/sample and samples/frame must match
    L"Soundboard APO",
    L"Soundboard",
    1, 0,             // version
    1, 1, 1, 1,       // connections
    0xFFFFFFFF,       // max instances
    1,
    { __uuidof(IAudioProcessingObject) }
};

static inline float Clip(float v) { return v > 1.0f ? 1.0f : (v < -1.0f ? -1.0f : v); }

static float Peak(const float* p, size_t n)
{
    float m = 0.0f;
    for (size_t i = 0; i < n; i++) { const float a = p[i] < 0 ? -p[i] : p[i]; if (a > m) m = a; }
    return m > 1.0f ? 1.0f : m;
}

static bool GetFloatFormat(IAudioMediaType* t, UNCOMPRESSEDAUDIOFORMAT* out)
{
    if (!t) return false;
    UNCOMPRESSEDAUDIOFORMAT f = {};
    if (FAILED(t->GetUncompressedAudioFormat(&f))) return false;
    if (!IsEqualGUID(f.guidFormatType, SMX_SUBTYPE_FLOAT)) return false;
    if (f.dwBytesPerSampleContainer != 4 || f.dwValidBitsPerSample != 32) return false;
    if (f.dwSamplesPerFrame < 1 || f.dwSamplesPerFrame > 32) return false;
    if (f.fFramesPerSecond < 1000.0f || f.fFramesPerSecond > 768000.0f) return false;
    if (out) *out = f;
    return true;
}

class SoundboardApo final :
    public IAudioProcessingObject,
    public IAudioProcessingObjectRT,
    public IAudioProcessingObjectConfiguration,
    public IAudioSystemEffects2
{
public:
    // The audio engine creates APOs aggregated (pOuter != null), so COM aggregation is required:
    // m_nd is the non-delegating IUnknown that owns the lifetime, every other interface
    // forwards its IUnknown methods to the outer object.
    explicit SoundboardApo(IUnknown* outer)
    {
        m_nd.owner = this;
        m_outer = outer ? outer : static_cast<IUnknown*>(&m_nd);
        g_objects++;
    }
    ~SoundboardApo()
    {
        if (m_dn) rnnoise_destroy(m_dn);
        CloseShared();
        g_objects--;
    }

    struct NonDelegating final : IUnknown
    {
        SoundboardApo* owner = nullptr;
        STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override { return owner->NDQueryInterface(riid, ppv); }
        STDMETHODIMP_(ULONG) AddRef() override { return ++owner->m_ref; }
        STDMETHODIMP_(ULONG) Release() override
        {
            ULONG r = --owner->m_ref;
            if (r == 0) delete owner;
            return r;
        }
    } m_nd;

    // IUnknown (delegating)
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override { return m_outer->QueryInterface(riid, ppv); }
    STDMETHODIMP_(ULONG) AddRef() override { return m_outer->AddRef(); }
    STDMETHODIMP_(ULONG) Release() override { return m_outer->Release(); }

    HRESULT NDQueryInterface(REFIID riid, void** ppv)
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == __uuidof(IUnknown))
        {
            *ppv = static_cast<IUnknown*>(&m_nd);
            m_nd.AddRef();
            return S_OK;
        }
        if (riid == __uuidof(IAudioProcessingObject))
            *ppv = static_cast<IAudioProcessingObject*>(this);
        else if (riid == __uuidof(IAudioProcessingObjectRT))
            *ppv = static_cast<IAudioProcessingObjectRT*>(this);
        else if (riid == __uuidof(IAudioProcessingObjectConfiguration))
            *ppv = static_cast<IAudioProcessingObjectConfiguration*>(this);
        else if (riid == __uuidof(IAudioSystemEffects))
            *ppv = static_cast<IAudioSystemEffects*>(static_cast<IAudioSystemEffects2*>(this));
        else if (riid == __uuidof(IAudioSystemEffects2))
            *ppv = static_cast<IAudioSystemEffects2*>(this);
        else
            return E_NOINTERFACE;
        static_cast<IUnknown*>(*ppv)->AddRef(); // goes to the outer object when aggregated
        return S_OK;
    }

    // IAudioProcessingObject
    STDMETHODIMP Reset() override { m_synced = false; return S_OK; }
    STDMETHODIMP GetLatency(HNSTIME* pTime) override
    {
        if (!pTime) return E_POINTER;
        *pTime = 0;
        return S_OK;
    }
    STDMETHODIMP GetRegistrationProperties(APO_REG_PROPERTIES** ppRegProps) override
    {
        if (!ppRegProps) return E_POINTER;
        auto* p = static_cast<APO_REG_PROPERTIES*>(CoTaskMemAlloc(sizeof(APO_REG_PROPERTIES)));
        if (!p) return E_OUTOFMEMORY;
        memcpy(p, &g_regProps, sizeof(APO_REG_PROPERTIES));
        *ppRegProps = p;
        return S_OK;
    }
    STDMETHODIMP Initialize(UINT32 cbDataSize, BYTE* pbyData) override
    {
        if (pbyData && cbDataSize == sizeof(APOInitSystemEffects2))
        {
            auto* init = reinterpret_cast<APOInitSystemEffects2*>(pbyData);
            wchar_t mode[64];
            StringFromGUID2(init->AudioProcessingMode, mode, 64);
            Log("Initialize mode=%ls discoveryOnly=%d", mode, init->InitializeForDiscoveryOnly);
        }
        else
            Log("Initialize cb=%u", cbDataSize);
        if (m_locked) return APOERR_APO_LOCKED;
        m_initialized = true;
        return S_OK;
    }
    STDMETHODIMP IsInputFormatSupported(IAudioMediaType* pOpposite, IAudioMediaType* pRequested,
                                        IAudioMediaType** ppSupported) override
    {
        return CheckFormat(pOpposite, pRequested, ppSupported);
    }
    STDMETHODIMP IsOutputFormatSupported(IAudioMediaType* pOpposite, IAudioMediaType* pRequested,
                                         IAudioMediaType** ppSupported) override
    {
        return CheckFormat(pOpposite, pRequested, ppSupported);
    }
    STDMETHODIMP GetInputChannelCount(UINT32* pu32ChannelCount) override
    {
        if (!pu32ChannelCount) return E_POINTER;
        *pu32ChannelCount = m_channels;
        return S_OK;
    }

    // IAudioProcessingObjectRT
    STDMETHODIMP_(void) APOProcess(UINT32 numIn, APO_CONNECTION_PROPERTY** ppIn,
                                   UINT32 numOut, APO_CONNECTION_PROPERTY** ppOut) override
    {
        if (numIn < 1 || numOut < 1 || !ppIn || !ppOut) return;
        APO_CONNECTION_PROPERTY* in = ppIn[0];
        APO_CONNECTION_PROPERTY* out = ppOut[0];
        const UINT32 frames = in->u32ValidFrameCount;
        float* src = reinterpret_cast<float*>(in->pBuffer);
        float* dst = reinterpret_cast<float*>(out->pBuffer);
        const bool inputSilent = in->u32BufferFlags != BUFFER_VALID;

        if (!inputSilent && dst != src)
            memcpy(dst, src, size_t(frames) * m_channels * sizeof(float));

        // 1) clean the voice, 2) add the soundboard on top so the denoiser never touches it
        bool voice = !inputSilent;
        if (m_locked && Denoise(dst, frames, inputSilent)) voice = true;
        const size_t samples = size_t(frames) * m_channels;
        const float voicePeak = voice ? Peak(dst, samples) : 0.0f;
        if (m_locked) PublishVoice(dst, frames, voice); // "hear myself": the voice as others get it, without the sounds
        const bool mixed = m_locked && Mix(dst, frames, !voice);
        if (m_shared)
        {
            const float outPeak = (voice || mixed) ? Peak(dst, samples) : 0.0f;
            m_shared->levels = uint32_t(voicePeak * 65535.0f) | (uint32_t(outPeak * 65535.0f) << 16);
        }

        out->u32ValidFrameCount = frames;
        out->u32BufferFlags = (!voice && !mixed) ? BUFFER_SILENT : BUFFER_VALID;
    }
    STDMETHODIMP_(UINT32) CalcInputFrames(UINT32 u32OutputFrameCount) override { return u32OutputFrameCount; }
    STDMETHODIMP_(UINT32) CalcOutputFrames(UINT32 u32InputFrameCount) override { return u32InputFrameCount; }

    // IAudioProcessingObjectConfiguration
    STDMETHODIMP LockForProcess(UINT32 numIn, APO_CONNECTION_DESCRIPTOR** ppIn,
                                UINT32 numOut, APO_CONNECTION_DESCRIPTOR** ppOut) override
    {
        Log("LockForProcess in=%u out=%u", numIn, numOut);
        if (m_locked) return APOERR_APO_LOCKED;
        if (numIn != 1 || numOut != 1 || !ppIn || !ppOut || !ppIn[0] || !ppOut[0])
            return APOERR_NUM_CONNECTIONS_INVALID;
        LogFormat("  in", ppIn[0]->pFormat);
        UNCOMPRESSEDAUDIOFORMAT f = {};
        if (!GetFloatFormat(ppIn[0]->pFormat, &f) || !GetFloatFormat(ppOut[0]->pFormat, nullptr))
        {
            Log("  -> format refused");
            return APOERR_FORMAT_NOT_SUPPORTED;
        }
        m_channels = f.dwSamplesPerFrame;
        m_step = double(SMX_RATE) / double(f.fFramesPerSecond);
        m_synced = false;
        m_rate = UINT32(f.fFramesPerSecond);
        OpenShared(); // failure is not fatal: the APO then passes audio through untouched
        OpenVoice();
        // RNNoise only supports 48 kHz; at other rates the voice is left untouched.
        if (f.fFramesPerSecond == 48000.0f && !m_dn)
        {
            m_dn = rnnoise_create(nullptr);
            // Warm-up: RNNoise allocates its FFT tables lazily on the first frame;
            // do it here instead of on the real-time thread.
            if (m_dn) rnnoise_process_frame(m_dn, m_dnOut, m_dnIn);
        }
        ResetDenoise();
        Log("  denoiser %s", m_dn ? "ready" : "unavailable");
        m_locked = true;
        return S_OK;
    }
    STDMETHODIMP UnlockForProcess() override
    {
        if (!m_locked) return APOERR_ALREADY_UNLOCKED;
        m_locked = false;
        m_synced = false;
        return S_OK;
    }

    // IAudioSystemEffects2
    STDMETHODIMP GetEffectsList(LPGUID* ppEffectsIds, UINT* pcEffects, HANDLE) override
    {
        if (!ppEffectsIds || !pcEffects) return E_POINTER;
        *ppEffectsIds = nullptr;
        *pcEffects = 0;
        return S_OK;
    }

private:
    HRESULT CheckFormat(IAudioMediaType* pOpposite, IAudioMediaType* pRequested, IAudioMediaType** ppSupported)
    {
        if (!pRequested || !ppSupported) return E_POINTER;
        *ppSupported = nullptr;
        if (m_locked) return APOERR_APO_LOCKED;
        if (!GetFloatFormat(pRequested, nullptr)) return APOERR_FORMAT_NOT_SUPPORTED;
        if (pOpposite)
        {
            DWORD flags = 0;
            const DWORD needed = AUDIOMEDIATYPE_EQUAL_FORMAT_TYPES | AUDIOMEDIATYPE_EQUAL_FORMAT_DATA;
            if (FAILED(pRequested->IsEqual(pOpposite, &flags)) || (flags & needed) != needed)
            {
                // In-place APO: input and output must match. Suggest the opposite format.
                if (!GetFloatFormat(pOpposite, nullptr)) return APOERR_FORMAT_NOT_SUPPORTED;
                pOpposite->AddRef();
                *ppSupported = pOpposite;
                return S_FALSE;
            }
        }
        pRequested->AddRef();
        *ppSupported = pRequested;
        return S_OK;
    }

    void OpenShared()
    {
        if (m_shared) return;
        PSECURITY_DESCRIPTOR sd = nullptr;
        // SYSTEM, LocalService, Administrators: full. Interactive users: read/write. Low IL allowed.
        ConvertStringSecurityDescriptorToSecurityDescriptorW(
            L"D:(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;BA)(A;;GRGW;;;AU)S:(ML;;NW;;;LW)",
            SDDL_REVISION_1, &sd, nullptr);
        SECURITY_ATTRIBUTES sa = { sizeof(sa), sd, FALSE };
        HANDLE h = CreateFileMappingW(INVALID_HANDLE_VALUE, sd ? &sa : nullptr, PAGE_READWRITE,
                                      0, SMX_TOTAL_SIZE, SMX_NAME);
        DWORD err = GetLastError();
        if (sd) LocalFree(sd);
        Log("  CreateFileMapping h=%p err=%lu", h, err);
        if (!h) h = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, SMX_NAME);
        if (!h) { Log("  OpenFileMapping failed err=%lu", GetLastError()); return; }
        void* view = MapViewOfFile(h, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, SMX_TOTAL_SIZE);
        if (!view) { Log("  MapViewOfFile failed err=%lu", GetLastError()); CloseHandle(h); return; }
        auto* hdr = static_cast<SmxHeader*>(view);
        if (hdr->magic != SMX_MAGIC)
        {
            hdr->version = SMX_VERSION;
            hdr->sampleRate = SMX_RATE;
            hdr->channels = SMX_CHANNELS;
            hdr->capacity = SMX_CAPACITY;
            MemoryBarrier();
            hdr->magic = SMX_MAGIC;
        }
        m_map = h;
        m_shared = hdr;
        m_ring = reinterpret_cast<const float*>(static_cast<BYTE*>(view) + SMX_HEADER_SIZE);
    }

    void OpenVoice()
    {
        if (m_voice) return;
        PSECURITY_DESCRIPTOR sd = nullptr;
        ConvertStringSecurityDescriptorToSecurityDescriptorW(
            L"D:(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;BA)(A;;GR;;;AU)S:(ML;;NW;;;LW)", // users: read only
            SDDL_REVISION_1, &sd, nullptr);
        SECURITY_ATTRIBUTES sa = { sizeof(sa), sd, FALSE };
        HANDLE h = CreateFileMappingW(INVALID_HANDLE_VALUE, sd ? &sa : nullptr, PAGE_READWRITE,
                                      0, SMX_VOICE_SIZE, SMX_VOICE_NAME);
        if (sd) LocalFree(sd);
        if (!h) h = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, SMX_VOICE_NAME);
        if (!h) { Log("  voice section unavailable err=%lu", GetLastError()); return; }
        void* view = MapViewOfFile(h, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, SMX_VOICE_SIZE);
        if (!view) { CloseHandle(h); return; }
        auto* vh = static_cast<SmxVoiceHeader*>(view);
        if (vh->magic != SMX_VOICE_MAGIC)
        {
            vh->capacity = SMX_VOICE_CAPACITY;
            MemoryBarrier();
            vh->magic = SMX_VOICE_MAGIC;
        }
        m_vmap = h;
        m_voice = vh;
        m_vdata = reinterpret_cast<float*>(static_cast<BYTE*>(view) + 64);
    }

    // Real-time path. One APO instance at a time owns the voice ring (several instances may run).
    void PublishVoice(const float* dst, UINT32 frames, bool voice)
    {
        SmxVoiceHeader* v = m_voice;
        if (!v || !m_shared || (m_shared->flags & SMX_FLAG_VOICE_MON) == 0) return;
        const LONG64 me = LONG64(reinterpret_cast<UINT_PTR>(this));
        const LONG64 now = LONG64(GetTickCount64());
        const LONG64 owner = ReadAcquire64(&v->ownerId);
        if (owner != me)
        {
            if (owner != 0 && now - ReadAcquire64(&v->ownerTick) < 300) return; // someone else is writing
            WriteRelease64(&v->ownerId, me);
        }
        WriteNoFence64(&v->ownerTick, now);
        v->sampleRate = m_rate;

        const UINT32 ch = m_channels;
        const float inv = 1.0f / float(ch);
        LONG64 pos = ReadAcquire64(&v->writePos);
        const LONG64 mask = SMX_VOICE_CAPACITY - 1;
        for (UINT32 i = 0; i < frames; i++)
        {
            float x = 0.0f;
            if (voice)
                for (UINT32 c = 0; c < ch; c++) x += dst[size_t(i) * ch + c];
            m_vdata[(pos + i) & mask] = x * inv;
        }
        WriteRelease64(&v->writePos, pos + frames);
    }

    void CloseShared()
    {
        if (m_voice)
        {
            if (m_voice->ownerId == LONG64(reinterpret_cast<UINT_PTR>(this))) m_voice->ownerId = 0;
            UnmapViewOfFile(m_voice);
        }
        if (m_vmap) CloseHandle(m_vmap);
        m_voice = nullptr;
        m_vdata = nullptr;
        m_vmap = nullptr;
        if (m_shared) UnmapViewOfFile(m_shared);
        if (m_map) CloseHandle(m_map);
        m_shared = nullptr;
        m_ring = nullptr;
        m_map = nullptr;
    }

    void ResetDenoise()
    {
        memset(m_dnIn, 0, sizeof(m_dnIn));
        memset(m_dnOut, 0, sizeof(m_dnOut));
        m_dnPos = 0;
        m_gate = 1.0f;
        m_hold = 0;
    }

    // Real-time path. Runs RNNoise on the voice with a fixed 10 ms delay, plus a soft
    // gate that lowers keyboard/background noise between sentences.
    // Returns true when dst now holds voice data.
    bool Denoise(float* dst, UINT32 frames, bool inputSilent)
    {
        const bool on = m_dn && m_shared && m_shared->magic == SMX_MAGIC &&
                        (m_shared->flags & SMX_FLAG_DENOISE) != 0;
        if (!on)
        {
            if (m_dnActive) { ResetDenoise(); m_dnActive = false; }
            return false;
        }
        m_dnActive = true;

        UINT32 lvl = (m_shared->flags >> SMX_LEVEL_SHIFT) & SMX_LEVEL_MASK;
        if (lvl > 2) lvl = 2;
        const DenoiseLevel& L = DN_LEVELS[lvl];
        if (lvl != m_dnLevel)
        {
            rnnoise_set_strength(m_dn, L.strength); // plain float store, real-time safe
            m_dnLevel = lvl;
        }

        const UINT32 ch = m_channels;
        if (inputSilent) memset(dst, 0, size_t(frames) * ch * sizeof(float));
        const float inv = 1.0f / float(ch);

        for (UINT32 i = 0; i < frames; i++)
        {
            float* o = dst + size_t(i) * ch;
            float x = 0.0f;
            for (UINT32 c = 0; c < ch; c++) x += o[c];
            m_dnIn[m_dnPos] = x * inv * 32768.0f;
            const float y = m_dnOut[m_dnPos] * (1.0f / 32768.0f);
            for (UINT32 c = 0; c < ch; c++) o[c] = y;

            if (++m_dnPos == DN_FRAME)
            {
                const float vad = rnnoise_process_frame(m_dn, m_dnOut, m_dnIn);
                float target;
                if (vad >= L.vadOn)  { m_hold = L.hold; target = 1.0f; }
                else if (m_hold > 0) { m_hold--; target = 1.0f; }
                else                 target = L.floor;
                // open fast, close smoothly; ramp across the block to avoid clicks
                const float next = target > m_gate ? target : m_gate + (target - m_gate) * 0.2f;
                for (UINT32 k = 0; k < DN_FRAME; k++)
                    m_dnOut[k] *= m_gate + (next - m_gate) * (float(k) / DN_FRAME);
                m_gate = next;
                m_dnPos = 0;
            }
        }
        return true;
    }

    // Real-time path: no locks, no allocations.
    bool Mix(float* dst, UINT32 frames, bool zeroFirst)
    {
        SmxHeader* s = m_shared;
        if (!s || s->magic != SMX_MAGIC) return false;

        const LONG64 now = (LONG64)GetTickCount64();
        WriteNoFence64(&s->lastReadTick, now);

        if (now - ReadAcquire64(&s->writerHeartbeat) > SMX_HEARTBEAT_MS)
        {
            m_synced = false;
            s->lastState = 1;
            return false;
        }

        const LONG64 ws = ReadAcquire64(&s->writePos);
        if (!m_synced || m_cursor > double(ws) || m_cursor < double(ws - SMX_MAX_LAG))
        {
            m_cursor = double(ws - SMX_TARGET_LAG);
            if (m_cursor < 0) m_cursor = 0;
            m_synced = true;
        }

        if (zeroFirst) memset(dst, 0, size_t(frames) * m_channels * sizeof(float));

        const UINT32 ch = m_channels;
        const LONG64 mask = SMX_CAPACITY - 1;
        const float* ring = m_ring;
        bool any = false;
        LONG64 mixedCount = 0;
        int32_t state = 3;

        for (UINT32 i = 0; i < frames; i++)
        {
            const LONG64 i0 = (LONG64)m_cursor;
            if (i0 + 1 >= ws) { state = 4; break; } // never read past what the writer published
            const float fr = float(m_cursor - double(i0));
            const float* a = ring + ((i0) & mask) * 2;
            const float* b = ring + ((i0 + 1) & mask) * 2;
            const float l = a[0] + (b[0] - a[0]) * fr;
            const float r = a[1] + (b[1] - a[1]) * fr;
            m_cursor += m_step;
            if (l == 0.0f && r == 0.0f) continue;
            any = true;
            mixedCount++;

            float* o = dst + size_t(i) * ch;
            if (ch == 2)
            {
                o[0] = Clip(o[0] + l);
                o[1] = Clip(o[1] + r);
            }
            else
            {
                const float m = 0.5f * (l + r);
                for (UINT32 c = 0; c < ch; c++) o[c] = Clip(o[c] + m);
            }
        }
        if (any && state == 3) state = 2;
        s->lastState = state;
        if (mixedCount) InterlockedAdd64(&s->mixedFrames, mixedCount);
        return any;
    }

    DenoiseState* m_dn = nullptr;
    bool m_dnActive = false;
    UINT32 m_dnLevel = 0xFFFFFFFF;
    float m_dnIn[DN_FRAME] = {};
    float m_dnOut[DN_FRAME] = {};
    UINT32 m_dnPos = 0;
    float m_gate = 1.0f;
    int m_hold = 0;

    std::atomic<ULONG> m_ref{ 1 };
    IUnknown* m_outer = nullptr;
    bool m_initialized = false;
    bool m_locked = false;
    UINT32 m_channels = 2;
    double m_step = 1.0;
    double m_cursor = 0.0;
    bool m_synced = false;
    HANDLE m_map = nullptr;
    SmxHeader* m_shared = nullptr;
    const float* m_ring = nullptr;
    HANDLE m_vmap = nullptr;
    SmxVoiceHeader* m_voice = nullptr;
    float* m_vdata = nullptr;
    UINT32 m_rate = 48000;
};

// ---------------------------------------------------------------- class factory

class SoundboardFactory final : public IClassFactory
{
public:
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IClassFactory))
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { g_locks++; return 2; }
    STDMETHODIMP_(ULONG) Release() override { g_locks--; return 1; }
    STDMETHODIMP CreateInstance(IUnknown* pOuter, REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        // COM rule: an aggregated object can only be asked for IUnknown at creation.
        if (pOuter && riid != __uuidof(IUnknown)) return CLASS_E_NOAGGREGATION;
        auto* apo = new (std::nothrow) SoundboardApo(pOuter);
        if (!apo) return E_OUTOFMEMORY;
        HRESULT hr = apo->m_nd.QueryInterface(riid, ppv);
        apo->m_nd.Release();
        return hr;
    }
    STDMETHODIMP LockServer(BOOL lock) override
    {
        if (lock) g_locks++; else g_locks--;
        return S_OK;
    }
};

static SoundboardFactory g_factory;

// ---------------------------------------------------------------- registration helpers

static LONG SetStr(HKEY k, const wchar_t* name, const wchar_t* v)
{
    return RegSetValueExW(k, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(v),
                          DWORD((wcslen(v) + 1) * sizeof(wchar_t)));
}
static LONG SetDw(HKEY k, const wchar_t* name, DWORD v)
{
    return RegSetValueExW(k, name, 0, REG_DWORD, reinterpret_cast<const BYTE*>(&v), sizeof(v));
}

static HRESULT RegisterServer()
{
    wchar_t path[MAX_PATH];
    if (!GetModuleFileNameW(g_module, path, MAX_PATH)) return HRESULT_FROM_WIN32(GetLastError());

    wchar_t keyPath[256];
    HKEY k = nullptr;

    // COM class
    swprintf_s(keyPath, L"SOFTWARE\\Classes\\CLSID\\%s", CLSID_SoundboardApo_Str);
    LONG r = RegCreateKeyExW(HKEY_LOCAL_MACHINE, keyPath, 0, nullptr, 0, KEY_WRITE, nullptr, &k, nullptr);
    if (r != ERROR_SUCCESS) return HRESULT_FROM_WIN32(r);
    SetStr(k, nullptr, L"Soundboard APO");
    RegCloseKey(k);

    swprintf_s(keyPath, L"SOFTWARE\\Classes\\CLSID\\%s\\InprocServer32", CLSID_SoundboardApo_Str);
    r = RegCreateKeyExW(HKEY_LOCAL_MACHINE, keyPath, 0, nullptr, 0, KEY_WRITE, nullptr, &k, nullptr);
    if (r != ERROR_SUCCESS) return HRESULT_FROM_WIN32(r);
    SetStr(k, nullptr, path);
    SetStr(k, L"ThreadingModel", L"Both");
    RegCloseKey(k);

    // APO registration (same layout Windows uses for its own APOs)
    swprintf_s(keyPath, L"SOFTWARE\\Classes\\AudioEngine\\AudioProcessingObjects\\%s", CLSID_SoundboardApo_Str);
    r = RegCreateKeyExW(HKEY_LOCAL_MACHINE, keyPath, 0, nullptr, 0, KEY_WRITE, nullptr, &k, nullptr);
    if (r != ERROR_SUCCESS) return HRESULT_FROM_WIN32(r);
    wchar_t iid[64];
    StringFromGUID2(__uuidof(IAudioProcessingObject), iid, 64);
    SetStr(k, L"FriendlyName", g_regProps.szFriendlyName);
    SetStr(k, L"Copyright", g_regProps.szCopyrightInfo);
    SetDw(k, L"MajorVersion", g_regProps.u32MajorVersion);
    SetDw(k, L"MinorVersion", g_regProps.u32MinorVersion);
    SetDw(k, L"Flags", g_regProps.Flags);
    SetDw(k, L"MinInputConnections", g_regProps.u32MinInputConnections);
    SetDw(k, L"MaxInputConnections", g_regProps.u32MaxInputConnections);
    SetDw(k, L"MinOutputConnections", g_regProps.u32MinOutputConnections);
    SetDw(k, L"MaxOutputConnections", g_regProps.u32MaxOutputConnections);
    SetDw(k, L"MaxInstances", g_regProps.u32MaxInstances);
    SetDw(k, L"NumAPOInterfaces", 1);
    SetStr(k, L"APOInterface0", iid);
    RegCloseKey(k);
    return S_OK;
}

static HRESULT UnregisterServer()
{
    wchar_t keyPath[256];
    swprintf_s(keyPath, L"SOFTWARE\\Classes\\CLSID\\%s", CLSID_SoundboardApo_Str);
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, keyPath);
    swprintf_s(keyPath, L"SOFTWARE\\Classes\\AudioEngine\\AudioProcessingObjects\\%s", CLSID_SoundboardApo_Str);
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, keyPath);
    return S_OK;
}

// ---------------------------------------------------------------- exports

BOOL WINAPI DllMain(HINSTANCE hInst, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = hInst;
        DisableThreadLibraryCalls(hInst);
        wchar_t exe[MAX_PATH] = L"";
        GetModuleFileNameW(nullptr, exe, MAX_PATH);
        Log("DLL loaded in %ls", exe);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (!IsEqualCLSID(rclsid, CLSID_SoundboardApo)) return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, ppv);
}

STDAPI DllCanUnloadNow()
{
    return (g_objects == 0 && g_locks == 0) ? S_OK : S_FALSE;
}

STDAPI DllRegisterServer() { return RegisterServer(); }
STDAPI DllUnregisterServer() { return UnregisterServer(); }
