#include <Windows.h>
#include <chrono>
#include <d3d11.h>
#include <dxgi.h>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

namespace {
LRESULT CALLBACK window_proc(HWND window, UINT message, WPARAM wparam, LPARAM lparam)
{
    if (message == WM_DESTROY) {
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(window, message, wparam, lparam);
}
}

int WINAPI wWinMain(
    _In_ HINSTANCE instance,
    _In_opt_ HINSTANCE,
    _In_ PWSTR,
    _In_ int show)
{
    constexpr int client_width = 2560;
    constexpr int client_height = 1440;
    constexpr DWORD window_style = WS_POPUP;

    WNDCLASSW window_class{};
    window_class.hInstance = instance;
    window_class.lpfnWndProc = window_proc;
    window_class.lpszClassName = L"CaptailObsCaptureFixture";
    window_class.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));
    if (!RegisterClassW(&window_class))
        return 1;

    RECT window_rect{0, 0, client_width, client_height};
    if (!AdjustWindowRect(&window_rect, window_style, FALSE))
        return 2;

    HWND window = CreateWindowExW(
        WS_EX_TOPMOST,
        window_class.lpszClassName,
        L"Captail OBS Capture Fixture",
        window_style,
        0,
        0,
        window_rect.right - window_rect.left,
        window_rect.bottom - window_rect.top,
        nullptr,
        nullptr,
        instance,
        nullptr);
    if (!window)
        return 2;

    DXGI_SWAP_CHAIN_DESC swap_desc{};
    swap_desc.BufferCount = 2;
    swap_desc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    swap_desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    swap_desc.OutputWindow = window;
    swap_desc.SampleDesc.Count = 1;
    swap_desc.Windowed = TRUE;
    swap_desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    swap_desc.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING;

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IDXGISwapChain> swap_chain;
    D3D_FEATURE_LEVEL level{};
    HRESULT result = D3D11CreateDeviceAndSwapChain(
        nullptr,
        D3D_DRIVER_TYPE_HARDWARE,
        nullptr,
        0,
        nullptr,
        0,
        D3D11_SDK_VERSION,
        &swap_desc,
        &swap_chain,
        &device,
        &level,
        &context);
    if (FAILED(result))
        return 3;

    ComPtr<ID3D11Texture2D> back_buffer;
    ComPtr<ID3D11RenderTargetView> render_target;
    result = swap_chain->GetBuffer(0, IID_PPV_ARGS(&back_buffer));
    if (FAILED(result) || FAILED(device->CreateRenderTargetView(
                              back_buffer.Get(), nullptr, &render_target)))
        return 4;

    D3D11_TEXTURE2D_DESC stress_desc{};
    back_buffer->GetDesc(&stress_desc);
    stress_desc.BindFlags = 0;
    stress_desc.MiscFlags = 0;
    ComPtr<ID3D11Texture2D> stress_texture;
    if (FAILED(device->CreateTexture2D(&stress_desc, nullptr, &stress_texture)))
        return 5;

    ShowWindow(window, show);
    uint32_t frame = 0;
    constexpr auto frame_interval = std::chrono::nanoseconds(1'000'000'000 / 240);
    auto next_frame = std::chrono::steady_clock::now();
    auto measurement_start = next_frame;
    uint32_t measurement_frames = 0;
    MSG message{};
    while (message.message != WM_QUIT) {
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }

        frame++;
        const uint32_t color_bits = frame * 1664525u + 1013904223u;
        const float color[] = {
            static_cast<float>(color_bits & 0xffu) / 255.0f,
            static_cast<float>((color_bits >> 8) & 0xffu) / 255.0f,
            static_cast<float>((color_bits >> 16) & 0xffu) / 255.0f,
            1.0f,
        };
        context->ClearRenderTargetView(render_target.Get(), color);
        for (int pass = 0; pass < 32; pass++) {
            context->CopyResource(stress_texture.Get(), back_buffer.Get());
            context->CopyResource(back_buffer.Get(), stress_texture.Get());
        }
        swap_chain->Present(0, DXGI_PRESENT_ALLOW_TEARING);
        measurement_frames++;
        const auto measurement_now = std::chrono::steady_clock::now();
        const auto measurement_elapsed = measurement_now - measurement_start;
        if (measurement_elapsed >= std::chrono::seconds(1)) {
            const double measured_fps = measurement_frames /
                std::chrono::duration<double>(measurement_elapsed).count();
            wchar_t title[96]{};
            swprintf_s(title, L"Captail Capture Fixture - %.1f FPS", measured_fps);
            SetWindowTextW(window, title);
            measurement_start = measurement_now;
            measurement_frames = 0;
        }
        next_frame += frame_interval;
        while (std::chrono::steady_clock::now() < next_frame)
            YieldProcessor();
        auto now = std::chrono::steady_clock::now();
        if (now - next_frame > frame_interval)
            next_frame = now;
    }
    return 0;
}
