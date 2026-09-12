#include "UnrealValueCodec.h"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace briefcase::unreal {

// BVC1 is an internal, bounded wire format. It never exposes owning Unreal
// representations such as FString, FText, TArray, TSet, or TMap to managed code.
constexpr std::uint32_t RecursiveStructWireMarker =
    std::numeric_limits<std::uint32_t>::max();
constexpr std::uint64_t CpfHasGetValueTypeHash = 0x0008000000000000ull;

bool validSetLayout(const FScriptSetLayout& layout) {
    return layout.Size > 0 && layout.Size <= 65'535 &&
           layout.SparseArrayLayout.Size == layout.Size &&
           layout.SparseArrayLayout.Alignment > 0 &&
           layout.SparseArrayLayout.Alignment <= 4096 &&
           (layout.SparseArrayLayout.Alignment &
               (layout.SparseArrayLayout.Alignment - 1)) == 0 &&
           layout.HashNextIdOffset >= 0 &&
           layout.HashNextIdOffset <= layout.Size - static_cast<std::int32_t>(sizeof(std::int32_t)) &&
           layout.HashIndexOffset >= 0 &&
           layout.HashIndexOffset <= layout.Size - static_cast<std::int32_t>(sizeof(std::int32_t)) &&
           std::max(layout.HashNextIdOffset, layout.HashIndexOffset) -
               std::min(layout.HashNextIdOffset, layout.HashIndexOffset) >=
               static_cast<std::int32_t>(sizeof(std::int32_t));
}

bool validSetElementLayout(
    const FScriptSetLayout& layout, std::int32_t keySize,
    std::int32_t valueOffset = 0, std::int32_t valueSize = 0) {
    if (!validSetLayout(layout) || keySize <= 0 || valueOffset < 0 || valueSize < 0)
        return false;
    const auto payloadEnd = std::max<std::int64_t>(
        keySize, static_cast<std::int64_t>(valueOffset) + valueSize);
    const auto hashMetadataBegin =
        std::min(layout.HashNextIdOffset, layout.HashIndexOffset);
    return payloadEnd <= hashMetadataBegin;
}

bool ValueWireBuilder::append(const void* source, std::size_t size) {
    if (size > MaximumValueWireBytes || Bytes.size() > MaximumValueWireBytes - size)
        return false;
    const auto start = Bytes.size();
    Bytes.resize(start + size);
    return size == 0 || safeCopy(Bytes.data() + start, source, size);
}

std::size_t ValueWireBuilder::beginNode(BriefcasePropertyKind kind) {
    const auto start = Bytes.size();
    const auto numericKind = static_cast<std::uint32_t>(kind);
    const std::uint32_t payloadSize = 0;
    return append(numericKind) && append(payloadSize) ? start : SIZE_MAX;
}

bool ValueWireBuilder::endNode(std::size_t start) {
    if (start == SIZE_MAX || start > Bytes.size() || Bytes.size() - start < 8)
        return false;
    const auto payload = Bytes.size() - start - 8;
    if (payload > std::numeric_limits<std::uint32_t>::max()) return false;
    const auto size = static_cast<std::uint32_t>(payload);
    std::memcpy(Bytes.data() + start + 4, &size, sizeof(size));
    return true;
}

bool reflectedTypeName(const FProperty* property, std::wstring& result) {
    if (!property || !readable(property, sizeof(FProperty)) ||
        !readable(property->ClassPrivate, sizeof(FFieldClass))) return false;
    result = nameToString(
        reinterpret_cast<const FFieldClass*>(property->ClassPrivate)->Name,
        RuntimeNameConverter);
    return !result.empty();
}

// A canonical prepared value has a fixed-size, pointer-free managed image.
// UObject fields are represented by {object-array index, serial number}; the
// runtime converts those handles recursively immediately around ProcessEvent.
// Owning values such as FString, FText and containers remain on the dynamic ABI,
// where their constructors and destructors can be called explicitly.
bool isPreparedCanonicalProperty(const FProperty* property, unsigned depth) {
    if (!property || depth > MaximumValueDepth ||
        !readable(property, sizeof(FProperty)) || property->ArrayDim != 1 ||
        property->OffsetInternal < 0 || property->ElementSize <= 0)
        return false;

    std::wstring type;
    if (!reflectedTypeName(property, type)) return false;
    if (type == L"ObjectProperty" || type == L"ClassProperty" ||
        type == L"WeakObjectProperty")
        return property->ElementSize == sizeof(BriefcaseObjectHandle);

    const auto kind = propertyKind(property);
    switch (kind) {
    case BRIEFCASE_PROPERTY_INT8:
    case BRIEFCASE_PROPERTY_INT16:
    case BRIEFCASE_PROPERTY_UINT16:
    case BRIEFCASE_PROPERTY_INT32:
    case BRIEFCASE_PROPERTY_UINT32:
    case BRIEFCASE_PROPERTY_INT64:
    case BRIEFCASE_PROPERTY_UINT64:
    case BRIEFCASE_PROPERTY_FLOAT:
    case BRIEFCASE_PROPERTY_DOUBLE:
    case BRIEFCASE_PROPERTY_BYTE:
    case BRIEFCASE_PROPERTY_NAME:
        return true;
    case BRIEFCASE_PROPERTY_BOOL: {
        if (!readable(property, sizeof(FBoolProperty))) return false;
        const auto* boolean = reinterpret_cast<const FBoolProperty*>(property);
        return boolean->FieldSize == 1 && boolean->ByteMask != 0 &&
               boolean->ByteOffset < property->ElementSize;
    }
    case BRIEFCASE_PROPERTY_STRUCT: {
        if (!readable(property, sizeof(FStructProperty))) return false;
        const auto* structure = reinterpret_cast<const FStructProperty*>(property)->Struct;
        if (!structure || !readable(structure, sizeof(UStruct)) ||
            structure->PropertiesSize != property->ElementSize)
            return false;
        for (auto* current = structure; current; current = current->SuperStruct) {
            if (!readable(current, sizeof(UStruct))) return false;
            auto* field = current->ChildProperties;
            for (unsigned visited = 0; field && visited < 4096; ++visited) {
                if (!readable(field, sizeof(FProperty))) return false;
                const auto* nested = reinterpret_cast<const FProperty*>(field);
                if (nested->OffsetInternal < 0 || nested->ElementSize <= 0 ||
                    static_cast<std::uint64_t>(nested->OffsetInternal) +
                        static_cast<std::uint64_t>(nested->ElementSize) >
                        static_cast<std::uint64_t>(property->ElementSize) ||
                    !isPreparedCanonicalProperty(nested, depth + 1))
                    return false;
                field = field->Next;
            }
        }
        return true;
    }
    default:
        return false;
    }
}

