#include "BinarySnapshotWriter.h"

#include <limits>
#include <ostream>

namespace briefcase::snapshot {
namespace {
constexpr std::uint16_t FormatVersion = 1;
constexpr std::size_t MaximumStringBytes = 1024 * 1024;
}

BinarySnapshotWriter::BinarySnapshotWriter(std::ostream& output) noexcept
    : output_(output) {}

void BinarySnapshotWriter::writeHeader() {
    output_.write("BRSK", 4);
    writeUInt16(FormatVersion);
    writeUInt16(0);
}

void BinarySnapshotWriter::writeBoolean(bool value) {
    writeByte(value ? 1 : 0);
}

void BinarySnapshotWriter::writeByte(std::uint8_t value) {
    output_.put(static_cast<char>(value));
}

void BinarySnapshotWriter::writeUInt16(std::uint16_t value) {
    writeUnsigned(value, sizeof(value));
}

void BinarySnapshotWriter::writeInt32(std::int32_t value) {
    writeUInt32(static_cast<std::uint32_t>(value));
}

void BinarySnapshotWriter::writeUInt32(std::uint32_t value) {
    writeUnsigned(value, sizeof(value));
}

void BinarySnapshotWriter::writeInt64(std::int64_t value) {
    writeUInt64(static_cast<std::uint64_t>(value));
}

void BinarySnapshotWriter::writeUInt64(std::uint64_t value) {
    writeUnsigned(value, sizeof(value));
}

void BinarySnapshotWriter::writeString(std::string_view utf8Value) {
    if (utf8Value.size() > MaximumStringBytes ||
        utf8Value.size() > std::numeric_limits<std::uint32_t>::max()) {
        output_.setstate(std::ios::failbit);
        return;
    }
    write7BitEncodedLength(static_cast<std::uint32_t>(utf8Value.size()));
    output_.write(utf8Value.data(), static_cast<std::streamsize>(utf8Value.size()));
}

void BinarySnapshotWriter::writeUnsigned(std::uint64_t value, unsigned byteCount) {
    for (unsigned index = 0; index < byteCount; ++index) {
        writeByte(static_cast<std::uint8_t>(value & 0xFF));
        value >>= 8;
    }
}

void BinarySnapshotWriter::write7BitEncodedLength(std::uint32_t value) {
    while (value >= 0x80) {
        writeByte(static_cast<std::uint8_t>(value | 0x80));
        value >>= 7;
    }
    writeByte(static_cast<std::uint8_t>(value));
}

} // namespace briefcase::snapshot
