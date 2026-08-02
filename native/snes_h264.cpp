/*
 * snes_h264.cpp — thin C++ wrapper around Cisco OpenH264 for the FFXIV SNES plugin.
 *
 * Exposes 7 flat C functions so the C# plugin never touches OpenH264's
 * C++ vtables or struct layouts directly.  OpenH264 is loaded at runtime
 * via LoadLibrary — no link-time dependency.
 *
 * Build (MSVC x64, from the native/ directory):
 *   cl /O2 /LD /EHsc /I. snes_h264.cpp /Fe:snes_h264.dll
 *
 * The resulting snes_h264.dll + openh264-2.6.0-win64.dll sit next to
 * the plugin DLL.
 */

#include <windows.h>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <new>

#include "wels/codec_api.h"

/* ------------------------------------------------------------------ */
/*  Runtime-loaded OpenH264 functions                                  */
/* ------------------------------------------------------------------ */

typedef int  (*fn_CreateEncoder)(ISVCEncoder **);
typedef void (*fn_DestroyEncoder)(ISVCEncoder *);
typedef long (*fn_CreateDecoder)(ISVCDecoder **);
typedef void (*fn_DestroyDecoder)(ISVCDecoder *);

static fn_CreateEncoder  pCreateEncoder;
static fn_DestroyEncoder pDestroyEncoder;
static fn_CreateDecoder  pCreateDecoder;
static fn_DestroyDecoder pDestroyDecoder;
static HMODULE hOpenH264;

static bool load_openh264()
{
    if (hOpenH264)
        return true;

    // Try loading from the same directory as this DLL first.
    char path[MAX_PATH];
    if (GetModuleFileNameA(NULL, path, MAX_PATH) == 0)
        return false;

    // Get the directory of snes_h264.dll itself.
    HMODULE hSelf = NULL;
    GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       (LPCSTR)&load_openh264, &hSelf);
    if (hSelf && GetModuleFileNameA(hSelf, path, MAX_PATH))
    {
        char *lastSlash = strrchr(path, '\\');
        if (lastSlash) *(lastSlash + 1) = '\0';
        strcat_s(path, MAX_PATH, "openh264-2.6.0-win64.dll");
        hOpenH264 = LoadLibraryA(path);
    }

    // Fallback: standard search paths.
    if (!hOpenH264) hOpenH264 = LoadLibraryA("openh264-2.6.0-win64.dll");
    if (!hOpenH264) hOpenH264 = LoadLibraryA("openh264.dll");
    if (!hOpenH264) return false;

    pCreateEncoder  = (fn_CreateEncoder) GetProcAddress(hOpenH264, "WelsCreateSVCEncoder");
    pDestroyEncoder = (fn_DestroyEncoder)GetProcAddress(hOpenH264, "WelsDestroySVCEncoder");
    pCreateDecoder  = (fn_CreateDecoder) GetProcAddress(hOpenH264, "WelsCreateDecoder");
    pDestroyDecoder = (fn_DestroyDecoder)GetProcAddress(hOpenH264, "WelsDestroyDecoder");

    return pCreateEncoder && pDestroyEncoder && pCreateDecoder && pDestroyDecoder;
}

/* ------------------------------------------------------------------ */
/*  Colour conversion (BT.601 limited range)                           */
/* ------------------------------------------------------------------ */

static inline uint8_t clamp8(int v)
{
    return (uint8_t)(v < 0 ? 0 : (v > 255 ? 255 : v));
}

static void rgba_to_i420(const uint8_t *rgba, int w, int h,
                         uint8_t *yP, uint8_t *uP, uint8_t *vP)
{
    const int uvW = w >> 1;
    for (int r = 0; r < h; r++) {
        for (int c = 0; c < w; c++) {
            const int i = (r * w + c) * 4;
            const int R = rgba[i], G = rgba[i + 1], B = rgba[i + 2];
            yP[r * w + c] = clamp8(((66*R + 129*G + 25*B + 128) >> 8) + 16);
            if (!(r & 1) && !(c & 1)) {
                const int j = (r >> 1) * uvW + (c >> 1);
                uP[j] = clamp8(((-38*R - 74*G + 112*B + 128) >> 8) + 128);
                vP[j] = clamp8(((112*R - 94*G - 18*B + 128) >> 8) + 128);
            }
        }
    }
}

