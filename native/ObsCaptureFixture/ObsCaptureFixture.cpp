#include <Windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <wrl/client.h>
#include <thread>

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

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int show)
{
    WNDCLASSW window_class{};
    window_class.hInstance = instance;
    window_class.lpfnWndProc = window_proc;
    window_class.lpszClassName = L"CaptailObsCaptureFixture";
    window_class.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));
    if (!RegisterClassW(&window_class))
        return 1;

    HWND window = CreateWindowExW(
        0,
        window_class.lpszClassName,
        L"Captail OBS Capture Fixture",
        WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT,
        CW_USEDEFAULT,
        1280,
        720,
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

    ShowWindow(window, show);
    uint32_t frame = 0;
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
        swap_chain->Present(0, DXGI_PRESENT_ALLOW_TEARING);
        std::this_thread::yield();
    }
    return 0;
}