bool isWireReadableProperty(const FProperty* property, unsigned depth) {
    if (!property || depth > MaximumValueDepth ||
        !readable(property, sizeof(FProperty)) || property->ArrayDim != 1 ||
        property->ElementSize <= 0) return false;
    const auto kind = propertyKind(property);
    if (kind == BRIEFCASE_PROPERTY_UNKNOWN) return false;
    if (kind == BRIEFCASE_PROPERTY_ARRAY) {
        if (!readable(property, sizeof(FArrayProperty))) return false;
        return isWireReadableProperty(
            reinterpret_cast<const FArrayProperty*>(property)->Inner, depth + 1);
    }
    if (kind == BRIEFCASE_PROPERTY_SET) {
        if (!readable(property, sizeof(FSetProperty))) return false;
        return isWireReadableProperty(
            reinterpret_cast<const FSetProperty*>(property)->ElementProperty, depth + 1);
    }
    if (kind == BRIEFCASE_PROPERTY_MAP) {
        if (!readable(property, sizeof(FMapProperty))) return false;
        const auto* map = reinterpret_cast<const FMapProperty*>(property);
        return isWireReadableProperty(map->KeyProperty, depth + 1) &&
               isWireReadableProperty(map->ValueProperty, depth + 1);
    }
    if (kind == BRIEFCASE_PROPERTY_STRUCT) {
        if (!readable(property, sizeof(FStructProperty))) return false;
        const auto* structure =
            reinterpret_cast<const FStructProperty*>(property)->Struct;
        if (!structure || !readable(structure, sizeof(UStruct)) ||
            structure->PropertiesSize != property->ElementSize) return false;
        // Unsupported reflected children are omitted from the managed SDK and
        // from the wire. Every supported child must itself be safely copyable.
        for (auto* current = structure; current; current = current->SuperStruct) {
            if (!readable(current, sizeof(UStruct))) return false;
            auto* field = current->ChildProperties;
            for (unsigned visited = 0; field && visited < 4096; ++visited) {
                if (!readable(field, sizeof(FProperty))) return false;
                const auto* nested = reinterpret_cast<const FProperty*>(field);
                if (propertyKind(nested) != BRIEFCASE_PROPERTY_UNKNOWN &&
                    !isWireReadableProperty(nested, depth + 1)) return false;
                field = field->Next;
            }
        }
    }
    return true;
}

// Every owning allocation is reconstructed through the current executable's
// GMalloc and every element is initialized through its reflected FProperty.
// Sets and maps additionally require Unreal's type hash contract; properties
// without CPF_HasGetValueTypeHash remain read-only.
bool isWireWritableProperty(const FProperty* property, unsigned depth) {
    if (!property || depth > MaximumValueDepth ||
        !readable(property, sizeof(FProperty)) || property->ArrayDim != 1 ||
        property->ElementSize <= 0)
        return false;
    if (isPreparedCanonicalProperty(property, depth)) return true;
    const auto kind = propertyKind(property);
    if (kind == BRIEFCASE_PROPERTY_STRING || kind == BRIEFCASE_PROPERTY_TEXT)
        return true;
    if (kind == BRIEFCASE_PROPERTY_ARRAY) {
        if (!RuntimeMalloc || !readable(property, sizeof(FArrayProperty))) return false;
        const auto* array = reinterpret_cast<const FArrayProperty*>(property);
        return isWireWritableProperty(array->Inner, depth + 1);
    }
    if (kind == BRIEFCASE_PROPERTY_SET) {
        if (!RuntimeMalloc || !readable(property, sizeof(FSetProperty))) return false;
        const auto* set = reinterpret_cast<const FSetProperty*>(property);
        return set->ElementProperty &&
               readable(set->ElementProperty, sizeof(FProperty)) &&
               (set->ElementProperty->PropertyFlags & CpfHasGetValueTypeHash) != 0 &&
               validSetElementLayout(
                   set->SetLayout, set->ElementProperty->ElementSize) &&
               isWireWritableProperty(set->ElementProperty, depth + 1);
    }
    if (kind == BRIEFCASE_PROPERTY_MAP) {
        if (!RuntimeMalloc || !readable(property, sizeof(FMapProperty))) return false;
        const auto* map = reinterpret_cast<const FMapProperty*>(property);
        return map->KeyProperty && map->ValueProperty &&
               readable(map->KeyProperty, sizeof(FProperty)) &&
               readable(map->ValueProperty, sizeof(FProperty)) &&
               (map->KeyProperty->PropertyFlags & CpfHasGetValueTypeHash) != 0 &&
               map->MapLayout.ValueOffset >= map->KeyProperty->ElementSize &&
               validSetElementLayout(
                   map->MapLayout.SetLayout, map->KeyProperty->ElementSize,
                   map->MapLayout.ValueOffset, map->ValueProperty->ElementSize) &&
               isWireWritableProperty(map->KeyProperty, depth + 1) &&
               isWireWritableProperty(map->ValueProperty, depth + 1);
    }
    if (kind != BRIEFCASE_PROPERTY_STRUCT ||
        !readable(property, sizeof(FStructProperty))) return false;
    const auto* structure = reinterpret_cast<const FStructProperty*>(property)->Struct;
    if (!structure || !readable(structure, sizeof(UStruct)) ||
        structure->PropertiesSize != property->ElementSize) return false;
    for (auto* current = structure; current; current = current->SuperStruct) {
        if (!readable(current, sizeof(UStruct))) return false;
        auto* field = current->ChildProperties;
        for (unsigned visited = 0; field && visited < 4096; ++visited) {
            if (!readable(field, sizeof(FProperty))) return false;
            const auto* nested = reinterpret_cast<const FProperty*>(field);
            if (nested->OffsetInternal < 0 || nested->ElementSize <= 0 ||
                static_cast<std::uint64_t>(nested->OffsetInternal) +
                    static_cast<std::uint64_t>(nested->ElementSize) >
                    static_cast<std::uint64_t>(property->ElementSize) ||
                !isWireWritableProperty(nested, depth + 1))
                return false;
            field = field->Next;
        }
    }
    return true;
}

bool appendWideString(ValueWireBuilder& output, const std::wstring& value) {
    if (value.size() > static_cast<std::size_t>(MaximumContainerElements) ||
        value.size() > std::numeric_limits<std::uint32_t>::max()) return false;
    const auto count = static_cast<std::uint32_t>(value.size());
    return output.append(count) &&
           output.append(value.data(), value.size() * sizeof(wchar_t));
}

bool appendStringBuffer(ValueWireBuilder& output, const FStringBuffer& value) {
    if (value.Num < 0 || value.Max < value.Num ||
        value.Num > MaximumContainerElements) return false;
    const auto count = static_cast<std::uint32_t>(value.Num > 0 ? value.Num - 1 : 0);
    return output.append(count) &&
           (count == 0 || (value.Data && output.append(
               value.Data, static_cast<std::size_t>(count) * sizeof(wchar_t))));
}

bool appendWeakHandle(ValueWireBuilder& output, const FWeakObjectPtr& weak) {
    BriefcaseObjectHandle handle{
        std::numeric_limits<std::uint32_t>::max(), 0};
    if (weak.ObjectIndex >= 0) {
        BriefcaseObjectHandle candidate{
            static_cast<std::uint32_t>(weak.ObjectIndex),
            static_cast<std::uint32_t>(weak.ObjectSerialNumber)};
        if (resolveObject(candidate)) handle = candidate;
    }
    return output.append(handle);
}

bool copyCanonicalFixedValue(
    const FProperty* property, const std::byte* source,
    std::byte* destination, std::size_t destinationSize, unsigned depth);

