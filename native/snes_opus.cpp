/*
 * snes_opus.cpp — thin C wrapper around libopus for the FFXIV SNES plugin.
 *
 * Exposes 6 flat C functions so the C# plugin never touches libopus
 * types directly.  libopus is loaded at runtime via LoadLibrary — no
 * link-time dependency.
 *
 * Build (MSVC x64, from the native/ directory):
 *   cl /O2 /LD /EHsc /MT /I. snes_opus.cpp /Fe:snes_opus.dll
 *
 * The resulting snes_opus.dll + opus.dll sit next to the plugin DLL.
 */

#include <windows.h>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <new>

#include "opus/opus.h"

/* ------------------------------------------------------------------ */
/*  Runtime-loaded libopus functions                                   */
/* ------------------------------------------------------------------ */

typedef OpusEncoder *(*fn_opus_encoder_create)(opus_int32, int, int, int *);
typedef void         (*fn_opus_encoder_destroy)(OpusEncoder *);
typedef opus_int32   (*fn_opus_encoder_ctl)(OpusEncoder *, int, ...);
typedef opus_int32   (*fn_opus_encode)(OpusEncoder *, const opus_int16 *, int, unsigned char *, opus_int32);
typedef OpusDecoder *(*fn_opus_decoder_create)(opus_int32, int, int *);
typedef void         (*fn_opus_decoder_destroy)(OpusDecoder *);
typedef int          (*fn_opus_decode)(OpusDecoder *, const unsigned char *, opus_int32, opus_int16 *, int, int);

static fn_opus_encoder_create  pOpusEncoderCreate;
static fn_opus_encoder_destroy pOpusEncoderDestroy;
static fn_opus_encoder_ctl     pOpusEncoderCtl;
static fn_opus_encode          pOpusEncode;
static fn_opus_decoder_create  pOpusDecoderCreate;
static fn_opus_decoder_destroy pOpusDecoderDestroy;
static fn_opus_decode          pOpusDecode;
static HMODULE hOpus;

static bool load_opus()
{
    if (hOpus)
        return true;

    // Try loading from the same directory as this DLL first.
    char path[MAX_PATH];
    HMODULE hSelf = NULL;
    GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       (LPCSTR)&load_opus, &hSelf);
    if (hSelf && GetModuleFileNameA(hSelf, path, MAX_PATH))
    {
        char *lastSlash = strrchr(path, '\\');
        if (lastSlash) *(lastSlash + 1) = '\0';
        strcat_s(path, MAX_PATH, "opus.dll");
        hOpus = LoadLibraryA(path);
    }

    // Fallback: standard search paths.
    if (!hOpus) hOpus = LoadLibraryA("opus.dll");
    if (!hOpus) return false;

    pOpusEncoderCreate  = (fn_opus_encoder_create) GetProcAddress(hOpus, "opus_encoder_create");
    pOpusEncoderDestroy = (fn_opus_encoder_destroy)GetProcAddress(hOpus, "opus_encoder_destroy");
    pOpusEncoderCtl     = (fn_opus_encoder_ctl)    GetProcAddress(hOpus, "opus_encoder_ctl");
    pOpusEncode         = (fn_opus_encode)         GetProcAddress(hOpus, "opus_encode");
    pOpusDecoderCreate  = (fn_opus_decoder_create) GetProcAddress(hOpus, "opus_decoder_create");
    pOpusDecoderDestroy = (fn_opus_decoder_destroy)GetProcAddress(hOpus, "opus_decoder_destroy");
    pOpusDecode         = (fn_opus_decode)         GetProcAddress(hOpus, "opus_decode");

    return pOpusEncoderCreate && pOpusEncoderDestroy && pOpusEncoderCtl &&
           pOpusEncode && pOpusDecoderCreate && pOpusDecoderDestroy && pOpusDecode;
}

/* ------------------------------------------------------------------ */
/*  Encoder                                                            */
/* ------------------------------------------------------------------ */

