#include "BinarySnapshotWriter.h"
#include "PeImageView.h"
#include "RuntimeProfile.h"
#include "RuntimeSymbolResolver.h"
#include "SignatureScanner.h"

#include <Windows.h>
#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <iostream>
#include <span>
#include <sstream>
#include <string>
#include <vector>

namespace {
using namespace briefcase::discovery;

int Failures{};
int Tests{};

void expect(bool condition, const char* message) {
    if (condition) return;
    ++Failures;
    std::cerr << "[FAIL] " << message << '\n';
}

template <typename Callback>
void test(const char* name, Callback callback) {
    ++Tests;
    const auto failuresBefore = Failures;
    callback();
    if (Failures == failuresBefore) std::cout << "[PASS] " << name << '\n';
}

template <typename T>
void write(std::span<std::byte> bytes, std::size_t offset, T value) {
    std::memcpy(bytes.data() + offset, &value, sizeof(value));
}

void writeSection(
    std::span<std::byte> image,
    std::size_t offset,
    const char* name,
    std::uint32_t rva,
    std::uint32_t size,
    std::uint32_t characteristics) {
    std::memcpy(image.data() + offset, name, std::min<std::size_t>(std::strlen(name), 8));
    write(image, offset + 8, size);
    write(image, offset + 12, rva);
    write(image, offset + 16, size);
    write(image, offset + 36, characteristics);
}

std::vector<std::byte> mappedPe() {
    constexpr std::size_t imageSize = 0x1000;
    constexpr std::size_t peOffset = 0x80;
    constexpr std::size_t coff = peOffset + 4;
    constexpr std::uint16_t optionalSize = 0xF0;
    constexpr std::size_t optional = coff + 20;
    constexpr std::size_t sectionTable = optional + optionalSize;
    std::vector<std::byte> image(imageSize);
    auto bytes = std::span(image);
    write<std::uint16_t>(bytes, 0, 0x5A4D);
    write<std::int32_t>(bytes, 0x3C, static_cast<std::int32_t>(peOffset));
    write<std::uint32_t>(bytes, peOffset, 0x00004550);
    write<std::uint16_t>(bytes, coff, 0x8664);
    write<std::uint16_t>(bytes, coff + 2, 2);
    write<std::uint32_t>(bytes, coff + 4, 0x12345678);
    write<std::uint16_t>(bytes, coff + 16, optionalSize);
    write<std::uint16_t>(bytes, optional, 0x020B);
    write<std::uint32_t>(bytes, optional + 56, static_cast<std::uint32_t>(imageSize));
    writeSection(bytes, sectionTable, ".text", 0x200, 0x200, 0x60000020);
    writeSection(bytes, sectionTable + 40, ".data", 0x600, 0x200, 0xC0000040);
    return image;
}

void binarySnapshotWriterTests() {
    test("binary snapshot writer uses a versioned little-endian header", [] {
        std::ostringstream stream(std::ios::binary);
        briefcase::snapshot::BinarySnapshotWriter writer(stream);
        writer.writeHeader();
        writer.writeUInt32(0x12345678);
        const std::string expected{"BRSK\x01\x00\x00\x00\x78\x56\x34\x12", 12};
        expect(stream.str() == expected, "binary header or integer encoding changed");
    });

    test("Briefcase Snapshot strings use 7-bit UTF-8 lengths", [] {
        std::ostringstream stream(std::ios::binary);
        briefcase::snapshot::BinarySnapshotWriter writer(stream);
        const std::string value(130, 'a');
        writer.writeString(value);
        const auto bytes = stream.str();
        expect(bytes.size() == 132, "length prefix changed the string payload size");
        expect(static_cast<unsigned char>(bytes[0]) == 0x82 &&
               static_cast<unsigned char>(bytes[1]) == 0x01,
               "string length is not 7-bit encoded");
        expect(bytes.substr(2) == value, "string payload was altered");
    });
}

void copyPattern(std::span<std::byte> destination, std::size_t offset, PatternView pattern) {
    for (std::size_t index = 0; index < pattern.Bytes.size(); ++index)
        destination[offset + index] = static_cast<std::byte>(pattern.Bytes[index].Value);
}

void patternTests() {
    test("masked scanner matches at the final valid offset", [] {
        constexpr auto pattern = makePattern("AA ?? CC");
        const std::array bytes{
            std::byte{0x00}, std::byte{0xAA}, std::byte{0x42}, std::byte{0xCC}};
        const auto matches = findPatternMatches(bytes, PatternView(pattern));
        expect(matches.Offsets == std::vector<std::size_t>{1}, "wildcard match was not found");
        expect(!matches.Truncated, "a single match was reported as truncated");
    });

    test("scanner rejects an empty or wildcard-only pattern", [] {
        constexpr std::array<PatternByte, 0> empty{};
        constexpr auto wildcards = makePattern("?? ??");
        const std::array bytes{std::byte{0x01}, std::byte{0x02}};
        expect(findPatternMatches(bytes, PatternView(empty)).Offsets.empty(),
               "empty pattern matched");
        expect(findPatternMatches(bytes, PatternView(wildcards)).Offsets.empty(),
               "wildcard-only pattern matched");
    });

    test("unique resolution distinguishes missing and ambiguous signatures", [] {
        constexpr auto pattern = makePattern("AA BB");
        const std::array missing{std::byte{0x00}, std::byte{0x01}};
        const std::array unique{std::byte{0x00}, std::byte{0xAA}, std::byte{0xBB}};
        const std::array ambiguous{
            std::byte{0xAA}, std::byte{0xBB}, std::byte{0xAA}, std::byte{0xBB}};
        expect(resolveUniqueMatch(missing, 0x200, PatternView(pattern), 0x1000).Status ==
                   ResolutionStatus::NotFound,
               "missing signature did not fail closed");
        const auto resolved = resolveUniqueMatch(unique, 0x200, PatternView(pattern), 0x1000);
        expect(resolved.Status == ResolutionStatus::Resolved && resolved.Rva == 0x201,
               "unique signature resolved to the wrong RVA");
        expect(resolveUniqueMatch(ambiguous, 0x200, PatternView(pattern), 0x1000).Status ==
                   ResolutionStatus::Ambiguous,
               "ambiguous signature was accepted");
    });
}

void relativeTests() {
    test("RIP-relative references must agree on one target", [] {
        constexpr auto pattern = makePattern("48 8B 05 ?? ?? ?? ??");
        std::array<std::byte, 64> section{};
        copyPattern(section, 4, PatternView(pattern));
        copyPattern(section, 24, PatternView(pattern));
        constexpr std::uint32_t sectionRva = 0x200;
        constexpr std::uint32_t encodedTarget = 0x610;
        write<std::int32_t>(section, 7,
            static_cast<std::int32_t>(encodedTarget - (sectionRva + 4 + 7)));
        write<std::int32_t>(section, 27,
            static_cast<std::int32_t>(encodedTarget - (sectionRva + 24 + 7)));

        const auto resolved = resolveRipRelativeConsensus(
            section, sectionRva, PatternView(pattern), 3, 7, -0x10, 0x1000);
        expect(resolved.Status == ResolutionStatus::Resolved,
               "matching RIP-relative references were rejected");
        expect(resolved.Rva == 0x600 && resolved.MatchCount == 2,
               "RIP-relative target or reference count was wrong");

        constexpr std::uint32_t otherTarget = 0x710;
        write<std::int32_t>(section, 27,
            static_cast<std::int32_t>(otherTarget - (sectionRva + 24 + 7)));
        expect(resolveRipRelativeConsensus(
                   section, sectionRva, PatternView(pattern), 3, 7, -0x10, 0x1000).Status ==
                   ResolutionStatus::Ambiguous,
               "references to different targets were accepted");
    });

    test("RIP-relative resolution rejects invalid layouts and targets", [] {
        constexpr auto pattern = makePattern("48 8B 05 ?? ?? ?? ??");
        std::array<std::byte, 16> section{};
        copyPattern(section, 0, PatternView(pattern));
        write<std::int32_t>(section, 3, 0x7FFFFFFF);
        expect(resolveRipRelativeConsensus(
                   section, 0x200, PatternView(pattern), 3, 7, 0, 0x1000).Status ==
                   ResolutionStatus::OutsideImage,
               "an out-of-image target was accepted");
        expect(resolveRipRelativeConsensus(
                   section, 0x200, PatternView(pattern), 6, 7, 0, 0x1000).Status ==
                   ResolutionStatus::InvalidEncoding,
               "an out-of-pattern displacement was accepted");
    });
}

void peTests() {
    test("PE view exposes bounded virtual sections", [] {
        const auto bytes = mappedPe();
        const auto image = PeImageView::create(bytes);
        expect(image.has_value(), "valid mapped PE was rejected");
        if (!image) return;
        expect(image->timestamp() == 0x12345678, "PE timestamp was decoded incorrectly");
        expect(image->imageSize() == 0x1000, "PE image size was decoded incorrectly");
        const auto text = image->section(".text");
        expect(text && text->Rva == 0x200 && text->Bytes.size() == 0x200,
               ".text section was decoded incorrectly");
        const auto data = image->sectionContaining(0x620, 0x20);
        expect(data && std::string_view(data->Name.data()) == ".data",
               "containing section was not found");
        expect(!image->sectionContaining(0x7F0, 0x20),
               "a range crossing the section boundary was accepted");
    });

    test("PE view rejects corrupt headers and out-of-image sections", [] {
        auto bytes = mappedPe();
        bytes[0] = std::byte{0};
        expect(!PeImageView::create(bytes), "invalid DOS signature was accepted");

        bytes = mappedPe();
        constexpr std::size_t secondSectionRva = 0x188 + 40 + 12;
        write<std::uint32_t>(bytes, secondSectionRva, 0xF80);
        expect(!PeImageView::create(bytes), "out-of-image section was accepted");
        expect(!PeImageView::create(std::span(bytes).first(128)),
               "truncated PE headers were accepted");
    });
}

int validateProfile(
    const wchar_t* path,
    std::uint32_t expectedObjects,
    std::uint32_t expectedName) {
    const auto file = CreateFileW(
        path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        std::wcerr << L"Could not open " << path << L"\n";
        return 2;
    }
    const auto mapping = CreateFileMappingW(
        file, nullptr, PAGE_READONLY | SEC_IMAGE_NO_EXECUTE, 0, 0, nullptr);
    if (!mapping) {
        CloseHandle(file);
        std::wcerr << L"Could not map " << path << L"\n";
        return 2;
    }
    const auto* base = static_cast<const std::byte*>(MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 0));
    if (!base) {
        CloseHandle(mapping);
        CloseHandle(file);
        std::wcerr << L"Could not view " << path << L"\n";
        return 2;
    }

    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    const auto image = PeImageView::create(
        std::span(base, static_cast<std::size_t>(nt->OptionalHeader.SizeOfImage)));
    auto result = 0;
    if (!image) {
        std::wcerr << L"Mapped image has invalid PE headers.\n";
        result = 3;
    } else if (!briefcase::profile::find(image->timestamp(), image->imageSize())) {
        std::wcerr << L"No Briefcase profile matches this executable.\n";
        result = 3;
    } else {
        const auto resolution = resolveRuntimeSymbols(*image);
        if (!resolution.succeeded()) {
            const auto name = runtimeSymbolFailureName(resolution.Failure);
            std::cerr << "Signature validation failed: " << name << '\n';
            result = 4;
        } else if (resolution.Symbols.GUObjectArrayRva != expectedObjects ||
                   resolution.Symbols.FNameToStringRva != expectedName) {
            std::cerr << "Resolved RVAs do not match the independently known values.\n";
            result = 5;
        } else {
            std::cout << "[OK] signatures: GUObjectArray=0x" << std::hex
                      << resolution.Symbols.GUObjectArrayRva
                      << " FName::ToString=0x" << resolution.Symbols.FNameToStringRva
                      << std::dec << " references="
                      << resolution.Symbols.GUObjectReferenceCount << '\n';
        }
    }

    UnmapViewOfFile(base);
    CloseHandle(mapping);
    CloseHandle(file);
    return result;
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    if (argc == 5 && std::wstring_view(argv[1]) == L"--validate-profile") {
        const auto objects = static_cast<std::uint32_t>(std::wcstoull(argv[3], nullptr, 0));
        const auto name = static_cast<std::uint32_t>(std::wcstoull(argv[4], nullptr, 0));
        return validateProfile(argv[2], objects, name);
    }

    patternTests();
    relativeTests();
    peTests();
    binarySnapshotWriterTests();
    if (Failures != 0) {
        std::cerr << "[FAIL] " << Failures << " assertion(s) failed across "
                  << Tests << " native tests.\n";
        return 1;
    }
    std::cout << "[OK] " << Tests << " native tests passed.\n";
    return 0;
}
