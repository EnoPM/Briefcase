#pragma once

#include <cstddef>
#include <cstdint>
#include <string_view>

namespace briefcase::profile {

enum class TargetKind : std::uint32_t {
    Client = 1,
    Server = 2
};

// Profiles identify supported executables. Unreal symbols are deliberately
// absent: RuntimeSymbolResolver discovers them from bounded PE signatures.
struct RuntimeProfile {
    TargetKind Target;
    std::wstring_view TargetName;
    std::wstring_view SdkAssemblyName;
    std::uint32_t PeTimestamp;
    std::uint32_t ImageSize;
};

// Public client installed from Steam build 16480294.
inline constexpr RuntimeProfile PublicClient{
    TargetKind::Client,
    L"Client",
    L"Briefcase.DeceiveInc.Client.Sdk",
    0x673E81A6,
    0x06699000
};

// September 2026 dedicated-server-preview client retained for development and
// rollback testing.
inline constexpr RuntimeProfile PreviewClient{
    TargetKind::Client,
    L"Client",
    L"Briefcase.DeceiveInc.Client.Sdk",
    0x6A96564B,
    0x06283000
};

// Matching September 2026 dedicated server.
inline constexpr RuntimeProfile Server{
    TargetKind::Server,
    L"Server",
    L"Briefcase.DeceiveInc.Server.Sdk",
    0x6A966107,
    0x05B60000
};

inline constexpr const RuntimeProfile* find(
    std::uint32_t peTimestamp,
    std::uint32_t imageSize) noexcept {
    if (peTimestamp == PublicClient.PeTimestamp && imageSize == PublicClient.ImageSize)
        return &PublicClient;
    if (peTimestamp == PreviewClient.PeTimestamp && imageSize == PreviewClient.ImageSize)
        return &PreviewClient;
    if (peTimestamp == Server.PeTimestamp && imageSize == Server.ImageSize)
        return &Server;
    return nullptr;
}

// Client aliases retained for the existing packaging build guard.
inline constexpr std::uint32_t PeTimestamp = PreviewClient.PeTimestamp;
inline constexpr std::uint32_t ImageSize = PreviewClient.ImageSize;

// RE-UE4SS 3.0.1's generated non-case-preserving UE 4.27 layout places
// UObject::ProcessEvent at byte offset 0x220. The offset includes the virtual
// entries inherited before the UObject section (0x220 / sizeof(void*) = 68).
inline constexpr std::size_t ProcessEventVTableIndex = 68;

// AActor::BeginPlay is the stable lifecycle boundary used to expose
// ReceiveBeginPlay patches even when a cooked native class bypasses
// UObject::ProcessEvent. RE-UE4SS's UE 4.27 table places it at 0x338.
inline constexpr std::size_t ActorBeginPlayVTableIndex = 0x338 / sizeof(void*);

// UE 4.27 FProperty virtual slots used to construct and destroy owning
// parameter values such as FText. They are part of the same build-specific
// runtime profile as ProcessEvent and are never exposed through the mod ABI.
inline constexpr std::size_t DestroyValueInternalVTableIndex = 0xF0 / sizeof(void*);
inline constexpr std::size_t InitializeValueInternalVTableIndex = 0xF8 / sizeof(void*);
inline constexpr std::size_t IdenticalVTableIndex = 0x88 / sizeof(void*);
inline constexpr std::size_t GetValueTypeHashVTableIndex = 0xC0 / sizeof(void*);
inline constexpr std::size_t GetMinAlignmentVTableIndex = 0x110 / sizeof(void*);

// UE 4.27's FMalloc interface. Container storage must be owned by the same
// allocator as the engine because FProperty::DestroyValueInternal will release
// it later through FMemory.
inline constexpr std::size_t MallocVTableIndex = 0x10 / sizeof(void*);
inline constexpr std::size_t FreeVTableIndex = 0x30 / sizeof(void*);

} // namespace briefcase::profile