bool copyCanonicalStruct(
    const UStruct* structure, const std::byte* source,
    std::byte* destination, std::size_t destinationSize, unsigned depth) {
    if (!structure || depth > MaximumValueDepth ||
        !readable(structure, sizeof(UStruct))) return false;
    for (auto* current = structure; current; current = current->SuperStruct) {
        if (!readable(current, sizeof(UStruct))) return false;
        auto* field = current->ChildProperties;
        for (unsigned visited = 0; field && visited < 4096; ++visited) {
            if (!readable(field, sizeof(FProperty))) return false;
            const auto* property = reinterpret_cast<const FProperty*>(field);
            if (property->ArrayDim != 1 || property->OffsetInternal < 0 ||
                property->ElementSize <= 0 ||
                static_cast<std::size_t>(property->OffsetInternal) > destinationSize ||
                static_cast<std::size_t>(property->ElementSize) >
                    destinationSize - static_cast<std::size_t>(property->OffsetInternal)) {
                field = field->Next;
                continue;
            }
            if (!copyCanonicalFixedValue(
                    property, source + property->OffsetInternal,
                    destination + property->OffsetInternal,
                    static_cast<std::size_t>(property->ElementSize), depth + 1))
                return false;
            field = field->Next;
        }
    }
    return true;
}

bool copyCanonicalFixedValue(
    const FProperty* property, const std::byte* source,
    std::byte* destination, std::size_t destinationSize, unsigned depth) {
    if (!property || !source || !destination || depth > MaximumValueDepth) return false;
    std::wstring type;
    if (!reflectedTypeName(property, type)) return false;
    if (type == L"BoolProperty") {
        if (destinationSize < 1 || !readable(property, sizeof(FBoolProperty))) return false;
        const auto* boolean = reinterpret_cast<const FBoolProperty*>(property);
        std::uint8_t storage{};
        if (boolean->ByteOffset >= static_cast<std::uint8_t>(property->ElementSize) ||
            !safeCopy(&storage, source + boolean->ByteOffset, sizeof(storage))) return false;
        destination[0] = static_cast<std::byte>((storage & boolean->ByteMask) != 0 ? 1 : 0);
        return true;
    }
    if (type == L"ObjectProperty" || type == L"ClassProperty") {
        if (destinationSize != sizeof(BriefcaseObjectHandle)) return false;
        UObject* object{};
        BriefcaseObjectHandle handle{};
        return safeCopy(&object, source, sizeof(object)) && makeHandle(object, handle) &&
               safeCopy(destination, &handle, sizeof(handle));
    }
    if (type == L"WeakObjectProperty") {
        if (destinationSize != sizeof(BriefcaseObjectHandle)) return false;
        BriefcaseObjectHandle weak{};
        if (!safeCopy(&weak, source, sizeof(weak))) return false;
        if (weak.Index != std::numeric_limits<std::uint32_t>::max() && !resolveObject(weak))
            weak = {std::numeric_limits<std::uint32_t>::max(), 0};
        return safeCopy(destination, &weak, sizeof(weak));
    }
    if (type == L"StructProperty") {
        if (!readable(property, sizeof(FStructProperty))) return false;
        const auto* structure = reinterpret_cast<const FStructProperty*>(property)->Struct;
        return copyCanonicalStruct(
            structure, source, destination, destinationSize, depth + 1);
    }
    const auto kind = propertyKind(property);
    if (kind == BRIEFCASE_PROPERTY_UNKNOWN || kind == BRIEFCASE_PROPERTY_STRING ||
        kind == BRIEFCASE_PROPERTY_TEXT || kind == BRIEFCASE_PROPERTY_ARRAY ||
        kind == BRIEFCASE_PROPERTY_SET || kind == BRIEFCASE_PROPERTY_MAP ||
        kind == BRIEFCASE_PROPERTY_INTERFACE ||
        kind == BRIEFCASE_PROPERTY_LAZY_OBJECT ||
        kind == BRIEFCASE_PROPERTY_SOFT_OBJECT ||
        kind == BRIEFCASE_PROPERTY_SOFT_CLASS ||
        kind == BRIEFCASE_PROPERTY_DELEGATE ||
        kind == BRIEFCASE_PROPERTY_MULTICAST_DELEGATE ||
        kind == BRIEFCASE_PROPERTY_FIELD_PATH)
        return true; // Owning fields remain zero in the address-free struct image.
    return destinationSize == static_cast<std::size_t>(property->ElementSize) &&
           safeCopy(destination, source, destinationSize);
}

bool appendValueNode(
    const FProperty* property, const std::byte* source,
    ValueWireBuilder& output, unsigned depth);

bool validScriptArray(const FScriptArray& value, std::int32_t elementSize) {
    if (value.Num < 0 || value.Max < value.Num || value.Num > MaximumContainerElements ||
        elementSize <= 0) return false;
    if (value.Num == 0) return true;
    const auto bytes = static_cast<std::uint64_t>(value.Num) *
                       static_cast<std::uint64_t>(elementSize);
    return bytes <= MaximumValueWireBytes && value.Data &&
           readable(value.Data, static_cast<std::size_t>(bytes));
}

const std::uint32_t* bitArrayData(const FScriptBitArray& flags) {
    return flags.Allocator.SecondaryData
        ? flags.Allocator.SecondaryData
        : flags.Allocator.InlineData;
}

bool appendArrayNode(
    const FArrayProperty* property, const std::byte* source,
    ValueWireBuilder& output, unsigned depth) {
    if (!property || !readable(property, sizeof(FArrayProperty)) ||
        !readable(property->Inner, sizeof(FProperty))) return false;
    FScriptArray value{};
    if (!safeCopy(&value, source, sizeof(value)) ||
        !validScriptArray(value, property->Inner->ElementSize)) return false;
    const auto start = output.beginNode(BRIEFCASE_PROPERTY_ARRAY);
    const auto count = static_cast<std::uint32_t>(value.Num);
    if (start == SIZE_MAX || !output.append(count)) return false;
    const auto* data = reinterpret_cast<const std::byte*>(value.Data);
    for (std::int32_t index = 0; index < value.Num; ++index) {
        if (!appendValueNode(
                property->Inner,
                data + static_cast<std::size_t>(index) * property->Inner->ElementSize,
                output, depth + 1)) return false;
    }
    return output.endNode(start);
}

bool validSparseSet(
    const FScriptSet& set, const FScriptSetLayout& layout,
    std::int32_t& validCount, const std::uint32_t*& flags) {
    const auto& elements = set.Elements;
    if (layout.Size <= 0 || layout.Size > 65'535 ||
        layout.SparseArrayLayout.Size != layout.Size ||
        elements.Data.Num < 0 || elements.Data.Max < elements.Data.Num ||
        elements.Data.Num > MaximumContainerElements ||
        elements.NumFreeIndices < 0 || elements.NumFreeIndices > elements.Data.Num ||
        elements.AllocationFlags.NumBits < elements.Data.Num ||
        elements.AllocationFlags.MaxBits < elements.AllocationFlags.NumBits)
        return false;
    validCount = elements.Data.Num - elements.NumFreeIndices;
    if (!validScriptArray(elements.Data, layout.Size)) return false;
    flags = bitArrayData(elements.AllocationFlags);
    const auto words = (static_cast<std::size_t>(elements.Data.Num) + 31) / 32;
    return words == 0 || (flags && readable(flags, words * sizeof(std::uint32_t)));
}

