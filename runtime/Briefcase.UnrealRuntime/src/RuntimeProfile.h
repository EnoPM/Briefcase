#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <string_view>

namespace briefcase::profile {

enum class TargetKind : std::uint32_t {
    Client = 1,
    Server = 2
};

struct RuntimeProfile {
    TargetKind Target;
    std::wstring_view TargetName;
    std::wstring_view SdkAssemblyName;
    std::uint32_t PeTimestamp;
    std::uint32_t ImageSize;
    std::uintptr_t GUObjectArrayRva;
    std::uintptr_t FNameToStringRva;
    std::array<std::byte, 16> FNameToStringPrefix;
};

// September 2026 dedicated-server-preview client.
inline constexpr RuntimeProfile Client{
    TargetKind::Client, L"Client", L"Briefcase.DeceiveInc.Client.Sdk",
    0x6A96564B, 0x06283000, 0x05B73930, 0x01920150,
    {
        std::byte{0x48}, std::byte{0x89}, std::byte{0x5C}, std::byte{0x24},
        std::byte{0x18}, std::byte{0x56}, std::byte{0x57}, std::byte{0x41},
        std::byte{0x56}, std::byte{0x48}, std::byte{0x83}, std::byte{0xEC},
        std::byte{0x30}, std::byte{0x83}, std::byte{0x79}, std::byte{0x04}
    }
};

// Matching September 2026 dedicated server. Its RVAs were derived from the
// server's own UE4SS discovery log and remain guarded by PE and byte checks.
inline constexpr RuntimeProfile Server{
    TargetKind::Server, L"Server", L"Briefcase.DeceiveInc.Server.Sdk",
    0x6A966107, 0x05B60000, 0x054BB300, 0x01714270,
    {
        std::byte{0x48}, std::byte{0x89}, std::byte{0x5C}, std::byte{0x24},
        std::byte{0x18}, std::byte{0x56}, std::byte{0x57}, std::byte{0x41},
        std::byte{0x56}, std::byte{0x48}, std::byte{0x83}, std::byte{0xEC},
        std::byte{0x30}, std::byte{0x83}, std::byte{0x79}, std::byte{0x04}
    }
};

inline constexpr const RuntimeProfile* find(
    std::uint32_t peTimestamp, std::uint32_t imageSize) noexcept {
    if (peTimestamp == Client.PeTimestamp && imageSize == Client.ImageSize)
        return &Client;
    if (peTimestamp == Server.PeTimestamp && imageSize == Server.ImageSize)
        return &Server;
    return nullptr;
}

// Client aliases retained for the existing packaging build guard.
inline constexpr std::uint32_t PeTimestamp = Client.PeTimestamp;
inline constexpr std::uint32_t ImageSize = Client.ImageSize;

// RE-UE4SS 3.0.1's generated non-case-preserving UE 4.27 layout places
// UObject::ProcessEvent at byte offset 0x220. The offset includes the virtual
// entries inherited before the UObject section (0x220 / sizeof(void*) = 68).
inline constexpr std::size_t ProcessEventVTableIndex = 68;

// UE 4.27 FProperty virtual slots used to construct and destroy owning
// parameter values such as FText. They are part of the same build-specific
// runtime profile as ProcessEvent and are never exposed through the mod ABI.
inline constexpr std::size_t DestroyValueInternalVTableIndex = 0xF0 / sizeof(void*);
inline constexpr std::size_t InitializeValueInternalVTableIndex = 0xF8 / sizeof(void*);

} // namespace briefcase::profile
