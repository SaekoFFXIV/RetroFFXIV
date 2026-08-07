using System;

namespace RetroXIV.Emulation;

// A hardware presentation context handed to cores that cannot present through
// retro_video_refresh (LRPS2 renders on the GPU and displays into whatever
// render target the frontend has bound). The frontend owns the device; the
// core receives it through GET_HW_RENDER_INTERFACE.
public interface IHwRenderContext
{
    // retro_hw_context_type this provider can offer (e.g. HwContextD3D11).
    int ContextType { get; }

    // Writes the API-specific retro_hw_render_interface pointer for the core:
    // data is a retro_hw_render_interface** slot the frontend stores a pointer
    // to its (pinned, session-lifetime) interface struct in.
    bool FillInterface(IntPtr data);

    // Binds the presentation render target before retro_run.
    void BeginFrame(int width, int height);

    // Reads the presented frame back as RGBA32 after retro_run.
    bool TryReadFrame(out byte[] rgba, out int width, out int height);
}