static void i420_to_rgba(const uint8_t *yP, const uint8_t *uP, const uint8_t *vP,
                         int yStr, int uvStr, int w, int h, uint8_t *rgba)
{
    for (int r = 0; r < h; r++) {
        for (int c = 0; c < w; c++) {
            const int y = yP[r * yStr + c] - 16;
            const int u = uP[(r >> 1) * uvStr + (c >> 1)] - 128;
            const int v = vP[(r >> 1) * uvStr + (c >> 1)] - 128;
            const int C = 298 * y;
            const int i = (r * w + c) * 4;
            rgba[i]     = clamp8((C + 409*v + 128) >> 8);
            rgba[i + 1] = clamp8((C - 100*u - 208*v + 128) >> 8);
            rgba[i + 2] = clamp8((C + 516*u + 128) >> 8);
            rgba[i + 3] = 255;
        }
    }
}

/* ------------------------------------------------------------------ */
/*  Encoder                                                            */
/* ------------------------------------------------------------------ */

struct SnesEncoder {
    ISVCEncoder *enc;
    int w, h;
    uint8_t *yuv;      /* I420 scratch */
    uint8_t *bs;       /* bitstream output scratch */
    int bsCap;
};

extern "C" __declspec(dllexport)
void *snes_encoder_create(int width, int height, float fps, int bitrate)
{
    if (!load_openh264()) return nullptr;

    auto *e = new (std::nothrow) SnesEncoder{};
    if (!e) return nullptr;
    e->w = width;
    e->h = height;

    if (pCreateEncoder(&e->enc) != 0 || !e->enc) { delete e; return nullptr; }

    SEncParamBase p{};
    p.iUsageType     = SCREEN_CONTENT_REAL_TIME;
    p.iPicWidth      = width;
    p.iPicHeight     = height;
    p.iTargetBitrate = bitrate;
    p.iRCMode        = RC_BITRATE_MODE;
    p.fMaxFrameRate  = fps;

    if (e->enc->Initialize(&p) != 0) {
        pDestroyEncoder(e->enc);
        delete e;
        return nullptr;
    }

    int quiet = WELS_LOG_QUIET;
    e->enc->SetOption(ENCODER_OPTION_TRACE_LEVEL, &quiet);

    int ySize  = width * height;
    int uvSize = (width >> 1) * (height >> 1);
    e->yuv = (uint8_t *)std::malloc(ySize + uvSize * 2);

    e->bsCap = width * height * 3;
    e->bs = (uint8_t *)std::malloc(e->bsCap);

    return e;
}

extern "C" __declspec(dllexport)
int snes_encoder_encode(void *handle, const uint8_t *rgba,
                        const uint8_t **out, int *out_len, int *frame_type)
{
    auto *e = (SnesEncoder *)handle;
    if (!e || !rgba) return -1;

    const int w = e->w, h = e->h;
    const int ySize  = w * h;
    const int uvSize = (w >> 1) * (h >> 1);
    uint8_t *yP = e->yuv;
    uint8_t *uP = yP + ySize;
    uint8_t *vP = uP + uvSize;

    rgba_to_i420(rgba, w, h, yP, uP, vP);

    SSourcePicture pic{};
    pic.iColorFormat = videoFormatI420;
    pic.iPicWidth    = w;
    pic.iPicHeight   = h;
    pic.iStride[0]   = w;
    pic.iStride[1]   = w >> 1;
    pic.iStride[2]   = w >> 1;
    pic.pData[0]     = yP;
    pic.pData[1]     = uP;
    pic.pData[2]     = vP;

    SFrameBSInfo info{};
    if (e->enc->EncodeFrame(&pic, &info) != 0)
        return -1;

    *frame_type = (int)info.eFrameType;

    if (info.eFrameType == videoFrameTypeSkip || info.iFrameSizeInBytes <= 0) {
        *out = nullptr;
        *out_len = 0;
        return 0;
    }

    int total = info.iFrameSizeInBytes;
    if (total > e->bsCap) {
        e->bsCap = total * 2;
        e->bs = (uint8_t *)std::realloc(e->bs, e->bsCap);
    }

    /* Copy NAL data from all layers into our scratch buffer. */
    int off = 0;
    for (int i = 0; i < info.iLayerNum; i++) {
        SLayerBSInfo *li = &info.sLayerInfo[i];
        int layerBytes = 0;
        for (int n = 0; n < li->iNalCount; n++)
            layerBytes += li->pNalLengthInByte[n];
        std::memcpy(e->bs + off, li->pBsBuf, layerBytes);
        off += layerBytes;
    }

    *out = e->bs;
    *out_len = off;
    return 0;
}

