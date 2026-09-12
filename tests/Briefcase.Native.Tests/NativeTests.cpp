#include "BinarySnapshotWriter.h"
#include "PeImageView.h"
#include "RuntimeProfile.h"
#include "RuntimeSymbolResolver.h"
#include "SignatureScanner.h"
#include "UnrealReflection.h"
#include "UnrealInvocation.h"
#include "UnrealMarshalling.h"
#include "UnrealMetadataSnapshot.h"
#include "UnrealPatching.h"
#include "UnrealValueCodec.h"

#include <Windows.h>
#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
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
        const std::string expected{"BRSK\x02\x00\x00\x00\x78\x56\x34\x12", 12};
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

void __fastcall unusedNameConverter(const briefcase::unreal::FName*,
                                    briefcase::unreal::FStringBuffer*) {}

void __fastcall marshallingNameConverter(
    const briefcase::unreal::FName* name,
    briefcase::unreal::FStringBuffer* result) {
    if (!name || !result || !result->Data || result->Max <= 0) return;
    std::wstring_view text;
    switch (name->ComparisonIndex) {
    case 1: text = L"IntProperty"; break;
    case 2: text = L"ObjectProperty"; break;
    case 3: text = L"EnumProperty"; break;
    case 4: text = L"MapProperty"; break;
    default: text = L"UnsupportedProperty"; break;
    }
    if (text.size() + 1 > static_cast<std::size_t>(result->Max)) return;
    std::copy(text.begin(), text.end(), result->Data);
    result->Data[text.size()] = L'\0';
    result->Num = static_cast<std::int32_t>(text.size() + 1);
}

void unrealMarshallingTests() {
    test("property marshalling classifies Unreal field metadata", [] {
        using namespace briefcase::unreal;

        const auto previousConverter = RuntimeNameConverter;
        RuntimeNameConverter = marshallingNameConverter;

        FFieldClass fieldClass{};
        FProperty property{};
        property.ClassPrivate = &fieldClass;
        property.ArrayDim = 1;

        fieldClass.Name.ComparisonIndex = 1;
        property.ElementSize = 4;
        expect(propertyKind(&property) == BRIEFCASE_PROPERTY_INT32,
               "IntProperty did not map to the public Int32 kind");

        fieldClass.Name.ComparisonIndex = 2;
        expect(propertyKind(&property) == BRIEFCASE_PROPERTY_OBJECT,
               "ObjectProperty did not map to the public object kind");
        expect(isDirectPreparedObject(&property),
               "ObjectProperty was not accepted by prepared-call marshalling");

        fieldClass.Name.ComparisonIndex = 3;
        property.ElementSize = 2;
        expect(propertyKind(&property) == BRIEFCASE_PROPERTY_UINT16,
               "a two-byte EnumProperty did not preserve its storage width");

        fieldClass.Name.ComparisonIndex = 4;
        expect(propertyKind(&property) == BRIEFCASE_PROPERTY_MAP,
               "MapProperty did not map to the public map kind");

        fieldClass.Name.ComparisonIndex = 99;
        expect(propertyKind(&property) == BRIEFCASE_PROPERTY_UNKNOWN,
               "an unsupported property class was accepted");

        RuntimeNameConverter = previousConverter;
    });
}

