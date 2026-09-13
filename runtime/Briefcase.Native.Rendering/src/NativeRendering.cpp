#include "NativeRendering.h"

#include <MinHook.h>
#include <d3d11.h>
#include <dxgi.h>
#include <imgui.h>
#include <imgui_impl_dx11.h>

#include <Windows.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>

namespace {
using PresentFn = HRESULT(__stdcall*)(IDXGISwapChain*, UINT, UINT);
using ResizeBuffersFn = HRESULT(__stdcall*)(
    IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);

struct OverlayFrameCopy {
    std::uint32_t Width{};
    std::uint32_t Height{};
    std::uint64_t FrameNumber{};
    std::vector<BriefcaseOverlayCommand> Commands;
    std::vector<char> Text;
};

std::atomic<BriefcaseRenderingStatus> Status{BRIEFCASE_RENDERING_UNAVAILABLE};
std::atomic_uint32_t NativeError{};
std::mutex FrameMutex;
std::shared_ptr<const OverlayFrameCopy> LatestFrame;
std::mutex RenderMutex;
PresentFn OriginalPresent{};
ResizeBuffersFn OriginalResizeBuffers{};
IDXGISwapChain* ActiveSwapChain{};
ID3D11Device* Device{};
ID3D11DeviceContext* DeviceContext{};
ID3D11RenderTargetView* RenderTarget{};
ImGuiContext* Context{};
std::chrono::steady_clock::time_point PreviousPresent{};

void releaseRenderer() noexcept {
    if (Context) {
        ImGui::SetCurrentContext(Context);
        ImGui_ImplDX11_Shutdown();
        ImGui::DestroyContext(Context);
        Context = nullptr;
    }
    if (RenderTarget) {
        RenderTarget->Release();
        RenderTarget = nullptr;
    }
    if (DeviceContext) {
        DeviceContext->Release();
        DeviceContext = nullptr;
    }
    if (Device) {
        Device->Release();
        Device = nullptr;
    }
    ActiveSwapChain = nullptr;
}

bool isUnrealGameSwapChain(IDXGISwapChain* swapChain) noexcept {
    DXGI_SWAP_CHAIN_DESC description{};
    if (!swapChain || FAILED(swapChain->GetDesc(&description)) ||
        !description.OutputWindow) return false;
    DWORD processId{};
    GetWindowThreadProcessId(description.OutputWindow, &processId);
    if (processId != GetCurrentProcessId()) return false;
    wchar_t className[64]{};
    return GetClassNameW(description.OutputWindow, className,
                         static_cast<int>(std::size(className))) > 0 &&
           wcscmp(className, L"UnrealWindow") == 0;
}

bool createRenderTarget(IDXGISwapChain* swapChain) noexcept {
    ID3D11Texture2D* backBuffer{};
    const auto result = swapChain->GetBuffer(
        0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&backBuffer));
    if (FAILED(result) || !backBuffer) return false;
    const auto targetResult = Device->CreateRenderTargetView(
        backBuffer, nullptr, &RenderTarget);
    backBuffer->Release();
    return SUCCEEDED(targetResult) && RenderTarget;
}

bool initializeRenderer(IDXGISwapChain* swapChain) noexcept {
    releaseRenderer();
    if (FAILED(swapChain->GetDevice(
            __uuidof(ID3D11Device), reinterpret_cast<void**>(&Device))) || !Device)
        return false;
    Device->GetImmediateContext(&DeviceContext);
    if (!DeviceContext || !createRenderTarget(swapChain)) {
        releaseRenderer();
        return false;
    }

    IMGUI_CHECKVERSION();
    Context = ImGui::CreateContext();
    if (!Context) {
        releaseRenderer();
        return false;
    }
    ImGui::SetCurrentContext(Context);
    auto& io = ImGui::GetIO();
    io.IniFilename = nullptr;
    io.LogFilename = nullptr;
    if (!ImGui_ImplDX11_Init(Device, DeviceContext)) {
        releaseRenderer();
        return false;
    }
    ActiveSwapChain = swapChain;
    PreviousPresent = std::chrono::steady_clock::now();
    return true;
}

