// Shared memory layout between the soundboard app (writer) and the APO (readers).
// Must stay byte-identical to app/SharedRing.cs.
#pragma once
#include <stdint.h>

#define SMX_NAME            L"Global\\SoundMixApo_Ring_v1"
#define SMX_MAGIC           0x31584D53u   // "SMX1"
#define SMX_VERSION         1u
#define SMX_RATE            48000u        // ring sample rate
#define SMX_CHANNELS        2u            // ring is interleaved stereo float32
#define SMX_CAPACITY        65536u        // frames, power of two (~1.36 s)
#define SMX_HEADER_SIZE     64u
#define SMX_TOTAL_SIZE      (SMX_HEADER_SIZE + SMX_CAPACITY * SMX_CHANNELS * 4u)

#define SMX_TARGET_LAG      1920          // frames behind writePos the reader aims for (40 ms)
#define SMX_MAX_LAG         9600          // resync if further behind than this (200 ms)
#define SMX_HEARTBEAT_MS    300           // writer considered gone after this

#define SMX_FLAG_DENOISE    0x1u          // run RNNoise on the voice before mixing the soundboard
#define SMX_LEVEL_SHIFT     4             // bits 4-5: denoise level 0=light 1=normal 2=strong
#define SMX_LEVEL_MASK      0x3u
#define SMX_FLAG_VOICE_MON  0x100u        // publish the cleaned voice for the app's "hear myself" option

// Second section: your cleaned voice (mono float, device rate), written by the APO, read by the app.
#define SMX_VOICE_NAME      L"Global\\SoundMixApo_Voice_v1"
#define SMX_VOICE_MAGIC     0x31434F56u   // "VOC1"
#define SMX_VOICE_CAPACITY  65536u        // samples, power of two
#define SMX_VOICE_SIZE      (64u + SMX_VOICE_CAPACITY * 4u)

#pragma pack(push, 1)
struct SmxVoiceHeader
{
    uint32_t magic;                 // 0
    uint32_t sampleRate;            // 4
    uint32_t capacity;              // 8
    uint32_t reserved;              // 12
    volatile int64_t writePos;      // 16 samples ever written
    volatile int64_t ownerId;       // 24 APO instance currently writing (one writer at a time)
    volatile int64_t ownerTick;     // 32 GetTickCount64() of the owner's last write
    uint8_t pad[24];                // 40..63
};
#pragma pack(pop)
static_assert(sizeof(SmxVoiceHeader) == 64, "voice header size");

#pragma pack(push, 1)
struct SmxHeader
{
    uint32_t magic;            // 0
    uint32_t version;          // 4
    uint32_t sampleRate;       // 8
    uint32_t channels;         // 12
    uint32_t capacity;         // 16
    volatile uint32_t flags;   // 20  set by the app: SMX_FLAG_*
    volatile int64_t writePos;        // 24  total frames ever written (monotonic)
    volatile int64_t writerHeartbeat; // 32  GetTickCount64() of last writer update
    volatile int64_t lastReadTick;    // 40  GetTickCount64() of last APO processing pass
    volatile int64_t mixedFrames;     // 48  diagnostics: frames where a non-zero sample was mixed
    volatile int32_t lastState;       // 56  diagnostics: 1=no writer 2=mixed 3=silence 4=caught up
    volatile uint32_t levels;         // 60  peaks for the app's meter: voice (low 16 bits) | output (high 16 bits), 65535 = 1.0
};
#pragma pack(pop)

static_assert(sizeof(SmxHeader) == SMX_HEADER_SIZE, "header size");