extern "C" __declspec(dllexport)
void snes_encoder_force_keyframe(void *handle)
{
    auto *e = (SnesEncoder *)handle;
    if (e) e->enc->ForceIntraFrame(true);
}

extern "C" __declspec(dllexport)
void snes_encoder_destroy(void *handle)
{
    auto *e = (SnesEncoder *)handle;
    if (!e) return;
    if (e->enc) { e->enc->Uninitialize(); pDestroyEncoder(e->enc); }
    std::free(e->yuv);
    std::free(e->bs);
    delete e;
}

/* ------------------------------------------------------------------ */
/*  Decoder                                                            */
/* ------------------------------------------------------------------ */

struct SnesDecoder {
    ISVCDecoder *dec;
    uint8_t *rgba;
    int rgbaCap;
};

extern "C" __declspec(dllexport)
void *snes_decoder_create(void)
{
    if (!load_openh264()) return nullptr;

    auto *d = new (std::nothrow) SnesDecoder{};
    if (!d) return nullptr;

    if (pCreateDecoder(&d->dec) != 0 || !d->dec) { delete d; return nullptr; }

    SDecodingParam p{};
    p.sVideoProperty.eVideoBsType = VIDEO_BITSTREAM_AVC;

    if (d->dec->Initialize(&p) != 0) {
        pDestroyDecoder(d->dec);
        delete d;
        return nullptr;
    }

    int quiet = WELS_LOG_QUIET;
    d->dec->SetOption(DECODER_OPTION_TRACE_LEVEL, &quiet);

    return d;
}

extern "C" __declspec(dllexport)
int snes_decoder_decode(void *handle, const uint8_t *h264, int h264_len,
                        const uint8_t **rgba_out, int *width, int *height)
{
    auto *d = (SnesDecoder *)handle;
    if (!d) return -1;

    uint8_t *pData[3] = {};
    SBufferInfo buf{};

    DECODING_STATE st = d->dec->DecodeFrameNoDelay(h264, h264_len, pData, &buf);
    if (st != dsErrorFree && st != dsFramePending)
        return -1;
    if (buf.iBufferStatus != 1)
        return 0;

    const int w  = buf.UsrData.sSystemBuffer.iWidth;
    const int h  = buf.UsrData.sSystemBuffer.iHeight;
    const int yS = buf.UsrData.sSystemBuffer.iStride[0];
    const int uS = buf.UsrData.sSystemBuffer.iStride[1];

    int need = w * h * 4;
    if (need > d->rgbaCap) {
        d->rgbaCap = need;
        d->rgba = (uint8_t *)std::realloc(d->rgba, d->rgbaCap);
    }

    i420_to_rgba(pData[0], pData[1], pData[2], yS, uS, w, h, d->rgba);

    *rgba_out = d->rgba;
    *width  = w;
    *height = h;
    return 1;
}

extern "C" __declspec(dllexport)
void snes_decoder_destroy(void *handle)
{
    auto *d = (SnesDecoder *)handle;
    if (!d) return;
    if (d->dec) { d->dec->Uninitialize(); pDestroyDecoder(d->dec); }
    std::free(d->rgba);
    delete d;
}