std::shared_ptr<const OverlayFrameCopy> snapshotFrame() {
    const std::scoped_lock lock(FrameMutex);
    return LatestFrame;
}

void drawFrame(const OverlayFrameCopy& frame, UINT width, UINT height) noexcept {
    if (!Context || frame.Commands.empty() || frame.Width == 0 || frame.Height == 0)
        return;
    ImGui::SetCurrentContext(Context);
    const auto now = std::chrono::steady_clock::now();
    const float delta = std::clamp(
        std::chrono::duration<float>(now - PreviousPresent).count(), 0.001f, 0.25f);
    PreviousPresent = now;
    auto& io = ImGui::GetIO();
    io.DisplaySize = ImVec2(static_cast<float>(width), static_cast<float>(height));
    io.DeltaTime = delta;
    ImGui_ImplDX11_NewFrame();
    ImGui::NewFrame();

    auto* drawing = ImGui::GetBackgroundDrawList();
    const float scaleX = static_cast<float>(width) / static_cast<float>(frame.Width);
    const float scaleY = static_cast<float>(height) / static_cast<float>(frame.Height);
    const float scale = std::min(scaleX, scaleY);
    for (const auto& command : frame.Commands) {
        const ImU32 color = command.Color;
        switch (command.Kind) {
        case BRIEFCASE_OVERLAY_CIRCLE: {
            const auto center = ImVec2(command.X1 * scaleX, command.Y1 * scaleY);
            const float radius = std::max(0.0f, command.Radius * scale);
            const int segments = std::clamp(command.Segments, 0, 512);
            if ((command.Flags & BRIEFCASE_OVERLAY_FILLED) != 0)
                drawing->AddCircleFilled(center, radius, color, segments);
            else
                drawing->AddCircle(center, radius, color, segments,
                                   std::max(0.1f, command.Thickness * scale));
            break;
        }
        case BRIEFCASE_OVERLAY_LINE:
            drawing->AddLine(
                ImVec2(command.X1 * scaleX, command.Y1 * scaleY),
                ImVec2(command.X2 * scaleX, command.Y2 * scaleY), color,
                std::max(0.1f, command.Thickness * scale));
            break;
        case BRIEFCASE_OVERLAY_FILLED_RECTANGLE:
            drawing->AddRectFilled(
                ImVec2(command.X1 * scaleX, command.Y1 * scaleY),
                ImVec2(command.X2 * scaleX, command.Y2 * scaleY), color,
                std::max(0.0f, command.Rounding * scale));
            break;
        case BRIEFCASE_OVERLAY_TEXT: {
            const std::size_t begin = command.TextOffset;
            const std::size_t end = begin + command.TextLength;
            if (end <= frame.Text.size())
                drawing->AddText(ImVec2(command.X1 * scaleX, command.Y1 * scaleY),
                                 color, frame.Text.data() + begin, frame.Text.data() + end);
            break;
        }
        default:
            break;
        }
    }

    ImGui::Render();
    ID3D11RenderTargetView* previousTarget{};
    ID3D11DepthStencilView* previousDepth{};
    DeviceContext->OMGetRenderTargets(1, &previousTarget, &previousDepth);
    DeviceContext->OMSetRenderTargets(1, &RenderTarget, nullptr);
    ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());
    DeviceContext->OMSetRenderTargets(1, &previousTarget, previousDepth);
    if (previousDepth) previousDepth->Release();
    if (previousTarget) previousTarget->Release();
}