void unrealValueCodecTests() {
    test("BVC1 round-trips a canonical reflected value", [] {
        using namespace briefcase::unreal;

        const auto previousConverter = RuntimeNameConverter;
        RuntimeNameConverter = marshallingNameConverter;

        FFieldClass fieldClass{};
        fieldClass.Name.ComparisonIndex = 1;
        FProperty property{};
        property.ClassPrivate = &fieldClass;
        property.ArrayDim = 1;
        property.ElementSize = sizeof(std::int32_t);
        property.OffsetInternal = 0;

        const std::int32_t source = 1337;
        ValueWireBuilder wire;
        expect(wire.append(ValueWireMagic), "BVC1 magic could not be appended");
        expect(appendValueNode(
                   &property, reinterpret_cast<const std::byte*>(&source), wire, 0),
               "canonical IntProperty could not be encoded");
        expect(wire.Bytes.size() == 16, "canonical IntProperty envelope size changed");

        std::uint32_t magic{};
        std::uint32_t kind{};
        std::uint32_t payloadSize{};
        std::memcpy(&magic, wire.Bytes.data(), sizeof(magic));
        std::memcpy(&kind, wire.Bytes.data() + 4, sizeof(kind));
        std::memcpy(&payloadSize, wire.Bytes.data() + 8, sizeof(payloadSize));
        expect(magic == ValueWireMagic, "BVC1 envelope has the wrong magic");
        expect(kind == BRIEFCASE_PROPERTY_INT32 && payloadSize == sizeof(source),
               "BVC1 node header does not describe an Int32 payload");

        std::int32_t decoded{};
        expect(decodeValueEnvelope(
                   &property, reinterpret_cast<std::byte*>(&decoded),
                   reinterpret_cast<const std::uint8_t*>(wire.Bytes.data()),
                   static_cast<std::uint32_t>(wire.Bytes.size())),
               "valid BVC1 envelope was rejected");
        expect(decoded == source, "BVC1 round-trip changed the Int32 value");

        auto corrupt = wire.Bytes;
        corrupt[0] = std::byte{};
        decoded = 0;
        expect(!decodeValueEnvelope(
                   &property, reinterpret_cast<std::byte*>(&decoded),
                   reinterpret_cast<const std::uint8_t*>(corrupt.data()),
                   static_cast<std::uint32_t>(corrupt.size())),
               "an envelope with a corrupt BVC1 magic was accepted");
        expect(!decodeValueEnvelope(
                   &property, reinterpret_cast<std::byte*>(&decoded),
                   reinterpret_cast<const std::uint8_t*>(wire.Bytes.data()),
                   static_cast<std::uint32_t>(wire.Bytes.size() - 1)),
               "a truncated BVC1 payload was accepted");

        RuntimeNameConverter = previousConverter;
    });

    test("BVC1 builder enforces its explicit size budget", [] {
        using namespace briefcase::unreal;
        ValueWireBuilder wire;
        const std::byte value{0x2a};
        expect(!wire.append(&value, MaximumValueWireBytes + 1),
               "a value larger than the BVC1 budget was accepted");
        expect(wire.Bytes.empty(), "a rejected BVC1 append mutated the output");
        expect(canonicalSize(BRIEFCASE_PROPERTY_DOUBLE) == 8,
               "the canonical Double size changed");
        expect(isPreparedAggregateKind(BRIEFCASE_PROPERTY_MAP),
               "Map was not classified as a prepared aggregate");
    });
}

void unrealInvocationTests() {
    test("prepared invocation enforces the captured Unreal thread", [] {
        using namespace briefcase::unreal;

        const auto previousThread = capturedGameThreadId();
        const auto currentThread = GetCurrentThreadId();
        setCapturedGameThreadId(currentThread);
        expect(onCapturedGameThread(),
               "the captured Unreal thread was not recognized");

        setCapturedGameThreadId(currentThread ^ 0x80000000u);
        expect(!onCapturedGameThread(),
               "a different thread identifier was accepted");
        setCapturedGameThreadId(previousThread);
    });

    test("prepared invocation rejects unknown tokens and releases output buffers", [] {
        using namespace briefcase::unreal;

        FUObjectArray objects{};
        const auto* previousObjects = RuntimeObjects;
        const auto previousConverter = RuntimeNameConverter;
        const auto previousThread = capturedGameThreadId();
        RuntimeObjects = &objects;
        RuntimeNameConverter = marshallingNameConverter;
        setCapturedGameThreadId(GetCurrentThreadId());

        expect(apiInvokePreparedFunction(
                   nullptr, 0, {}, nullptr, 0) == BRIEFCASE_UNREAL_INVALID_ARGUMENT,
               "an unknown prepared-function token was accepted");

        BriefcaseOwnedValueBuffer buffer{};
        buffer.Data = new std::uint8_t[4]{1, 2, 3, 4};
        buffer.Size = 4;
        apiReleaseValueBuffer(nullptr, &buffer);
        expect(buffer.Data == nullptr && buffer.Size == 0,
               "released BVC1 output buffer was not cleared");

        RuntimeObjects = previousObjects;
        RuntimeNameConverter = previousConverter;
        setCapturedGameThreadId(previousThread);
    });
}


