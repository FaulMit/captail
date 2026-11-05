#include <Windows.h>
#include <d3d11.h>
#include <dxgi1_6.h>
#include <d2d1_3.h>
#include <d2d1effects_2.h>
#include <wrl/client.h>
#include <cstring>
#include <limits>
#include <new>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace {
float sdr_white_level(const DXGI_OUTPUT_DESC &output)
{
    UINT path_count = 0, mode_count = 0;
    if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &path_count, &mode_count) != ERROR_SUCCESS)
        return 200.0f;
    std::vector<DISPLAYCONFIG_PATH_INFO> paths(path_count);
    std::vector<DISPLAYCONFIG_MODE_INFO> modes(mode_count);
    if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &path_count, paths.data(),
        &mode_count, modes.data(), nullptr) != ERROR_SUCCESS)
        return 200.0f;
    for (UINT index = 0; index < path_count; ++index) {
        const auto &path = paths[index];
        DISPLAYCONFIG_SOURCE_DEVICE_NAME source{};
        source.header = {DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, sizeof(source),
            path.sourceInfo.adapterId, path.sourceInfo.id};
        if (DisplayConfigGetDeviceInfo(&source.header) != ERROR_SUCCESS ||
            wcscmp(source.viewGdiDeviceName, output.DeviceName) != 0)
            continue;
        DISPLAYCONFIG_SDR_WHITE_LEVEL level{};
        level.header = {DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL, sizeof(level),
            path.targetInfo.adapterId, path.targetInfo.id};
        if (DisplayConfigGetDeviceInfo(&level.header) == ERROR_SUCCESS && level.SDRWhiteLevel > 0)
            return static_cast<float>(level.SDRWhiteLevel) * 0.08f;
    }
    return 200.0f;
}