HRESULT __stdcall hookedPresent(IDXGISwapChain* swapChain, UINT syncInterval,
                                UINT flags) noexcept {
    if (isUnrealGameSwapChain(swapChain)) {
        const std::scoped_lock lock(RenderMutex);
        if ((ActiveSwapChain == swapChain || initializeRenderer(swapChain)) &&
            (!RenderTarget && createRenderTarget(swapChain) || RenderTarget)) {
            DXGI_SWAP_CHAIN_DESC description{};
            if (SUCCEEDED(swapChain->GetDesc(&description))) {
                UINT width = description.BufferDesc.Width;
                UINT height = description.BufferDesc.Height;
                if (width == 0 || height == 0) {
                    RECT client{};
                    if (GetClientRect(description.OutputWindow, &client)) {
                        width = static_cast<UINT>(std::max<LONG>(1, client.right));
                        height = static_cast<UINT>(std::max<LONG>(1, client.bottom));
                    }
                }
                if (const auto frame = snapshotFrame())
                    drawFrame(*frame, std::max(1u, width), std::max(1u, height));
            }
        }
    }
    return OriginalPresent(swapChain, syncInterval, flags);
}

HRESULT __stdcall hookedResizeBuffers(IDXGISwapChain* swapChain, UINT bufferCount,
                                      UINT width, UINT height, DXGI_FORMAT format,
                                      UINT flags) noexcept {
    const std::scoped_lock lock(RenderMutex);
    if (swapChain == ActiveSwapChain && RenderTarget) {
        RenderTarget->Release();
        RenderTarget = nullptr;
        ImGui::SetCurrentContext(Context);
        ImGui_ImplDX11_InvalidateDeviceObjects();
    }
    return OriginalResizeBuffers(
        swapChain, bufferCount, width, height, format, flags);
}

bool installHooks() noexcept {
    constexpr wchar_t windowClass[] = L"Briefcase.Native.Rendering.Probe";
    WNDCLASSW definition{};
    definition.lpfnWndProc = DefWindowProcW;
    definition.hInstance = GetModuleHandleW(nullptr);
    definition.lpszClassName = windowClass;
    RegisterClassW(&definition);
    const HWND window = CreateWindowExW(
        0, windowClass, L"", WS_OVERLAPPED, 0, 0, 2, 2,
        nullptr, nullptr, definition.hInstance, nullptr);
    if (!window) {
        NativeError.store(GetLastError(), std::memory_order_release);
        return false;
    }

    DXGI_SWAP_CHAIN_DESC description{};
    description.BufferDesc.Width = 2;
    description.BufferDesc.Height = 2;
    description.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    description.SampleDesc.Count = 1;
    description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    description.BufferCount = 1;
    description.OutputWindow = window;
    description.Windowed = TRUE;
    description.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
    ID3D11Device* device{};
    ID3D11DeviceContext* context{};
    IDXGISwapChain* swapChain{};
    D3D_FEATURE_LEVEL featureLevel{};
    const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_0};
    const HRESULT created = D3D11CreateDeviceAndSwapChain(
        nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, levels, 1,
        D3D11_SDK_VERSION, &description, &swapChain, &device,
        &featureLevel, &context);
    if (FAILED(created) || !swapChain) {
        NativeError.store(static_cast<std::uint32_t>(created), std::memory_order_release);
        if (context) context->Release();
        if (device) device->Release();
        DestroyWindow(window);
        UnregisterClassW(windowClass, definition.hInstance);
        return false;
    }

    void** methods = *reinterpret_cast<void***>(swapChain);
    void* presentAddress = methods[8];
    void* resizeAddress = methods[13];
    const auto initialized = MH_Initialize();
    bool success = initialized == MH_OK || initialized == MH_ERROR_ALREADY_INITIALIZED;
    success = success && MH_CreateHook(
        presentAddress, reinterpret_cast<void*>(&hookedPresent),
        reinterpret_cast<void**>(&OriginalPresent)) == MH_OK;
    success = success && MH_CreateHook(
        resizeAddress, reinterpret_cast<void*>(&hookedResizeBuffers),
        reinterpret_cast<void**>(&OriginalResizeBuffers)) == MH_OK;
    success = success && MH_EnableHook(presentAddress) == MH_OK;
    success = success && MH_EnableHook(resizeAddress) == MH_OK;
    if (!success)
        NativeError.store(0xBFC00001u, std::memory_order_release);

    swapChain->Release();
    if (context) context->Release();
    if (device) device->Release();
    DestroyWindow(window);
    UnregisterClassW(windowClass, definition.hInstance);
    return success;
}