bool appendSetNode(
    const FSetProperty* property, const std::byte* source,
    ValueWireBuilder& output, unsigned depth) {
    if (!property || !readable(property, sizeof(FSetProperty)) ||
        !readable(property->ElementProperty, sizeof(FProperty))) return false;
    FScriptSet set{};
    if (!safeCopy(&set, source, sizeof(set))) return false;
    std::int32_t count{};
    const std::uint32_t* flags{};
    if (!validSparseSet(set, property->SetLayout, count, flags)) return false;
    const auto start = output.beginNode(BRIEFCASE_PROPERTY_SET);
    const auto encodedCount = static_cast<std::uint32_t>(count);
    if (start == SIZE_MAX || !output.append(encodedCount)) return false;
    const auto* data = reinterpret_cast<const std::byte*>(set.Elements.Data.Data);
    for (std::int32_t index = 0; index < set.Elements.Data.Num; ++index) {
        if ((flags[index / 32] & (1u << (index & 31))) == 0) continue;
        if (!appendValueNode(
                property->ElementProperty,
                data + static_cast<std::size_t>(index) * property->SetLayout.Size,
                output, depth + 1)) return false;
    }
    return output.endNode(start);
}

bool appendMapNode(
    const FMapProperty* property, const std::byte* source,
    ValueWireBuilder& output, unsigned depth) {
    if (!property || !readable(property, sizeof(FMapProperty)) ||
        !readable(property->KeyProperty, sizeof(FProperty)) ||
        !readable(property->ValueProperty, sizeof(FProperty)) ||
        property->MapLayout.ValueOffset < 0 ||
        property->MapLayout.ValueOffset > property->MapLayout.SetLayout.Size -
            property->ValueProperty->ElementSize)
        return false;
    FScriptSet set{};
    if (!safeCopy(&set, source, sizeof(set))) return false;
    std::int32_t count{};
    const std::uint32_t* flags{};
    if (!validSparseSet(set, property->MapLayout.SetLayout, count, flags)) return false;
    const auto start = output.beginNode(BRIEFCASE_PROPERTY_MAP);
    const auto encodedCount = static_cast<std::uint32_t>(count);
    if (start == SIZE_MAX || !output.append(encodedCount)) return false;
    const auto* data = reinterpret_cast<const std::byte*>(set.Elements.Data.Data);
    for (std::int32_t index = 0; index < set.Elements.Data.Num; ++index) {
        if ((flags[index / 32] & (1u << (index & 31))) == 0) continue;
        const auto* pair = data + static_cast<std::size_t>(index) *
                                   property->MapLayout.SetLayout.Size;
        if (!appendValueNode(property->KeyProperty, pair, output, depth + 1) ||
            !appendValueNode(
                property->ValueProperty, pair + property->MapLayout.ValueOffset,
                output, depth + 1)) return false;
    }
    return output.endNode(start);
}

