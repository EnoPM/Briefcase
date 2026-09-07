#pragma once

#include <cstdint>
#include <iosfwd>
#include <string_view>

namespace briefcase::snapshot {

// Primitive writer shared by the native Unreal collector and its unit tests.
// Briefcase Snapshot strings use UTF-8 with a 7-bit encoded byte count.
class BinarySnapshotWriter {
public:
    explicit BinarySnapshotWriter(std::ostream& output) noexcept;

    void writeHeader();
    void writeBoolean(bool value);
    void writeByte(std::uint8_t value);
    void writeUInt16(std::uint16_t value);
    void writeInt32(std::int32_t value);
    void writeUInt32(std::uint32_t value);
    void writeInt64(std::int64_t value);
    void writeUInt64(std::uint64_t value);
    void writeString(std::string_view utf8Value);

private:
    void writeUnsigned(std::uint64_t value, unsigned byteCount);
    void write7BitEncodedLength(std::uint32_t value);

    std::ostream& output_;
};

} // namespace briefcase::snapshot