BriefcaseBool BRIEFCASE_MOD_CALL start(void*) noexcept {
    auto expected = BRIEFCASE_RENDERING_UNAVAILABLE;
    if (!Status.compare_exchange_strong(expected, BRIEFCASE_RENDERING_STARTING))
        return expected == BRIEFCASE_RENDERING_STARTING ||
               expected == BRIEFCASE_RENDERING_READY;
    std::thread([] {
        const bool installed = installHooks();
        Status.store(installed ? BRIEFCASE_RENDERING_READY : BRIEFCASE_RENDERING_FAILED,
                     std::memory_order_release);
    }).detach();
    return 1;
}

BriefcaseRenderingResult BRIEFCASE_MOD_CALL submit(
    void*, const BriefcaseOverlayFrame* frame) noexcept {
    if (!frame || frame->StructSize < sizeof(BriefcaseOverlayFrame) ||
        frame->Version != BRIEFCASE_RENDERING_API_VERSION ||
        frame->Width == 0 || frame->Height == 0 ||
        frame->Width > 32768 || frame->Height > 32768 ||
        frame->CommandCount > BRIEFCASE_OVERLAY_MAX_COMMANDS ||
        frame->TextBytes > BRIEFCASE_OVERLAY_MAX_TEXT_BYTES ||
        (frame->CommandCount != 0 && !frame->Commands) ||
        (frame->TextBytes != 0 && !frame->Utf8Text))
        return BRIEFCASE_RENDERING_INVALID_ARGUMENT;

    try {
        auto copy = std::make_shared<OverlayFrameCopy>();
        copy->Width = frame->Width;
        copy->Height = frame->Height;
        copy->FrameNumber = frame->FrameNumber;
        if (frame->TextBytes != 0)
            copy->Text.assign(frame->Utf8Text, frame->Utf8Text + frame->TextBytes);
        copy->Commands.reserve(frame->CommandCount);
        for (std::uint32_t index = 0; index < frame->CommandCount; ++index) {
            const auto& command = frame->Commands[index];
            const bool finite = std::isfinite(command.X1) && std::isfinite(command.Y1) &&
                std::isfinite(command.X2) && std::isfinite(command.Y2) &&
                std::isfinite(command.Radius) && std::isfinite(command.Thickness) &&
                std::isfinite(command.Rounding);
            const std::uint64_t textEnd =
                static_cast<std::uint64_t>(command.TextOffset) + command.TextLength;
            if (command.StructSize < sizeof(BriefcaseOverlayCommand) || !finite ||
                command.Kind > BRIEFCASE_OVERLAY_TEXT ||
                (command.Flags & ~BRIEFCASE_OVERLAY_FILLED) != 0 ||
                (command.Kind == BRIEFCASE_OVERLAY_TEXT && textEnd > frame->TextBytes))
                return BRIEFCASE_RENDERING_INVALID_ARGUMENT;
            copy->Commands.push_back(command);
        }
        const std::scoped_lock lock(FrameMutex);
        LatestFrame = std::move(copy);
        return BRIEFCASE_RENDERING_OK;
    } catch (...) {
        return BRIEFCASE_RENDERING_NOT_READY;
    }
}

BriefcaseRenderingStatus BRIEFCASE_MOD_CALL getStatus(
    void*, std::uint32_t* nativeError) noexcept {
    if (nativeError) *nativeError = NativeError.load(std::memory_order_acquire);
    return Status.load(std::memory_order_acquire);
}

const BriefcaseRenderingApi RenderingApi{
    sizeof(BriefcaseRenderingApi), BRIEFCASE_RENDERING_API_VERSION, nullptr,
    start, submit, getStatus, {}};
} // namespace

const BriefcaseRenderingApi* BRIEFCASE_MOD_CALL BriefcaseGetRenderingApi() {
    return &RenderingApi;
}