void unrealPatchingTests() {
    test("patching APIs fail closed before runtime configuration", [] {
        using namespace briefcase::unreal;

        const auto previousThread = capturedGameThreadId();
        setCapturedGameThreadId(GetCurrentThreadId());
        configurePatchingRuntime(nullptr, nullptr, 0);

        BriefcaseBool result = 1;
        expect(apiInvokeNativeBoolean(nullptr, {}, {}, 1, &result) ==
                   BRIEFCASE_UNREAL_INVALID_ARGUMENT,
               "an RVA call was accepted without a validated runtime image");
        expect(apiUnregisterPatch(nullptr, UINT64_MAX) == 0,
               "an unknown reflected patch registration was removed");
        expect(apiUnregisterNativePatch(nullptr, UINT64_MAX) == 0,
               "an unknown native patch registration was removed");
        expect(apiIsGameThread(nullptr) == 1,
               "the patching scheduler did not use the captured Unreal thread");

        setCapturedGameThreadId(previousThread);
    });
}
void unrealReflectionTests() {
    test("object handles round-trip through the validated Unreal registry", [] {
        using namespace briefcase::unreal;

        std::vector<FUObjectItem> items(1000);
        FUObjectItem* chunks[]{items.data()};
        FUObjectArray registry{};
        registry.ObjObjects.Objects = chunks;
        registry.ObjObjects.MaxElements = static_cast<std::int32_t>(items.size());
        registry.ObjObjects.NumElements = static_cast<std::int32_t>(items.size());
        registry.ObjObjects.MaxChunks = 1;
        registry.ObjObjects.NumChunks = 1;

        UObject object{};
        object.InternalIndex = 17;
        items[17].Object = &object;
        items[17].SerialNumber = 42;

        const auto* previousObjects = RuntimeObjects;
        const auto previousConverter = RuntimeNameConverter;
        RuntimeObjects = &registry;
        RuntimeNameConverter = unusedNameConverter;

        BriefcaseObjectHandle handle{};
        expect(saneObjectArray(&registry), "synthetic object registry was rejected");
        expect(makeHandle(&object, handle), "registered object did not produce a handle");
        expect(handle.Index == 17 && handle.SerialNumber == 42,
               "object handle does not preserve index and serial number");
        expect(resolveObject(handle) == &object,
               "fresh object handle did not resolve to its object");
        expect(resolveObject({handle.Index, handle.SerialNumber + 1}) == nullptr,
               "stale object handle was accepted");

        RuntimeObjects = previousObjects;
        RuntimeNameConverter = previousConverter;
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

void sdkSnapshotCapturePolicyTests() {
    test("SDK snapshots are captured once per build by default", [] {
        const auto root = std::filesystem::temp_directory_path() /
            (L"BriefcaseSnapshotPolicy-" + std::to_wstring(GetCurrentProcessId()));
        std::error_code cleanupError;
        std::filesystem::remove_all(root, cleanupError);
        std::filesystem::create_directories(
            root / L"Briefcase" / L"Core" / L"Sdk" / L"Metadata");

        constexpr briefcase::profile::RuntimeProfile profile{
            briefcase::profile::TargetKind::Client,
            L"Client",
            L"Briefcase.DeceiveInc.Client.Sdk",
            0x1234ABCD,
            0x00102000};
        expect(briefcase::metadata::shouldCaptureSdkSnapshot(root, profile),
               "a missing build snapshot was treated as reusable");

        const auto snapshot = root / L"Briefcase" / L"Core" / L"Sdk" /
            L"Metadata" / L"DeceiveInc.Client.1234ABCD-00102000.bsnap";
        std::ofstream(snapshot, std::ios::binary).put('x');
        expect(!briefcase::metadata::shouldCaptureSdkSnapshot(root, profile),
               "an existing non-empty build snapshot was scheduled for recapture");

        const auto configuration = root / L"Briefcase" / L"loader.json";
        std::ofstream(configuration, std::ios::binary | std::ios::trunc)
            << R"({"sdkSnapshotFormat":"binary","sdkSnapshotRefresh":"always"})";
        expect(briefcase::metadata::shouldCaptureSdkSnapshot(root, profile),
               "the explicit always refresh policy was ignored");

        std::ofstream(configuration, std::ios::binary | std::ios::trunc)
            << R"({"sdkSnapshotFormat":"json","sdkSnapshotRefresh":"missing"})";
        expect(briefcase::metadata::shouldCaptureSdkSnapshot(root, profile),
               "a missing snapshot in the selected JSON format was treated as reusable");
        std::filesystem::remove_all(root, cleanupError);
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
    sdkSnapshotCapturePolicyTests();
    unrealReflectionTests();
    unrealMarshallingTests();
    unrealValueCodecTests();
    unrealInvocationTests();
    unrealPatchingTests();
    if (Failures != 0) {
        std::cerr << "[FAIL] " << Failures << " assertion(s) failed across "
                  << Tests << " native tests.\n";
        return 1;
    }
    std::cout << "[OK] " << Tests << " native tests passed.\n";
    return 0;
}