HRESULT convert_to_sdr(ID3D11Device *device, ID3D11DeviceContext *context,
    IDXGIOutput1 *output, ID3D11Texture2D *source, ComPtr<ID3D11Texture2D> &target)
{
    // Use Windows' HDR tone mapper and color management, rather than clipping scRGB values.
    D3D11_TEXTURE2D_DESC desc{};
    source->GetDesc(&desc);
    desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.CPUAccessFlags = 0;
    desc.MiscFlags = 0;
    ComPtr<ID3D11Texture2D> source_copy;
    HRESULT result = device->CreateTexture2D(&desc, nullptr, &source_copy);
    if (FAILED(result)) return result;
    context->CopyResource(source_copy.Get(), source);
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    result = device->CreateTexture2D(&desc, nullptr, &target);
    if (FAILED(result)) return result;

    ComPtr<ID2D1Factory1> factory;
    result = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, factory.GetAddressOf());
    if (FAILED(result)) return result;
    ComPtr<IDXGIDevice> dxgi_device;
    result = device->QueryInterface(IID_PPV_ARGS(&dxgi_device));
    if (FAILED(result)) return result;
    ComPtr<ID2D1Device> d2d_device;
    result = factory->CreateDevice(dxgi_device.Get(), &d2d_device);
    if (FAILED(result)) return result;
    ComPtr<ID2D1DeviceContext> d2d;
    result = d2d_device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &d2d);
    if (FAILED(result)) return result;
    ComPtr<IDXGISurface> source_surface, target_surface;
    result = source_copy.As(&source_surface);
    if (FAILED(result)) return result;
    result = target.As(&target_surface);
    if (FAILED(result)) return result;
    ComPtr<ID2D1Bitmap1> source_bitmap, target_bitmap;
    auto source_properties = D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_NONE,
        D2D1::PixelFormat(DXGI_FORMAT_R16G16B16A16_FLOAT, D2D1_ALPHA_MODE_IGNORE));
    result = d2d->CreateBitmapFromDxgiSurface(source_surface.Get(), &source_properties, &source_bitmap);
    if (FAILED(result)) return result;
    auto properties = D2D1::BitmapProperties1(
        D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_IGNORE));
    result = d2d->CreateBitmapFromDxgiSurface(target_surface.Get(), &properties, &target_bitmap);
    if (FAILED(result)) return result;

    ComPtr<ID2D1Effect> color_management;
    result = d2d->CreateEffect(CLSID_D2D1ColorManagement, &color_management);
    if (FAILED(result)) return result;
    ComPtr<ID2D1DeviceContext5> d2d5;
    result = d2d.As(&d2d5);
    if (FAILED(result)) return result;
    ComPtr<ID2D1ColorContext1> source_color, target_color;
    result = d2d5->CreateColorContextFromDxgiColorSpace(DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709, &source_color);
    if (FAILED(result)) return result;
    result = d2d5->CreateColorContextFromDxgiColorSpace(DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709, &target_color);
    if (FAILED(result)) return result;
    result = color_management->SetValue(D2D1_COLORMANAGEMENT_PROP_SOURCE_COLOR_CONTEXT, source_color.Get());
    if (FAILED(result)) return result;
    result = color_management->SetValue(D2D1_COLORMANAGEMENT_PROP_DESTINATION_COLOR_CONTEXT, target_color.Get());
    if (FAILED(result)) return result;
    result = color_management->SetValue(D2D1_COLORMANAGEMENT_PROP_QUALITY, D2D1_COLORMANAGEMENT_QUALITY_BEST);
    if (FAILED(result)) return result;
    result = color_management->SetValue(D2D1_COLORMANAGEMENT_PROP_DESTINATION_RENDERING_INTENT,
        D2D1_COLORMANAGEMENT_RENDERING_INTENT_ABSOLUTE_COLORIMETRIC);
    if (FAILED(result)) return result;

    ComPtr<IDXGIOutput6> output6;
    DXGI_OUTPUT_DESC1 output_info{};
    bool hdr = SUCCEEDED(output->QueryInterface(IID_PPV_ARGS(&output6))) &&
        SUCCEEDED(output6->GetDesc1(&output_info)) &&
        output_info.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
    ComPtr<ID2D1Effect> white_adjustment, tone_map;
    if (hdr) {
        DXGI_OUTPUT_DESC output_desc{};
        result = output->GetDesc(&output_desc);
        if (FAILED(result)) return result;
        float white = sdr_white_level(output_desc);
        result = d2d->CreateEffect(CLSID_D2D1WhiteLevelAdjustment, &white_adjustment);
        if (FAILED(result)) return result;
        result = white_adjustment->SetValue(D2D1_WHITELEVELADJUSTMENT_PROP_INPUT_WHITE_LEVEL, 200.0f);
        if (FAILED(result)) return result;
        result = white_adjustment->SetValue(D2D1_WHITELEVELADJUSTMENT_PROP_OUTPUT_WHITE_LEVEL, white);
        if (FAILED(result)) return result;
        white_adjustment->SetInput(0, source_bitmap.Get());
        result = d2d->CreateEffect(CLSID_D2D1HdrToneMap, &tone_map);
        if (FAILED(result)) return result;
        result = tone_map->SetValue(D2D1_HDRTONEMAP_PROP_INPUT_MAX_LUMINANCE,
            (output_info.MaxLuminance > 0 ? output_info.MaxLuminance : 1000.0f) * 200.0f / white);
        if (FAILED(result)) return result;
        result = tone_map->SetValue(D2D1_HDRTONEMAP_PROP_OUTPUT_MAX_LUMINANCE, 80.0f);
        if (FAILED(result)) return result;
        result = tone_map->SetValue(D2D1_HDRTONEMAP_PROP_DISPLAY_MODE, D2D1_HDRTONEMAP_DISPLAY_MODE_SDR);
        if (FAILED(result)) return result;
        tone_map->SetInputEffect(0, white_adjustment.Get());
        color_management->SetInputEffect(0, tone_map.Get());
    } else {
        color_management->SetInput(0, source_bitmap.Get());
    }
    d2d->SetTarget(target_bitmap.Get());
    d2d->SetRenderingControls({D2D1_BUFFER_PRECISION_16BPC_FLOAT, {0, 0}});
    d2d->BeginDraw();
    d2d->DrawImage(color_management.Get());
    return d2d->EndDraw();
}