bool appendValueNode(
    const FProperty* property, const std::byte* source,
    ValueWireBuilder& output, unsigned depth) {
    if (!property || !source || depth > MaximumValueDepth ||
        !readable(property, sizeof(FProperty))) return false;
    const auto kind = propertyKind(property);
    if (kind == BRIEFCASE_PROPERTY_ARRAY)
        return appendArrayNode(reinterpret_cast<const FArrayProperty*>(property), source, output, depth);
    if (kind == BRIEFCASE_PROPERTY_SET)
        return appendSetNode(reinterpret_cast<const FSetProperty*>(property), source, output, depth);
    if (kind == BRIEFCASE_PROPERTY_MAP)
        return appendMapNode(reinterpret_cast<const FMapProperty*>(property), source, output, depth);

    const auto start = output.beginNode(kind);
    if (start == SIZE_MAX || kind == BRIEFCASE_PROPERTY_UNKNOWN) return false;
    if (kind == BRIEFCASE_PROPERTY_STRING) {
        FStringBuffer value{};
        if (!safeCopy(&value, source, sizeof(value)) || value.Num < 0 ||
            value.Max < value.Num || value.Num > MaximumContainerElements) return false;
        auto count = value.Num > 0 ? value.Num - 1 : 0;
        if (!output.append(static_cast<std::uint32_t>(count))) return false;
        if (count && (!value.Data ||
            !output.append(value.Data, static_cast<std::size_t>(count) * sizeof(std::uint16_t))))
            return false;
    } else if (kind == BRIEFCASE_PROPERTY_TEXT) {
        TextConversionRuntime runtime{};
        if (!resolveTextConversionRuntime(runtime)) return false;
        std::uint32_t required{};
        auto status = textToUtf16(runtime, source, nullptr, 0, &required);
        if (status == BRIEFCASE_UNREAL_OK && required == 0) {
            if (!output.append(required)) return false;
        } else {
            if (status != BRIEFCASE_UNREAL_BUFFER_TOO_SMALL || required > 65'536) return false;
            std::vector<std::uint16_t> text(required);
            status = textToUtf16(runtime, source, text.data(), required, &required);
            if (status != BRIEFCASE_UNREAL_OK) return false;
            auto count = required > 0 && text[required - 1] == 0 ? required - 1 : required;
            if (!output.append(count) ||
                !output.append(text.data(), static_cast<std::size_t>(count) * sizeof(std::uint16_t)))
                return false;
        }
    } else if (kind == BRIEFCASE_PROPERTY_BOOL) {
        std::byte value{};
        if (!copyCanonicalFixedValue(property, source, &value, 1, depth) ||
            !output.append(value)) return false;
    } else if (kind == BRIEFCASE_PROPERTY_OBJECT) {
        BriefcaseObjectHandle handle{};
        if (!copyCanonicalFixedValue(property, source,
                reinterpret_cast<std::byte*>(&handle), sizeof(handle), depth) ||
             !output.append(handle)) return false;
    } else if (kind == BRIEFCASE_PROPERTY_INTERFACE) {
        FScriptInterface value{};
        BriefcaseObjectHandle handle{};
        if (property->ElementSize != sizeof(value) ||
            !safeCopy(&value, source, sizeof(value)) ||
            !makeHandle(value.ObjectPointer, handle) || !output.append(handle)) return false;
    } else if (kind == BRIEFCASE_PROPERTY_LAZY_OBJECT) {
        FLazyObjectPtrView value{};
        if (property->ElementSize != sizeof(value) ||
            !safeCopy(&value, source, sizeof(value)) ||
            !appendWeakHandle(output, value.WeakObject) ||
            !output.append(value.Guid, sizeof(value.Guid))) return false;
    } else if (kind == BRIEFCASE_PROPERTY_SOFT_OBJECT ||
               kind == BRIEFCASE_PROPERTY_SOFT_CLASS) {
        FSoftObjectPtrView value{};
        if (property->ElementSize != sizeof(value) ||
            !safeCopy(&value, source, sizeof(value)) ||
            !appendWeakHandle(output, value.WeakObject) ||
            !appendWideString(output, nameToString(value.AssetPathName, RuntimeNameConverter)) ||
            !appendStringBuffer(output, value.SubPathString)) return false;
    } else if (kind == BRIEFCASE_PROPERTY_DELEGATE) {
        FScriptDelegate value{};
        if (property->ElementSize != sizeof(value) ||
            !safeCopy(&value, source, sizeof(value)) ||
            !appendWeakHandle(output, value.Object) ||
            !output.append(value.FunctionName)) return false;
    } else if (kind == BRIEFCASE_PROPERTY_MULTICAST_DELEGATE) {
        FScriptArray value{};
        if (property->ElementSize != sizeof(value) ||
            !safeCopy(&value, source, sizeof(value)) ||
            !validScriptArray(value, sizeof(FScriptDelegate))) return false;
        const auto count = static_cast<std::uint32_t>(value.Num);
        if (!output.append(count)) return false;
        const auto* bindings = reinterpret_cast<const FScriptDelegate*>(value.Data);
        for (std::int32_t index = 0; index < value.Num; ++index) {
            FScriptDelegate binding{};
            if (!safeCopy(&binding, bindings + index, sizeof(binding)) ||
                !appendWeakHandle(output, binding.Object) ||
                !output.append(binding.FunctionName)) return false;
        }
    } else if (kind == BRIEFCASE_PROPERTY_FIELD_PATH) {
        FFieldPathView value{};
        if (property->ElementSize != sizeof(value) ||
            !safeCopy(&value, source, sizeof(value)) ||
            !validScriptArray(value.Path, sizeof(FName))) return false;
        const auto count = static_cast<std::uint32_t>(value.Path.Num);
        if (!output.append(count) ||
            (count != 0 && !output.append(
                value.Path.Data, static_cast<std::size_t>(count) * sizeof(FName))))
            return false;
    } else if (kind == BRIEFCASE_PROPERTY_STRUCT) {
        const auto size = static_cast<std::uint32_t>(property->ElementSize);
        if (isPreparedCanonicalProperty(property, depth)) {
            std::vector<std::byte> canonical(size);
            if (!copyCanonicalFixedValue(
                    property, source, canonical.data(), canonical.size(), depth) ||
                !output.append(size) ||
                !output.append(canonical.data(), canonical.size())) return false;
        } else {
            if (!readable(property, sizeof(FStructProperty))) return false;
            const auto* structure =
                reinterpret_cast<const FStructProperty*>(property)->Struct;
            if (!structure || !readable(structure, sizeof(UStruct)) ||
                structure->PropertiesSize != property->ElementSize) return false;

            struct EncodedField {
                std::int32_t Offset{};
                std::wstring Name;
                std::vector<std::byte> Node;
            };
            std::vector<EncodedField> fields;
            for (auto* current = structure; current; current = current->SuperStruct) {
                if (!readable(current, sizeof(UStruct))) return false;
                auto* field = current->ChildProperties;
                for (unsigned visited = 0; field && visited < 4096; ++visited) {
                    if (!readable(field, sizeof(FProperty))) return false;
                    const auto* nested = reinterpret_cast<const FProperty*>(field);
                    if (nested->ArrayDim == 1 && nested->OffsetInternal >= 0 &&
                        nested->ElementSize > 0 &&
                        static_cast<std::uint64_t>(nested->OffsetInternal) +
                            static_cast<std::uint64_t>(nested->ElementSize) <= size &&
                        isWireReadableProperty(nested, depth + 1)) {
                        ValueWireBuilder child;
                        if (!appendValueNode(
                                nested, source + nested->OffsetInternal,
                                child, depth + 1)) return false;
                        fields.push_back({
                            nested->OffsetInternal,
                            nameToString(nested->NamePrivate, RuntimeNameConverter),
                            std::move(child.Bytes)});
                    }
                    field = field->Next;
                }
            }
            if (fields.size() > static_cast<std::size_t>(MaximumContainerElements) ||
                !output.append(RecursiveStructWireMarker) || !output.append(size) ||
                !output.append(static_cast<std::uint32_t>(fields.size()))) return false;
            for (const auto& field : fields) {
                if (!output.append(field.Offset) ||
                    !appendWideString(output, field.Name) ||
                    !output.append(field.Node.data(), field.Node.size())) return false;
            }
        }
    } else {
        if (property->ElementSize <= 0 ||
            !output.append(source, static_cast<std::size_t>(property->ElementSize))) return false;
    }
    return output.endNode(start);
}

std::int32_t canonicalSize(BriefcasePropertyKind kind) {
    switch (kind) {
    case BRIEFCASE_PROPERTY_INT8:
    case BRIEFCASE_PROPERTY_BYTE:
    case BRIEFCASE_PROPERTY_BOOL: return 1;
    case BRIEFCASE_PROPERTY_INT16:
    case BRIEFCASE_PROPERTY_UINT16: return 2;
    case BRIEFCASE_PROPERTY_INT32:
    case BRIEFCASE_PROPERTY_UINT32:
    case BRIEFCASE_PROPERTY_FLOAT: return 4;
    case BRIEFCASE_PROPERTY_INT64:
    case BRIEFCASE_PROPERTY_UINT64:
    case BRIEFCASE_PROPERTY_DOUBLE:
    case BRIEFCASE_PROPERTY_OBJECT:
    case BRIEFCASE_PROPERTY_NAME: return 8;
    default: return 0;
    }
}

bool isPreparedAggregateKind(BriefcasePropertyKind kind) {
    return kind == BRIEFCASE_PROPERTY_STRUCT ||
           kind == BRIEFCASE_PROPERTY_ARRAY ||
           kind == BRIEFCASE_PROPERTY_SET ||
           kind == BRIEFCASE_PROPERTY_MAP;
}

bool writeCanonicalFixedValue(
    const FProperty* property, std::byte* destination,
    const std::byte* source, std::size_t sourceSize, unsigned depth);

bool writeCanonicalStruct(
    const UStruct* structure, std::byte* destination,
    const std::byte* source, std::size_t sourceSize, unsigned depth) {
    if (!structure || depth > MaximumValueDepth ||
        !readable(structure, sizeof(UStruct))) return false;
    for (auto* current = structure; current; current = current->SuperStruct) {
        if (!readable(current, sizeof(UStruct))) return false;
        auto* field = current->ChildProperties;
        for (unsigned visited = 0; field && visited < 4096; ++visited) {
            if (!readable(field, sizeof(FProperty))) return false;
            const auto* property = reinterpret_cast<const FProperty*>(field);
            if (property->ArrayDim == 1 && property->OffsetInternal >= 0 &&
                property->ElementSize > 0 &&
                static_cast<std::size_t>(property->OffsetInternal) <= sourceSize &&
                static_cast<std::size_t>(property->ElementSize) <=
                    sourceSize - static_cast<std::size_t>(property->OffsetInternal) &&
                !writeCanonicalFixedValue(
                    property, destination + property->OffsetInternal,
                    source + property->OffsetInternal,
                    static_cast<std::size_t>(property->ElementSize), depth + 1))
                return false;
            field = field->Next;
        }
    }

    return true;
}

bool writeCanonicalFixedValue(
    const FProperty* property, std::byte* destination,
    const std::byte* source, std::size_t sourceSize, unsigned depth) {
    if (!property || !destination || !source || depth > MaximumValueDepth) return false;
    std::wstring type;
    if (!reflectedTypeName(property, type)) return false;
    if (type == L"BoolProperty") {
        if (sourceSize < 1 || !readable(property, sizeof(FBoolProperty))) return false;
        const auto* boolean = reinterpret_cast<const FBoolProperty*>(property);
        std::uint8_t storage{};
        if (boolean->ByteOffset >= static_cast<std::uint8_t>(property->ElementSize) ||
            !safeCopy(&storage, destination + boolean->ByteOffset, 1)) return false;
        const bool enabled = source[0] != std::byte{};
        storage = enabled ? static_cast<std::uint8_t>(storage | boolean->ByteMask)
                          : static_cast<std::uint8_t>(storage & ~boolean->ByteMask);
        return safeCopy(destination + boolean->ByteOffset, &storage, 1);
    }
    if (type == L"ObjectProperty" || type == L"ClassProperty") {
        if (sourceSize != sizeof(BriefcaseObjectHandle)) return false;
        BriefcaseObjectHandle handle{};
        if (!safeCopy(&handle, source, sizeof(handle))) return false;
        UObject* object{};
        if (handle.Index != std::numeric_limits<std::uint32_t>::max()) {
            object = const_cast<UObject*>(resolveObject(handle));
            if (!object) return false;
        }
        return safeCopy(destination, &object, sizeof(object));
    }
    if (type == L"WeakObjectProperty") {
        if (sourceSize != sizeof(BriefcaseObjectHandle)) return false;
        BriefcaseObjectHandle handle{};
        if (!safeCopy(&handle, source, sizeof(handle)) ||
            (handle.Index != std::numeric_limits<std::uint32_t>::max() &&
             !resolveObject(handle))) return false;
        return safeCopy(destination, &handle, sizeof(handle));
    }
    if (type == L"StructProperty") {
        if (!readable(property, sizeof(FStructProperty))) return false;
        return writeCanonicalStruct(
            reinterpret_cast<const FStructProperty*>(property)->Struct,
            destination, source, sourceSize, depth + 1);
    }
    const auto kind = propertyKind(property);
    if (kind == BRIEFCASE_PROPERTY_UNKNOWN || kind == BRIEFCASE_PROPERTY_STRING ||
        kind == BRIEFCASE_PROPERTY_TEXT || kind == BRIEFCASE_PROPERTY_ARRAY ||
        kind == BRIEFCASE_PROPERTY_SET || kind == BRIEFCASE_PROPERTY_MAP ||
        kind == BRIEFCASE_PROPERTY_INTERFACE ||
        kind == BRIEFCASE_PROPERTY_LAZY_OBJECT ||
        kind == BRIEFCASE_PROPERTY_SOFT_OBJECT ||
        kind == BRIEFCASE_PROPERTY_SOFT_CLASS ||
        kind == BRIEFCASE_PROPERTY_DELEGATE ||
        kind == BRIEFCASE_PROPERTY_MULTICAST_DELEGATE ||
        kind == BRIEFCASE_PROPERTY_FIELD_PATH)
        return true; // Owning nested fields are mutated through UFunctions.
    return sourceSize == static_cast<std::size_t>(property->ElementSize) &&
           safeCopy(destination, source, sourceSize);
}

class ValueWireReader {
public:
    ValueWireReader(const std::uint8_t* data, std::size_t size)
        : cursor_(data), remaining_(size) {}

    template <typename TValue>
    bool read(TValue& value) {
        return readBytes(&value, sizeof(value));
    }

    bool readBytes(void* destination, std::size_t size) {
        if (size > remaining_ || (size != 0 && !destination)) return false;
        if (size != 0 && !safeCopy(destination, cursor_, size)) return false;
        cursor_ += size;
        remaining_ -= size;
        return true;
    }

    bool take(std::size_t size, ValueWireReader& result) {
        if (size > remaining_) return false;
        result = ValueWireReader(cursor_, size);
        cursor_ += size;
        remaining_ -= size;
        return true;
    }

    bool skip(std::size_t size) {
        if (size > remaining_) return false;
        cursor_ += size;
        remaining_ -= size;
        return true;
    }

    bool readWideString(std::wstring& value) {
        std::uint32_t count{};
        if (!read(count) || count > static_cast<std::uint32_t>(MaximumContainerElements))
            return false;
        const auto bytes = static_cast<std::size_t>(count) * sizeof(std::uint16_t);
        value.resize(count);
        return bytes == 0 || readBytes(value.data(), bytes);
    }

    bool empty() const { return remaining_ == 0; }

private:
    const std::uint8_t* cursor_{};
    std::size_t remaining_{};
};

bool skipValueNode(ValueWireReader& input) {
    std::uint32_t kind{};
    std::uint32_t size{};
    return input.read(kind) && input.read(size) && input.skip(size);
}

const FProperty* findStructField(
    const UStruct* structure, std::int32_t offset, std::wstring_view name,
    std::int32_t nativeSize) {
    for (auto* current = structure; current; current = current->SuperStruct) {
        if (!readable(current, sizeof(UStruct))) return nullptr;
        auto* field = current->ChildProperties;
        for (unsigned visited = 0; field && visited < 4096; ++visited) {
            if (!readable(field, sizeof(FProperty))) return nullptr;
            const auto* property = reinterpret_cast<const FProperty*>(field);
            if (property->ArrayDim == 1 && property->OffsetInternal == offset &&
                property->ElementSize > 0 &&
                static_cast<std::uint64_t>(offset) +
                    static_cast<std::uint64_t>(property->ElementSize) <=
                    static_cast<std::uint64_t>(nativeSize) &&
                nameToString(property->NamePrivate, RuntimeNameConverter) == name)
                return property;
            field = field->Next;
        }
    }
    return nullptr;
}

bool replaceInitializedTextValue(
    const FProperty* property, std::byte* destination,
    BriefcasePropertyKind kind, const std::wstring& text) {
    if (text.size() >= 65'536 || text.find(L'\0') != std::wstring::npos) return false;
    std::vector<std::uint16_t> terminated(text.size() + 1);
    if (!text.empty())
        std::memcpy(terminated.data(), text.data(), text.size() * sizeof(std::uint16_t));
    TextConversionRuntime runtime{};
    if (!resolveTextConversionRuntime(runtime)) return false;

    if (kind == BRIEFCASE_PROPERTY_TEXT) {
        if (property->ElementSize != 24) return false;
        OwnedTextValue temporary;
        if (!constructText(
                runtime, terminated.data(), static_cast<std::uint32_t>(text.size()), temporary))
            return false;
        std::array<std::byte, 24> bytes{};
        if (!safeCopy(bytes.data(), temporary.Parameters.data() + 16, bytes.size()) ||
            !destroyPropertyValue(property, destination) ||
            !safeCopy(destination, bytes.data(), bytes.size())) {
            destroyText(temporary);
            return false;
        }
        temporary.Initialized = false;
        return true;
    }

    if (kind != BRIEFCASE_PROPERTY_STRING ||
        property->ElementSize != sizeof(FStringBuffer)) return false;
    OwnedStringValue temporary;
    if (!constructString(
            runtime, terminated.data(), static_cast<std::uint32_t>(text.size()), temporary))
        return false;
    std::array<std::byte, sizeof(FStringBuffer)> bytes{};
    if (!safeCopy(bytes.data(), temporary.Parameters.data() + 24, bytes.size()) ||
        !destroyPropertyValue(property, destination) ||
        !safeCopy(destination, bytes.data(), bytes.size())) {
        if (temporary.Initialized)
            destroyPropertyValue(temporary.Property, temporary.Parameters.data() + 24);
        return false;
    }
    temporary.Initialized = false;
    return true;
}

bool decodeValueNode(
    const FProperty* property, std::byte* destination,
    ValueWireReader& input, unsigned depth);

void destroyArrayElements(
    const FProperty* elementProperty, std::byte* data,
    std::uint32_t initializedCount) noexcept {
    if (!elementProperty || !data) return;
    for (std::uint32_t index = initializedCount; index > 0; --index)
        destroyPropertyValue(
            elementProperty,
            data + static_cast<std::size_t>(index - 1) * elementProperty->ElementSize);
}

bool decodeArrayValue(
    const FArrayProperty* property, std::byte* destination,
    ValueWireReader& payload, unsigned depth) {
    if (!property || !readable(property, sizeof(FArrayProperty)) ||
        !isWireWritableProperty(property->Inner, depth + 1)) return false;
    std::uint32_t count{};
    if (!payload.read(count) ||
        count > static_cast<std::uint32_t>(MaximumContainerElements)) return false;
    if (count == 0) return payload.empty();

    FScriptArray current{};
    if (!safeCopy(&current, destination, sizeof(current)) || current.Data ||
        current.Num != 0 || current.Max != 0) return false;
    const auto elementSize = static_cast<std::size_t>(property->Inner->ElementSize);
    if (elementSize == 0 || count > MaximumValueWireBytes / elementSize) return false;
    const auto allocationSize = static_cast<std::size_t>(count) * elementSize;
    std::uint32_t alignment{};
    if (!safePropertyAlignment(property->Inner, alignment)) return false;
    void* allocation{};
    if (!safeUnrealMalloc(allocationSize, alignment, allocation)) return false;
    auto* data = static_cast<std::byte*>(allocation);

    std::uint32_t initialized{};
    for (; initialized < count; ++initialized) {
        auto* element = data + static_cast<std::size_t>(initialized) * elementSize;
        if (!initializePropertyValue(property->Inner, element)) break;
        if (!decodeValueNode(property->Inner, element, payload, depth + 1)) {
            ++initialized;
            break;
        }
    }
    if (initialized != count || !payload.empty()) {
        destroyArrayElements(property->Inner, data, initialized);
        safeUnrealFree(allocation);
        return false;
    }

    const FScriptArray value{
        allocation, static_cast<std::int32_t>(count), static_cast<std::int32_t>(count)};
    if (!safeCopy(destination, &value, sizeof(value))) {
        destroyArrayElements(property->Inner, data, initialized);
        safeUnrealFree(allocation);
        return false;
    }
    return true;
}

std::uint32_t setHashSize(std::uint32_t count) {
    if (count < 4) return 1;
    auto desired = count / 2 + 8;
    std::uint32_t result = 1;
    while (result < desired) result <<= 1;
    return result;
}

void destroySetElements(
    const FProperty* keyProperty, const FProperty* valueProperty,
    std::int32_t valueOffset, std::byte* data, std::int32_t stride,
    std::uint32_t initializedKeys, std::uint32_t initializedValues) noexcept {
    if (!data) return;
    for (std::uint32_t index = initializedValues; index > 0; --index)
        destroyPropertyValue(
            valueProperty,
            data + static_cast<std::size_t>(index - 1) * stride + valueOffset);
    for (std::uint32_t index = initializedKeys; index > 0; --index)
        destroyPropertyValue(
            keyProperty,
            data + static_cast<std::size_t>(index - 1) * stride);
}

bool decodeSetValue(
    const FScriptSetLayout& layout, const FProperty* keyProperty,
    const FProperty* valueProperty, std::int32_t valueOffset,
    std::byte* destination, ValueWireReader& payload, unsigned depth) {
    if (!keyProperty ||
        !isWireWritableProperty(keyProperty, depth + 1) ||
        (keyProperty->PropertyFlags & CpfHasGetValueTypeHash) == 0 ||
        !validSetElementLayout(
            layout, keyProperty->ElementSize, valueOffset,
            valueProperty ? valueProperty->ElementSize : 0) ||
        (valueProperty && (!isWireWritableProperty(valueProperty, depth + 1) ||
            valueOffset < keyProperty->ElementSize ||
            valueOffset > layout.Size - valueProperty->ElementSize))) return false;
    std::uint32_t count{};
    if (!payload.read(count) ||
        count > static_cast<std::uint32_t>(MaximumContainerElements)) return false;
    if (count == 0) return payload.empty();

    FScriptSet current{};
    if (!safeCopy(&current, destination, sizeof(current)) ||
        current.Elements.Data.Data || current.Elements.Data.Num != 0 ||
        current.Elements.Data.Max != 0 ||
        current.Elements.AllocationFlags.Allocator.SecondaryData ||
        current.Hash.SecondaryData) return false;

    const auto stride = static_cast<std::size_t>(layout.Size);
    if (count > MaximumValueWireBytes / stride) return false;
    void* dataAllocation{};
    if (!safeUnrealMalloc(
            static_cast<std::size_t>(count) * stride,
            static_cast<std::uint32_t>(layout.SparseArrayLayout.Alignment),
            dataAllocation)) return false;

    const auto flagWords = (count + 31u) / 32u;
    void* flagAllocation{};
    if (flagWords > 4 && !safeUnrealMalloc(
            static_cast<std::size_t>(flagWords) * sizeof(std::uint32_t),
            alignof(std::uint32_t), flagAllocation)) {
        safeUnrealFree(dataAllocation);
        return false;
    }

    const auto hashSize = setHashSize(count);
    void* hashAllocation{};
    if (hashSize > 1 && !safeUnrealMalloc(
            static_cast<std::size_t>(hashSize) * sizeof(std::int32_t),
            alignof(std::int32_t), hashAllocation)) {
        safeUnrealFree(flagAllocation);
        safeUnrealFree(dataAllocation);
        return false;
    }

    FScriptSet value{};
    value.Elements.Data = {
        dataAllocation, static_cast<std::int32_t>(count), static_cast<std::int32_t>(count)};
    value.Elements.AllocationFlags.NumBits = static_cast<std::int32_t>(count);
    value.Elements.AllocationFlags.MaxBits = flagWords <= 4
        ? 128 : static_cast<std::int32_t>(flagWords * 32u);
    value.Elements.AllocationFlags.Allocator.SecondaryData =
        static_cast<std::uint32_t*>(flagAllocation);
    auto* flags = flagAllocation
        ? static_cast<std::uint32_t*>(flagAllocation)
        : value.Elements.AllocationFlags.Allocator.InlineData;
    for (std::uint32_t word = 0; word < flagWords; ++word) flags[word] = ~0u;
    if ((count & 31u) != 0) flags[flagWords - 1] = (1u << (count & 31u)) - 1u;
    value.Elements.FirstFreeIndex = -1;
    value.Elements.NumFreeIndices = 0;
    value.Hash.InlineData = -1;
    value.Hash.SecondaryData = static_cast<std::int32_t*>(hashAllocation);
    value.HashSize = static_cast<std::int32_t>(hashSize);
    auto* buckets = hashAllocation
        ? static_cast<std::int32_t*>(hashAllocation) : &value.Hash.InlineData;
    for (std::uint32_t index = 0; index < hashSize; ++index) buckets[index] = -1;

    auto* data = static_cast<std::byte*>(dataAllocation);
    std::uint32_t initializedKeys{};
    std::uint32_t initializedValues{};
    bool complete = true;
    for (std::uint32_t index = 0; index < count; ++index) {
        auto* element = data + static_cast<std::size_t>(index) * stride;
        if (!initializePropertyValue(keyProperty, element)) {
            complete = false;
            break;
        }
        ++initializedKeys;
        if (!decodeValueNode(keyProperty, element, payload, depth + 1)) {
            complete = false;
            break;
        }
        if (valueProperty) {
            auto* mappedValue = element + valueOffset;
            if (!initializePropertyValue(valueProperty, mappedValue)) {
                complete = false;
                break;
            }
            ++initializedValues;
            if (!decodeValueNode(valueProperty, mappedValue, payload, depth + 1)) {
                complete = false;
                break;
            }
        }

        std::uint32_t hash{};
        if (!safePropertyHash(keyProperty, element, hash)) {
            complete = false;
            break;
        }
        const auto bucket = hash & (hashSize - 1u);
        for (auto candidate = buckets[bucket]; candidate != -1;) {
            if (candidate < 0 || candidate >= static_cast<std::int32_t>(index)) {
                complete = false;
                break;
            }
            auto* existing = data + static_cast<std::size_t>(candidate) * stride;
            bool identical{};
            if (!safePropertyIdentical(keyProperty, element, existing, identical) || identical) {
                complete = false;
                break;
            }
            std::memcpy(&candidate, existing + layout.HashNextIdOffset, sizeof(candidate));
        }
        if (!complete) break;
        const auto previous = buckets[bucket];
        const auto bucketIndex = static_cast<std::int32_t>(bucket);
        std::memcpy(element + layout.HashNextIdOffset, &previous, sizeof(previous));
        std::memcpy(element + layout.HashIndexOffset, &bucketIndex, sizeof(bucketIndex));
        buckets[bucket] = static_cast<std::int32_t>(index);
    }

    if (!complete || initializedKeys != count ||
        (valueProperty && initializedValues != count) || !payload.empty() ||
        !safeCopy(destination, &value, sizeof(value))) {
        destroySetElements(
            keyProperty, valueProperty, valueOffset, data, layout.Size,
            initializedKeys, initializedValues);
        safeUnrealFree(hashAllocation);
        safeUnrealFree(flagAllocation);
        safeUnrealFree(dataAllocation);
        return false;
    }
    return true;
}

bool decodeValueNode(
    const FProperty* property, std::byte* destination,
    ValueWireReader& input, unsigned depth) {
    if (!property || !destination || depth > MaximumValueDepth ||
        !readable(property, sizeof(FProperty))) return false;
    std::uint32_t encodedKind{};
    std::uint32_t payloadSize{};
    if (!input.read(encodedKind) || !input.read(payloadSize) ||
        payloadSize > MaximumValueWireBytes) return false;
    ValueWireReader payload(nullptr, 0);
    if (!input.take(payloadSize, payload) ||
        encodedKind != static_cast<std::uint32_t>(propertyKind(property))) return false;
    const auto kind = static_cast<BriefcasePropertyKind>(encodedKind);

    if (kind == BRIEFCASE_PROPERTY_STRING || kind == BRIEFCASE_PROPERTY_TEXT) {
        std::wstring text;
        return payload.readWideString(text) && payload.empty() &&
               replaceInitializedTextValue(property, destination, kind, text);
    }
    if (kind == BRIEFCASE_PROPERTY_ARRAY)
        return decodeArrayValue(
            reinterpret_cast<const FArrayProperty*>(property), destination,
            payload, depth);
    if (kind == BRIEFCASE_PROPERTY_SET) {
        if (!readable(property, sizeof(FSetProperty))) return false;
        const auto* set = reinterpret_cast<const FSetProperty*>(property);
        return decodeSetValue(
            set->SetLayout, set->ElementProperty, nullptr, 0,
            destination, payload, depth);
    }
    if (kind == BRIEFCASE_PROPERTY_MAP) {
        if (!readable(property, sizeof(FMapProperty))) return false;
        const auto* map = reinterpret_cast<const FMapProperty*>(property);
        return decodeSetValue(
            map->MapLayout.SetLayout, map->KeyProperty, map->ValueProperty,
            map->MapLayout.ValueOffset, destination, payload, depth);
    }
    if (kind == BRIEFCASE_PROPERTY_STRUCT) {
        std::uint32_t marker{};
        if (!payload.read(marker)) return false;
        if (marker != RecursiveStructWireMarker) {
            if (marker != static_cast<std::uint32_t>(property->ElementSize) ||
                !isPreparedCanonicalProperty(property, depth) ||
                marker > MaximumValueWireBytes) return false;
            std::vector<std::byte> canonical(marker);
            return payload.readBytes(canonical.data(), canonical.size()) && payload.empty() &&
                   writeCanonicalFixedValue(
                       property, destination, canonical.data(), canonical.size(), depth);
        }
        if (!readable(property, sizeof(FStructProperty))) return false;
        const auto* structure = reinterpret_cast<const FStructProperty*>(property)->Struct;
        std::uint32_t nativeSize{};
        std::uint32_t fieldCount{};
        if (!structure || !readable(structure, sizeof(UStruct)) ||
            !payload.read(nativeSize) ||
            nativeSize != static_cast<std::uint32_t>(property->ElementSize) ||
            !payload.read(fieldCount) ||
            fieldCount > static_cast<std::uint32_t>(MaximumContainerElements)) return false;
        for (std::uint32_t index = 0; index < fieldCount; ++index) {
            std::int32_t offset{};
            std::wstring name;
            if (!payload.read(offset) || offset < 0 || !payload.readWideString(name)) return false;
            const auto* field = findStructField(
                structure, offset, name, static_cast<std::int32_t>(nativeSize));
            if (!field) {
                if (!skipValueNode(payload)) return false;
                continue;
            }
            if (!isWireWritableProperty(field, depth + 1) ||
                !decodeValueNode(
                    field, destination + offset, payload, depth + 1)) return false;
        }
        return payload.empty();
    }

    if (!isPreparedCanonicalProperty(property, depth)) return false;
    const auto required = kind == BRIEFCASE_PROPERTY_BOOL ? 1u :
        kind == BRIEFCASE_PROPERTY_OBJECT
            ? static_cast<std::uint32_t>(sizeof(BriefcaseObjectHandle))
            : static_cast<std::uint32_t>(property->ElementSize);
    if (payloadSize != required) return false;
    std::vector<std::byte> canonical(required);
    return payload.readBytes(canonical.data(), canonical.size()) && payload.empty() &&
           writeCanonicalFixedValue(
               property, destination, canonical.data(), canonical.size(), depth);
}

bool decodeValueEnvelope(
    const FProperty* property, std::byte* destination,
    const std::uint8_t* input, std::uint32_t inputSize) {
    if (!property || !destination || !input || inputSize < 12 ||
        inputSize > MaximumValueWireBytes || !readableRange(input, inputSize)) return false;
    ValueWireReader reader(input, inputSize);
    std::uint32_t magic{};
    return reader.read(magic) && magic == ValueWireMagic &&
           decodeValueNode(property, destination, reader, 0) && reader.empty();
}

BriefcaseUnrealResult replacePropertyValueFromWire(
    const FProperty* property, std::byte* destination,
    const std::uint8_t* input, std::uint32_t inputSize) {
    if (!isWireWritableProperty(property, 0)) return BRIEFCASE_UNREAL_UNSUPPORTED;
    if (!writable(destination, static_cast<std::size_t>(property->ElementSize)))
        return BRIEFCASE_UNREAL_UNREADABLE;
    std::vector<std::byte> temporary(static_cast<std::size_t>(property->ElementSize));
    if (!initializePropertyValue(property, temporary.data()))
        return BRIEFCASE_UNREAL_UNREADABLE;
    if (!decodeValueEnvelope(property, temporary.data(), input, inputSize)) {
        destroyPropertyValue(property, temporary.data());
        return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    }
    if (!destroyPropertyValue(property, destination) ||
        !safeCopy(destination, temporary.data(), temporary.size())) {
        destroyPropertyValue(property, temporary.data());
        return BRIEFCASE_UNREAL_UNREADABLE;
    }
    // Ownership of every nested allocation moved into the destination.
    return BRIEFCASE_UNREAL_OK;
}


} // namespace briefcase::unreal