/* Largest possible Opus packet for a single frame. */
#define SNES_OPUS_MAX_PACKET 4000

struct SnesOpusEncoder {
    OpusEncoder *enc;
    uint8_t packet[SNES_OPUS_MAX_PACKET];
};

extern "C" __declspec(dllexport)
void *snes_opus_encoder_create(int sample_rate, int channels, int bitrate)
{
    if (!load_opus()) return nullptr;

    int err = 0;
    OpusEncoder *enc = pOpusEncoderCreate(sample_rate, channels,
                                          OPUS_APPLICATION_AUDIO, &err);
    if (err != OPUS_OK || !enc) return nullptr;

    // Complexity defaults to 10 (best quality).  The ctl call is the only
    // way to set the target bitrate; the varargs macro in opus.h expands
    // to this same exported function.
    if (pOpusEncoderCtl(enc, OPUS_SET_BITRATE_REQUEST, (opus_int32)bitrate) != OPUS_OK) {
        pOpusEncoderDestroy(enc);
        return nullptr;
    }

    auto *e = new (std::nothrow) SnesOpusEncoder{};
    if (!e) { pOpusEncoderDestroy(enc); return nullptr; }
    e->enc = enc;
    return e;
}

/* Encode one frame of interleaved int16 PCM.  frame_samples is per channel
 * and must be a valid Opus frame size for the sample rate (e.g. 960 = 20 ms
 * at 48 kHz).  Returns 0 on success (*out/*out_len set), -1 on error. */
extern "C" __declspec(dllexport)
int snes_opus_encode(void *handle, const int16_t *pcm, int frame_samples,
                     const uint8_t **out, int *out_len)
{
    auto *e = (SnesOpusEncoder *)handle;
    if (!e || !pcm || frame_samples <= 0) return -1;

    opus_int32 n = pOpusEncode(e->enc, pcm, frame_samples,
                               e->packet, SNES_OPUS_MAX_PACKET);
    if (n < 0) return -1;

    *out = e->packet;
    *out_len = (int)n;
    return 0;
}

extern "C" __declspec(dllexport)
void snes_opus_encoder_destroy(void *handle)
{
    auto *e = (SnesOpusEncoder *)handle;
    if (!e) return;
    if (e->enc) pOpusEncoderDestroy(e->enc);
    delete e;
}

/* ------------------------------------------------------------------ */
/*  Decoder                                                            */
/* ------------------------------------------------------------------ */

struct SnesOpusDecoder {
    OpusDecoder *dec;
};

extern "C" __declspec(dllexport)
void *snes_opus_decoder_create(int sample_rate, int channels)
{
    if (!load_opus()) return nullptr;

    int err = 0;
    OpusDecoder *dec = pOpusDecoderCreate(sample_rate, channels, &err);
    if (err != OPUS_OK || !dec) return nullptr;

    auto *d = new (std::nothrow) SnesOpusDecoder{};
    if (!d) { pOpusDecoderDestroy(dec); return nullptr; }
    d->dec = dec;
    return d;
}

/* Decode one Opus packet into interleaved int16 PCM.  max_frame_samples is
 * the per-channel capacity of pcm_out (e.g. 5760 = 120 ms at 48 kHz).
 * Returns the number of decoded samples per channel, or -1 on error. */
extern "C" __declspec(dllexport)
int snes_opus_decode(void *handle, const uint8_t *data, int len,
                     int16_t *pcm_out, int max_frame_samples)
{
    auto *d = (SnesOpusDecoder *)handle;
    if (!d || !pcm_out || max_frame_samples <= 0) return -1;

    int n = pOpusDecode(d->dec, data, len, pcm_out, max_frame_samples, 0);
    if (n < 0) return -1;
    return n;
}

extern "C" __declspec(dllexport)
void snes_opus_decoder_destroy(void *handle)
{
    auto *d = (SnesOpusDecoder *)handle;
    if (!d) return;
    if (d->dec) pOpusDecoderDestroy(d->dec);
    delete d;
}