HRESULT capture_monitor(HMONITOR monitor, void **pixels, int *width, int *height, int *format)
{
    ComPtr<IDXGIFactory1> factory;
    HRESULT result = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
    if (FAILED(result))
        return result;

    ComPtr<IDXGIAdapter1> adapter;
    ComPtr<IDXGIOutput1> selected;
    DXGI_OUTPUT_DESC output_desc{};
    for (UINT adapter_index = 0; !selected; ++adapter_index) {
        result = factory->EnumAdapters1(adapter_index, &adapter);
        if (result == DXGI_ERROR_NOT_FOUND)
            return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        if (FAILED(result))
            return result;
        for (UINT output_index = 0;; ++output_index) {
            ComPtr<IDXGIOutput> output;
            result = adapter->EnumOutputs(output_index, &output);
            if (result == DXGI_ERROR_NOT_FOUND)
                break;
            if (FAILED(result))
                return result;
            result = output->GetDesc(&output_desc);
            if (FAILED(result))
                return result;
            if (output_desc.AttachedToDesktop && output_desc.Monitor == monitor) {
                result = output.As(&selected);
                if (FAILED(result))
                    return result;
                break;
            }
        }
    }

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    result = D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
        &device, nullptr, &context);
    if (FAILED(result))
        return result;

    // Windows may supply BGRA8 or linear scRGB on an HDR desktop.
    ComPtr<IDXGIOutputDuplication> duplication;
    ComPtr<IDXGIOutput5> output5;
    if (SUCCEEDED(selected.As(&output5))) {
        const DXGI_FORMAT formats[] = {DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT_B8G8R8A8_UNORM};
        result = output5->DuplicateOutput1(device.Get(), 0, 2, formats, &duplication);
    } else {
        result = selected->DuplicateOutput(device.Get(), &duplication);
    }
    if (FAILED(result))
        return result;

    ComPtr<IDXGIResource> resource;
    DXGI_OUTDUPL_FRAME_INFO frame_info{};
    const ULONGLONG deadline = GetTickCount64() + 750;
    do {
        result = duplication->AcquireNextFrame(250, &frame_info, &resource);
        if (FAILED(result) && result != DXGI_ERROR_WAIT_TIMEOUT)
            return result;
        if (SUCCEEDED(result)) {
            if (frame_info.LastPresentTime.QuadPart != 0)
                break;
            // A pointer-only initial update does not contain a usable desktop image.
            duplication->ReleaseFrame();
            resource.Reset();
        }
    } while (GetTickCount64() < deadline);
    if (!resource)
        return DXGI_ERROR_WAIT_TIMEOUT;
    struct FrameGuard {
        IDXGIOutputDuplication *duplication;
        ~FrameGuard() { duplication->ReleaseFrame(); }
    } frame_guard{duplication.Get()};

    ComPtr<ID3D11Texture2D> texture;
    result = resource.As(&texture);
    if (FAILED(result))
        return result;
    D3D11_TEXTURE2D_DESC desc{};
    texture->GetDesc(&desc);
    *format = static_cast<int>(desc.Format);
    if (desc.Format == DXGI_FORMAT_R16G16B16A16_FLOAT) {
        ComPtr<ID3D11Texture2D> sdr;
        result = convert_to_sdr(device.Get(), context.Get(), selected.Get(), texture.Get(), sdr);
        if (FAILED(result)) return result;
        texture = sdr;
        texture->GetDesc(&desc);
    }
    if (desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM ||
        desc.Width == 0 || desc.Height == 0 || desc.Width > 32768 || desc.Height > 32768)
        return E_UNEXPECTED;

    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    desc.MiscFlags = 0;
    ComPtr<ID3D11Texture2D> staging;
    result = device->CreateTexture2D(&desc, nullptr, &staging);
    if (FAILED(result))
        return result;
    context->CopyResource(staging.Get(), texture.Get());
    D3D11_MAPPED_SUBRESOURCE mapped{};
    result = context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped);
    if (FAILED(result))
        return result;
    struct MapGuard {
        ID3D11DeviceContext *context;
        ID3D11Texture2D *texture;
        ~MapGuard() { context->Unmap(texture, 0); }
    } map_guard{context.Get(), staging.Get()};

    const bool portrait = output_desc.Rotation == DXGI_MODE_ROTATION_ROTATE90 ||
                          output_desc.Rotation == DXGI_MODE_ROTATION_ROTATE270;
    const UINT target_width = portrait ? desc.Height : desc.Width;
    const UINT target_height = portrait ? desc.Width : desc.Height;
    const size_t size = static_cast<size_t>(target_width) * target_height * 4;
    if (size > static_cast<size_t>(std::numeric_limits<int>::max()))
        return E_OUTOFMEMORY;
    auto *buffer = static_cast<unsigned char *>(CoTaskMemAlloc(size));
    if (!buffer)
        return E_OUTOFMEMORY;

    const auto *source = static_cast<const unsigned char *>(mapped.pData);
    for (UINT y = 0; y < target_height; ++y) {
        for (UINT x = 0; x < target_width; ++x) {
            UINT sx = x, sy = y;
            switch (output_desc.Rotation) {
            case DXGI_MODE_ROTATION_ROTATE90: sx = y; sy = desc.Height - 1 - x; break;
            case DXGI_MODE_ROTATION_ROTATE180: sx = desc.Width - 1 - x; sy = desc.Height - 1 - y; break;
            case DXGI_MODE_ROTATION_ROTATE270: sx = desc.Width - 1 - y; sy = x; break;
            default: break;
            }
            auto *destination = buffer + (static_cast<size_t>(y) * target_width + x) * 4;
            std::memcpy(destination, source + static_cast<size_t>(sy) * mapped.RowPitch + sx * 4, 4);
            destination[3] = 255;
        }
    }
    *pixels = buffer;
    *width = static_cast<int>(target_width);
    *height = static_cast<int>(target_height);
    return S_OK;
}
} // namespace

extern "C" __declspec(dllexport) HRESULT __cdecl
captail_capture_monitor(HMONITOR monitor, void **pixels, int *width, int *height, int *format)
{
    if (!monitor || !pixels || !width || !height || !format)
        return E_INVALIDARG;
    *pixels = nullptr;
    *width = *height = 0;
    *format = 0;
    try {
        return capture_monitor(monitor, pixels, width, height, format);
    } catch (const std::bad_alloc &) {
        return E_OUTOFMEMORY;
    } catch (...) {
        return E_FAIL;
    }
}
