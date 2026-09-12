#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <optional>
#include <span>
#include <string_view>
#include <utility>
#include <vector>

namespace briefcase::discovery {

struct PeSectionView {
    std::array<char, 9> Name{};
    std::uint32_t Rva{};
    std::uint32_t Size{};
    std::uint32_t Characteristics{};
    std::span<const std::byte> Bytes;

    [[nodiscard]] bool contains(std::uint32_t rva, std::size_t size = 1) const noexcept;
};

// A non-owning, bounds-checked view over an image already mapped at its virtual
// addresses. It does not accept raw on-disk section offsets.
class PeImageView {
public:
    static std::optional<PeImageView> create(std::span<const std::byte> image);

    [[nodiscard]] std::uint32_t timestamp() const noexcept { return timestamp_; }
    [[nodiscard]] std::uint32_t imageSize() const noexcept { return imageSize_; }
    [[nodiscard]] std::optional<PeSectionView> section(std::string_view name) const;
    [[nodiscard]] std::optional<PeSectionView> sectionContaining(
        std::uint32_t rva,
        std::size_t size = 1) const;

private:
    struct SectionRecord {
        std::array<char, 9> Name{};
        std::uint32_t Rva{};
        std::uint32_t Size{};
        std::uint32_t Characteristics{};
    };

    PeImageView(
        std::span<const std::byte> image,
        std::uint32_t timestamp,
        std::uint32_t imageSize,
        std::vector<SectionRecord> sections)
        : image_(image),
          timestamp_(timestamp),
          imageSize_(imageSize),
          sections_(std::move(sections)) {}

    [[nodiscard]] PeSectionView view(const SectionRecord& section) const;

    std::span<const std::byte> image_;
    std::uint32_t timestamp_{};
    std::uint32_t imageSize_{};
    std::vector<SectionRecord> sections_;
};

} // namespace briefcase::discovery
