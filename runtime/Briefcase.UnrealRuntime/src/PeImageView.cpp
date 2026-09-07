#include "PeImageView.h"

#include <algorithm>
#include <cstring>
#include <limits>

namespace briefcase::discovery {
namespace {

constexpr std::uint16_t DosSignature = 0x5A4D;
constexpr std::uint32_t PeSignature = 0x00004550;
constexpr std::uint16_t Amd64Machine = 0x8664;
constexpr std::uint16_t Pe32PlusMagic = 0x020B;
constexpr std::size_t PeOffsetLocation = 0x3C;
constexpr std::size_t CoffHeaderSize = 20;
constexpr std::size_t SectionHeaderSize = 40;
constexpr std::size_t MaximumSections = 96;

template <typename T>
std::optional<T> read(std::span<const std::byte> bytes, std::size_t offset) noexcept {
    if (offset > bytes.size() || sizeof(T) > bytes.size() - offset) return std::nullopt;
    T value{};
    std::memcpy(&value, bytes.data() + offset, sizeof(value));
    return value;
}

bool rangeFits(std::uint32_t offset, std::uint32_t size, std::uint32_t limit) noexcept {
    return offset < limit && size != 0 && size <= limit - offset;
}

} // namespace

bool PeSectionView::contains(std::uint32_t rva, std::size_t size) const noexcept {
    if (rva < Rva || size == 0 || size > std::numeric_limits<std::uint32_t>::max()) return false;
    const auto offset = rva - Rva;
    return offset < Size && size <= static_cast<std::size_t>(Size - offset);
}

std::optional<PeImageView> PeImageView::create(std::span<const std::byte> image) {
    const auto dosSignature = read<std::uint16_t>(image, 0);
    const auto peOffsetValue = read<std::int32_t>(image, PeOffsetLocation);
    if (!dosSignature || *dosSignature != DosSignature || !peOffsetValue || *peOffsetValue <= 0)
        return std::nullopt;
    const auto peOffset = static_cast<std::size_t>(*peOffsetValue);
    const auto signature = read<std::uint32_t>(image, peOffset);
    if (!signature || *signature != PeSignature) return std::nullopt;

    const auto coff = peOffset + sizeof(std::uint32_t);
    const auto machine = read<std::uint16_t>(image, coff);
    const auto sectionCount = read<std::uint16_t>(image, coff + 2);
    const auto timestamp = read<std::uint32_t>(image, coff + 4);
    const auto optionalSize = read<std::uint16_t>(image, coff + 16);
    if (!machine || *machine != Amd64Machine || !sectionCount || *sectionCount == 0 ||
        *sectionCount > MaximumSections || !timestamp || !optionalSize)
        return std::nullopt;

    const auto optional = coff + CoffHeaderSize;
    if (optional > image.size() || *optionalSize > image.size() - optional) return std::nullopt;
    const auto magic = read<std::uint16_t>(image, optional);
    const auto imageSize = read<std::uint32_t>(image, optional + 56);
    if (!magic || *magic != Pe32PlusMagic || !imageSize || *imageSize == 0 ||
        *imageSize > image.size())
        return std::nullopt;

    const auto table = optional + *optionalSize;
    if (table > image.size() ||
        static_cast<std::size_t>(*sectionCount) >
            (image.size() - table) / SectionHeaderSize)
        return std::nullopt;

    std::vector<SectionRecord> sections;
    sections.reserve(*sectionCount);
    for (std::size_t index = 0; index < *sectionCount; ++index) {
        const auto offset = table + index * SectionHeaderSize;
        SectionRecord section{};
        std::memcpy(section.Name.data(), image.data() + offset, 8);
        section.Name[8] = '\0';
        const auto virtualSize = read<std::uint32_t>(image, offset + 8);
        const auto rva = read<std::uint32_t>(image, offset + 12);
        const auto rawSize = read<std::uint32_t>(image, offset + 16);
        const auto characteristics = read<std::uint32_t>(image, offset + 36);
        if (!virtualSize || !rva || !rawSize || !characteristics) return std::nullopt;
        section.Rva = *rva;
        section.Size = *virtualSize == 0 ? *rawSize : *virtualSize;
        section.Characteristics = *characteristics;
        if (!rangeFits(section.Rva, section.Size, *imageSize)) return std::nullopt;
        sections.push_back(section);
    }

    auto ordered = sections;
    std::sort(ordered.begin(), ordered.end(), [](const auto& left, const auto& right) {
        return left.Rva < right.Rva;
    });
    for (std::size_t index = 1; index < ordered.size(); ++index) {
        const auto previousEnd = static_cast<std::uint64_t>(ordered[index - 1].Rva) +
                                 ordered[index - 1].Size;
        if (previousEnd > ordered[index].Rva) return std::nullopt;
    }

    return PeImageView(image.first(*imageSize), *timestamp, *imageSize, std::move(sections));
}

std::optional<PeSectionView> PeImageView::section(std::string_view name) const {
    const auto found = std::find_if(sections_.begin(), sections_.end(), [&](const auto& item) {
        return std::string_view(item.Name.data()) == name;
    });
    if (found == sections_.end()) return std::nullopt;
    return view(*found);
}

std::optional<PeSectionView> PeImageView::sectionContaining(
    std::uint32_t rva,
    std::size_t size) const {
    const auto found = std::find_if(sections_.begin(), sections_.end(), [&](const auto& item) {
        return view(item).contains(rva, size);
    });
    if (found == sections_.end()) return std::nullopt;
    return view(*found);
}

PeSectionView PeImageView::view(const SectionRecord& section) const {
    return {
        section.Name,
        section.Rva,
        section.Size,
        section.Characteristics,
        image_.subspan(section.Rva, section.Size)};
}

} // namespace briefcase::discovery
