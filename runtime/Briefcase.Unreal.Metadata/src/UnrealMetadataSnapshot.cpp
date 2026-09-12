#include "UnrealMetadataSnapshot.h"

#include "BinarySnapshotWriter.h"
#include "Log.h"
#include "UnrealReflection.h"

#include <Windows.h>
#include <algorithm>
#include <cctype>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <sstream>
#include <string>
#include <string_view>
#include <system_error>
#include <utility>
#include <vector>

namespace briefcase::metadata {
using namespace briefcase::unreal;

std::string utf8(std::wstring_view value) {
    if (value.empty()) return {};
    const auto required = WideCharToMultiByte(
        CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()),
        nullptr, 0, nullptr, nullptr);
    if (required <= 0) return {};
    std::string result(static_cast<std::size_t>(required), '\0');
    if (WideCharToMultiByte(
            CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()),
            result.data(), required, nullptr, nullptr) != required)
        return {};
    return result;
}

void writeJsonString(std::ostream& output, std::wstring_view value) {
    output.put('"');
    for (const auto character : utf8(value)) {
        const auto byte = static_cast<unsigned char>(character);
        switch (byte) {
        case '"': output << "\\\""; break;
        case '\\': output << "\\\\"; break;
        case '\b': output << "\\b"; break;
        case '\f': output << "\\f"; break;
        case '\n': output << "\\n"; break;
        case '\r': output << "\\r"; break;
        case '\t': output << "\\t"; break;
        default:
            if (byte < 0x20) {
                constexpr char digits[] = "0123456789ABCDEF";
                output << "\\u00" << digits[byte >> 4] << digits[byte & 0x0F];
            } else {
                output.put(static_cast<char>(byte));
            }
            break;
        }
    }
    output.put('"');
}

void writeTypeReference(std::ostream& output, const UObject* object) {
    if (!isRegisteredObject(object)) return;
    const auto path = objectPath(object, RuntimeNameConverter);
    if (path.empty()) return;
    output << ",\"referencedTypePath\":";
    writeJsonString(output, path);
}

void writeFunctionSnapshot(
    std::ostream& output, const UFunction* function, unsigned depth);

void writePropertyTypeSnapshot(
    std::ostream& output,
    const FProperty* property,
    unsigned depth = 0) {
    constexpr unsigned MaximumTypeDepth = 8;
    const auto typeName = propertyTypeName(property);
    output << "{\"unrealType\":";
    writeJsonString(output, typeName);
    output << ",\"elementSize\":" << (property ? property->ElementSize : 0);
    if (!property || depth >= MaximumTypeDepth) {
        output << '}';
        return;
    }

    if (typeName == L"BoolProperty" && readable(property, sizeof(FBoolProperty))) {
        const auto* boolean = reinterpret_cast<const FBoolProperty*>(property);
        output << ",\"booleanLayout\":{\"fieldSize\":"
               << static_cast<unsigned>(boolean->FieldSize)
               << ",\"byteOffset\":" << static_cast<unsigned>(boolean->ByteOffset)
               << ",\"byteMask\":" << static_cast<unsigned>(boolean->ByteMask)
               << ",\"fieldMask\":" << static_cast<unsigned>(boolean->FieldMask)
               << '}';
    } else if (typeName == L"StructProperty" &&
               readable(property, sizeof(FStructProperty))) {
        writeTypeReference(
            output, reinterpret_cast<const FStructProperty*>(property)->Struct);
    } else if ((typeName == L"ObjectProperty" || typeName == L"WeakObjectProperty" ||
                typeName == L"LazyObjectProperty" || typeName == L"SoftObjectProperty") &&
               readable(property, sizeof(FObjectPropertyBase))) {
        writeTypeReference(
            output, reinterpret_cast<const FObjectPropertyBase*>(property)->PropertyClass);
    } else if ((typeName == L"ClassProperty" || typeName == L"SoftClassProperty") &&
               readable(property, sizeof(FClassProperty))) {
        writeTypeReference(
            output, reinterpret_cast<const FClassProperty*>(property)->MetaClass);
    } else if (typeName == L"InterfaceProperty" &&
               readable(property, sizeof(FInterfaceProperty))) {
        writeTypeReference(
            output, reinterpret_cast<const FInterfaceProperty*>(property)->InterfaceClass);
    } else if (typeName == L"EnumProperty" && readable(property, sizeof(FEnumProperty))) {
        const auto* enumeration = reinterpret_cast<const FEnumProperty*>(property);
        writeTypeReference(output, enumeration->Enum);
        if (enumeration->UnderlyingProperty &&
            readable(enumeration->UnderlyingProperty, sizeof(FProperty))) {
            output << ",\"underlyingType\":";
            writePropertyTypeSnapshot(output, enumeration->UnderlyingProperty, depth + 1);
        }
    } else if (typeName == L"ByteProperty" && readable(property, sizeof(FByteProperty))) {
        writeTypeReference(output, reinterpret_cast<const FByteProperty*>(property)->Enum);
    } else if ((typeName == L"DelegateProperty" ||
                typeName == L"MulticastDelegateProperty" ||
                typeName == L"MulticastInlineDelegateProperty" ||
                typeName == L"MulticastSparseDelegateProperty") &&
               readable(property, sizeof(FDelegateProperty))) {
        const auto* signature =
            reinterpret_cast<const FDelegateProperty*>(property)->SignatureFunction;
        writeTypeReference(output, signature);
        if (isRegisteredObject(signature) && readable(signature, sizeof(UFunction))) {
            output << ",\"delegateSignature\":";
            writeFunctionSnapshot(output, signature, depth + 1);
        }
    } else if (typeName == L"FieldPathProperty" &&
               readable(property, sizeof(FFieldPathProperty))) {
        const auto* fieldClass =
            reinterpret_cast<const FFieldPathProperty*>(property)->PropertyClass;
        if (fieldClass && readable(fieldClass, sizeof(FFieldClass))) {
            output << ",\"referencedTypePath\":";
            writeJsonString(output, L"FFieldClass:" +
                nameToString(fieldClass->Name, RuntimeNameConverter));
        }
    }

    const auto writeNested = [&](const char* name, const FProperty* nested) {
        if (!nested || !readable(nested, sizeof(FProperty))) return;
        output << ",\"" << name << "\":";
        writePropertyTypeSnapshot(output, nested, depth + 1);
    };
    if (typeName == L"ArrayProperty" && readable(property, sizeof(FArrayProperty))) {
        writeNested("innerType", reinterpret_cast<const FArrayProperty*>(property)->Inner);
    } else if (typeName == L"SetProperty" && readable(property, sizeof(FSetProperty))) {
        writeNested(
            "innerType", reinterpret_cast<const FSetProperty*>(property)->ElementProperty);
    } else if (typeName == L"MapProperty" && readable(property, sizeof(FMapProperty))) {
        const auto* map = reinterpret_cast<const FMapProperty*>(property);
        writeNested("keyType", map->KeyProperty);
        writeNested("valueType", map->ValueProperty);
    }
    output << '}';
}

void writePropertySnapshot(
    std::ostream& output, const FProperty* property, unsigned depth = 0) {
    const auto typeName = propertyTypeName(property);
    output << "{\"name\":";
    writeJsonString(output, nameToString(property->NamePrivate, RuntimeNameConverter));
    output << ",\"unrealType\":";
    writeJsonString(output, typeName);
    output << ",\"offset\":" << property->OffsetInternal
           << ",\"elementSize\":" << property->ElementSize
           << ",\"arrayDimension\":" << property->ArrayDim
           << ",\"flags\":" << property->PropertyFlags
           << ",\"type\":";
    writePropertyTypeSnapshot(output, property, depth);
    output << '}';
}
void writeProperties(
    std::ostream& output, const FField* first, bool parametersOnly,
    unsigned depth = 0) {
    constexpr std::uint64_t ParameterFlag = 0x80;
    bool needsComma = false;
    auto* field = first;
    for (unsigned visited = 0; field && visited < 4096; ++visited) {
        if (!readable(field, sizeof(FProperty))) break;
        const auto* property = reinterpret_cast<const FProperty*>(field);
        if (!parametersOnly || (property->PropertyFlags & ParameterFlag) != 0) {
            if (needsComma) output.put(',');
            writePropertySnapshot(output, property, depth);
            needsComma = true;
        }
        field = field->Next;
    }
}

void writeFunctionSnapshot(
    std::ostream& output, const UFunction* function, unsigned depth) {
    output << "{\"name\":";
    writeJsonString(output, nameToString(function->NamePrivate, RuntimeNameConverter));
    output << ",\"flags\":" << function->FunctionFlags
           << ",\"parameterSize\":" << function->ParmsSize
           << ",\"parameterCount\":" << static_cast<unsigned>(function->NumParms)
           << ",\"parameters\":[";
    writeProperties(output, function->ChildProperties, true, depth + 1);
    output << "]}";
}

void writeFunctions(std::ostream& output, const UField* first) {
    bool needsComma = false;
    auto* child = first;
    for (unsigned visited = 0; child && visited < 4096; ++visited) {
        if (!readable(child, sizeof(UField)) ||
            !readable(child->ClassPrivate, sizeof(UObject)))
            break;
        if (nameToString(child->ClassPrivate->NamePrivate, RuntimeNameConverter) == L"Function" &&
            readable(child, sizeof(UFunction))) {
            const auto* function = reinterpret_cast<const UFunction*>(child);
            if (needsComma) output.put(',');
            writeFunctionSnapshot(output, function, 0);
            needsComma = true;
        }
        child = child->Next;
    }
}

void writeEnumValues(std::ostream& output, const UEnum* enumeration) {
    if (!enumeration || !readable(enumeration, sizeof(UEnum)) ||
        enumeration->NamesNum < 0 || enumeration->NamesMax < enumeration->NamesNum ||
        enumeration->NamesNum > 65'536 ||
        (enumeration->NamesNum > 0 &&
         (!enumeration->Names ||
          !readable(enumeration->Names,
              static_cast<std::size_t>(enumeration->NamesNum) * sizeof(FEnumNameValue)))))
        return;

    for (std::int32_t index = 0; index < enumeration->NamesNum; ++index) {
        if (index != 0) output.put(',');
        output << "{\"name\":";
        writeJsonString(
            output, nameToString(enumeration->Names[index].Name, RuntimeNameConverter));
        output << ",\"value\":" << enumeration->Names[index].Value << '}';
    }
}
struct ReflectedType {
    const UObject* Object;
    std::wstring Path;
    std::wstring Kind;
};

enum class SdkSnapshotFormat {
    Binary,
    Json
};

std::string configuredString(
    const std::filesystem::path& root, std::string_view key) {
    constexpr std::uintmax_t MaximumConfigurationBytes = 1024 * 1024;
    const auto path = root / L"Briefcase" / L"loader.json";
    std::error_code error;
    const auto size = std::filesystem::file_size(path, error);
    if (error || size > MaximumConfigurationBytes) return {};

    std::ifstream input(path, std::ios::binary);
    if (!input) return {};
    std::string contents(static_cast<std::size_t>(size), '\0');
    input.read(contents.data(), static_cast<std::streamsize>(contents.size()));
    if (!input && !input.eof()) return {};

    const auto quotedKey = '"' + std::string(key) + '"';
    auto position = contents.find(quotedKey);
    if (position == std::string::npos) return {};
    position = contents.find(':', position + quotedKey.size());
    if (position == std::string::npos) return {};
    position = contents.find('"', position + 1);
    if (position == std::string::npos) return {};
    const auto end = contents.find('"', position + 1);
    if (end == std::string::npos) return {};

    auto value = contents.substr(position + 1, end - position - 1);
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char character) {
        return static_cast<char>(std::tolower(character));
    });
    return value;
}

SdkSnapshotFormat configuredSnapshotFormat(const std::filesystem::path& root) {
    const auto value = configuredString(root, "sdkSnapshotFormat");
    if (value == "json") return SdkSnapshotFormat::Json;
    if (!value.empty() && value != "binary")
        briefcase::log(L"sdk snapshot: unknown sdkSnapshotFormat; using binary");
    return SdkSnapshotFormat::Binary;
}

bool configuredSnapshotRefreshAlways(const std::filesystem::path& root) {
    const auto value = configuredString(root, "sdkSnapshotRefresh");
    if (value == "always") return true;
    if (!value.empty() && value != "missing")
        briefcase::log(L"sdk snapshot: unknown sdkSnapshotRefresh; using missing");
    return false;
}

std::wstring snapshotFileStem(const briefcase::profile::RuntimeProfile& runtimeProfile) {
    std::wostringstream stem;
    stem << L"DeceiveInc." << runtimeProfile.TargetName << L'.'
         << std::uppercase << std::hex << std::setw(8) << std::setfill(L'0')
         << runtimeProfile.PeTimestamp << L'-' << std::setw(8)
         << runtimeProfile.ImageSize;
    return stem.str();
}

std::filesystem::path snapshotDestination(
    const std::filesystem::path& root,
    const briefcase::profile::RuntimeProfile& runtimeProfile,
    SdkSnapshotFormat format) {
    const auto extension = format == SdkSnapshotFormat::Json ? L".json" : L".bsnap";
    return root / L"Briefcase" / L"Core" / L"Sdk" / L"Metadata" /
           (snapshotFileStem(runtimeProfile) + extension);
}

bool shouldCaptureSdkSnapshot(
    const std::filesystem::path& root,
    const briefcase::profile::RuntimeProfile& runtimeProfile) {
    if (configuredSnapshotRefreshAlways(root)) return true;
    std::error_code error;
    const auto size = std::filesystem::file_size(
        snapshotDestination(root, runtimeProfile, configuredSnapshotFormat(root)), error);
    return error || size == 0;
}

std::vector<const FProperty*> snapshotProperties(
    const FField* first, bool parametersOnly) {
    constexpr std::uint64_t ParameterFlag = 0x80;
    std::vector<const FProperty*> properties;
    auto* field = first;
    for (unsigned visited = 0; field && visited < 4096; ++visited) {
        if (!readable(field, sizeof(FProperty))) break;
        const auto* property = reinterpret_cast<const FProperty*>(field);
        if (!parametersOnly || (property->PropertyFlags & ParameterFlag) != 0)
            properties.push_back(property);
        field = field->Next;
    }
    return properties;
}

std::vector<const UFunction*> snapshotFunctions(const UField* first) {
    std::vector<const UFunction*> functions;
    auto* child = first;
    for (unsigned visited = 0; child && visited < 4096; ++visited) {
        if (!readable(child, sizeof(UField)) ||
            !isRegisteredObject(child->ClassPrivate))
            break;
        if (nameToString(child->ClassPrivate->NamePrivate, RuntimeNameConverter) == L"Function" &&
            readable(child, sizeof(UFunction)))
            functions.push_back(reinterpret_cast<const UFunction*>(child));
        child = child->Next;
    }
    return functions;
}

void writeBinaryString(
    briefcase::snapshot::BinarySnapshotWriter& output, std::wstring_view value) {
    output.writeString(utf8(value));
}

void writeBinaryNullableString(
    briefcase::snapshot::BinarySnapshotWriter& output, std::wstring_view value) {
    output.writeBoolean(!value.empty());
    if (!value.empty()) writeBinaryString(output, value);
}

void writeBinaryFunctionSnapshot(
    briefcase::snapshot::BinarySnapshotWriter& output,
    const UFunction* function,
    unsigned depth);

void writeBinaryPropertyType(
    briefcase::snapshot::BinarySnapshotWriter& output,
    const FProperty* property,
    unsigned depth = 0) {
    constexpr unsigned MaximumTypeDepth = 8;
    const auto typeName = propertyTypeName(property);
    writeBinaryString(output, typeName);
    output.writeInt32(property ? property->ElementSize : 0);

    std::wstring referencedTypePath;
    const FProperty* innerType{};
    const FProperty* keyType{};
    const FProperty* valueType{};
    const FProperty* underlyingType{};
    const FBoolProperty* booleanLayout{};
    const UFunction* delegateSignature{};

    if (property && depth < MaximumTypeDepth) {
        const auto reference = [&](const UObject* object) {
            return isRegisteredObject(object)
                ? objectPath(object, RuntimeNameConverter)
                : std::wstring{};
        };
        const auto nested = [](const FProperty* candidate) {
            return candidate && readable(candidate, sizeof(FProperty)) ? candidate : nullptr;
        };

        if (typeName == L"BoolProperty" && readable(property, sizeof(FBoolProperty))) {
            booleanLayout = reinterpret_cast<const FBoolProperty*>(property);
        } else if (typeName == L"StructProperty" &&
                   readable(property, sizeof(FStructProperty))) {
            referencedTypePath = reference(
                reinterpret_cast<const FStructProperty*>(property)->Struct);
        } else if ((typeName == L"ObjectProperty" || typeName == L"WeakObjectProperty" ||
                    typeName == L"LazyObjectProperty" || typeName == L"SoftObjectProperty") &&
                   readable(property, sizeof(FObjectPropertyBase))) {
            referencedTypePath = reference(
                reinterpret_cast<const FObjectPropertyBase*>(property)->PropertyClass);
        } else if ((typeName == L"ClassProperty" || typeName == L"SoftClassProperty") &&
                   readable(property, sizeof(FClassProperty))) {
            referencedTypePath = reference(
                reinterpret_cast<const FClassProperty*>(property)->MetaClass);
        } else if (typeName == L"InterfaceProperty" &&
                   readable(property, sizeof(FInterfaceProperty))) {
            referencedTypePath = reference(
                reinterpret_cast<const FInterfaceProperty*>(property)->InterfaceClass);
        } else if (typeName == L"EnumProperty" &&
                   readable(property, sizeof(FEnumProperty))) {
            const auto* enumeration = reinterpret_cast<const FEnumProperty*>(property);
            referencedTypePath = reference(enumeration->Enum);
            underlyingType = nested(enumeration->UnderlyingProperty);
        } else if (typeName == L"ByteProperty" &&
                   readable(property, sizeof(FByteProperty))) {
            referencedTypePath = reference(
                reinterpret_cast<const FByteProperty*>(property)->Enum);
        } else if ((typeName == L"DelegateProperty" ||
                    typeName == L"MulticastDelegateProperty" ||
                    typeName == L"MulticastInlineDelegateProperty" ||
                    typeName == L"MulticastSparseDelegateProperty") &&
                   readable(property, sizeof(FDelegateProperty))) {
            const auto* signature =
                reinterpret_cast<const FDelegateProperty*>(property)->SignatureFunction;
            referencedTypePath = reference(signature);
            if (isRegisteredObject(signature) && readable(signature, sizeof(UFunction)))
                delegateSignature = signature;
        } else if (typeName == L"FieldPathProperty" &&
                   readable(property, sizeof(FFieldPathProperty))) {
            const auto* fieldClass =
                reinterpret_cast<const FFieldPathProperty*>(property)->PropertyClass;
            if (fieldClass && readable(fieldClass, sizeof(FFieldClass))) {
                const auto name = nameToString(fieldClass->Name, RuntimeNameConverter);
                if (!name.empty()) referencedTypePath = L"FFieldClass:" + name;
            }
        }

        if (typeName == L"ArrayProperty" && readable(property, sizeof(FArrayProperty))) {
            innerType = nested(reinterpret_cast<const FArrayProperty*>(property)->Inner);
        } else if (typeName == L"SetProperty" &&
                   readable(property, sizeof(FSetProperty))) {
            innerType = nested(
                reinterpret_cast<const FSetProperty*>(property)->ElementProperty);
        } else if (typeName == L"MapProperty" &&
                   readable(property, sizeof(FMapProperty))) {
            const auto* map = reinterpret_cast<const FMapProperty*>(property);
            keyType = nested(map->KeyProperty);
            valueType = nested(map->ValueProperty);
        }
    }

    writeBinaryNullableString(output, referencedTypePath);
    const auto writeNested = [&](const FProperty* nested) {
        output.writeBoolean(nested != nullptr);
        if (nested) writeBinaryPropertyType(output, nested, depth + 1);
    };
    writeNested(innerType);
    writeNested(keyType);
    writeNested(valueType);
    writeNested(underlyingType);
    output.writeBoolean(booleanLayout != nullptr);
    if (booleanLayout) {
        output.writeByte(booleanLayout->FieldSize);
        output.writeByte(booleanLayout->ByteOffset);
        output.writeByte(booleanLayout->ByteMask);
        output.writeByte(booleanLayout->FieldMask);
    }
    output.writeBoolean(delegateSignature != nullptr);
    if (delegateSignature)
        writeBinaryFunctionSnapshot(output, delegateSignature, depth + 1);
}

void writeBinaryProperty(
    briefcase::snapshot::BinarySnapshotWriter& output,
    const FProperty* property,
    unsigned depth = 0) {
    writeBinaryString(output, nameToString(property->NamePrivate, RuntimeNameConverter));
    writeBinaryString(output, propertyTypeName(property));
    output.writeInt32(property->OffsetInternal);
    output.writeInt32(property->ElementSize);
    output.writeInt32(property->ArrayDim);
    output.writeUInt64(property->PropertyFlags);
    // Legacy schema-2 fields are absent from schema-3 snapshots.
    output.writeBoolean(false);
    output.writeBoolean(false);
    output.writeBoolean(true);
    writeBinaryPropertyType(output, property, depth);
}

void writeBinaryProperties(
    briefcase::snapshot::BinarySnapshotWriter& output,
    const FField* first,
    bool parametersOnly,
    unsigned depth = 0) {
    const auto properties = snapshotProperties(first, parametersOnly);
    output.writeInt32(static_cast<std::int32_t>(properties.size()));
    for (const auto* property : properties)
        writeBinaryProperty(output, property, depth);
}

void writeBinaryFunctionSnapshot(
    briefcase::snapshot::BinarySnapshotWriter& output,
    const UFunction* function,
    unsigned depth) {
    writeBinaryString(
        output, nameToString(function->NamePrivate, RuntimeNameConverter));
    output.writeUInt32(function->FunctionFlags);
    output.writeInt32(function->ParmsSize);
    output.writeInt32(function->NumParms);
    writeBinaryProperties(output, function->ChildProperties, true, depth + 1);
}

void writeBinaryFunctions(
    briefcase::snapshot::BinarySnapshotWriter& output,
    const UField* first) {
    const auto functions = snapshotFunctions(first);
    output.writeInt32(static_cast<std::int32_t>(functions.size()));
    for (const auto* function : functions) {
        writeBinaryFunctionSnapshot(output, function, 0);
    }
}

void writeBinaryEnumValues(
    briefcase::snapshot::BinarySnapshotWriter& output,
    const UEnum* enumeration) {
    if (!enumeration || !readable(enumeration, sizeof(UEnum)) ||
        enumeration->NamesNum < 0 || enumeration->NamesMax < enumeration->NamesNum ||
        enumeration->NamesNum > 65'536 ||
        (enumeration->NamesNum > 0 &&
         (!enumeration->Names ||
          !readable(enumeration->Names,
              static_cast<std::size_t>(enumeration->NamesNum) * sizeof(FEnumNameValue))))) {
        output.writeInt32(0);
        return;
    }
    output.writeInt32(enumeration->NamesNum);
    for (std::int32_t index = 0; index < enumeration->NamesNum; ++index) {
        writeBinaryString(
            output, nameToString(enumeration->Names[index].Name, RuntimeNameConverter));
        output.writeInt64(enumeration->Names[index].Value);
    }
}

void writeBinarySdkSnapshot(
    std::ostream& stream,
    const std::vector<ReflectedType>& types,
    std::int32_t objectCount,
    const briefcase::profile::RuntimeProfile& runtimeProfile) {
    briefcase::snapshot::BinarySnapshotWriter output(stream);
    output.writeHeader();
    output.writeInt32(4);
    writeBinaryString(output, runtimeProfile.TargetName);
    writeBinaryString(output, runtimeProfile.SdkAssemblyName);
    output.writeUInt32(runtimeProfile.PeTimestamp);
    output.writeUInt32(runtimeProfile.ImageSize);
    output.writeInt32(objectCount);
    output.writeInt32(static_cast<std::int32_t>(types.size()));

    for (const auto& type : types) {
        const bool enumeration = type.Kind == L"Enum";
        const auto* structure = reinterpret_cast<const UStruct*>(type.Object);
        const auto* enumObject = reinterpret_cast<const UEnum*>(type.Object);
        writeBinaryString(output, type.Path);
        writeBinaryString(
            output, nameToString(type.Object->NamePrivate, RuntimeNameConverter));
        writeBinaryString(output, type.Kind);

        std::wstring superPath;
        if (!enumeration && structure->SuperStruct)
            superPath = objectPath(structure->SuperStruct, RuntimeNameConverter);
        writeBinaryNullableString(output, superPath);
        output.writeInt32(enumeration ? 0 : structure->PropertiesSize);
        if (enumeration) {
            output.writeInt32(0);
            output.writeInt32(0);
            writeBinaryEnumValues(output, enumObject);
        } else {
            writeBinaryProperties(output, structure->ChildProperties, false);
            writeBinaryFunctions(output, structure->Children);
            output.writeInt32(0);
        }
    }
}

void writeSdkSnapshot(const std::filesystem::path& root,
                      const briefcase::profile::RuntimeProfile& runtimeProfile) {
    if (!RuntimeObjects || !RuntimeNameConverter) return;

    std::vector<ReflectedType> types;
    const auto objectCount = RuntimeObjects->ObjObjects.NumElements;
    types.reserve(4096);
    for (std::int32_t index = 0; index < objectCount; ++index) {
        const auto* item = itemAt(RuntimeObjects->ObjObjects, index);
        if (!item || !readable(item->Object, sizeof(UObject)) ||
            item->Object->InternalIndex != index ||
            !readable(item->Object->ClassPrivate, sizeof(UObject)))
            continue;
        const auto kind = nameToString(
            item->Object->ClassPrivate->NamePrivate, RuntimeNameConverter);
        if (kind != L"Class" && kind != L"ScriptStruct" && kind != L"Enum") continue;
        if ((kind == L"Enum" && !readable(item->Object, sizeof(UEnum))) ||
            (kind != L"Enum" && !readable(item->Object, sizeof(UStruct))))
            continue;
        auto path = objectPath(item->Object, RuntimeNameConverter);
        if (!path.starts_with(L"/Script/")) continue;
        types.push_back({item->Object, std::move(path), kind});
    }
    std::sort(types.begin(), types.end(), [](const auto& left, const auto& right) {
        return left.Path < right.Path;
    });

    const auto format = configuredSnapshotFormat(root);
    const auto fileStem = snapshotFileStem(runtimeProfile);
    const auto destination = snapshotDestination(root, runtimeProfile, format);
    const auto directory = destination.parent_path();
    std::filesystem::create_directories(directory);
    const auto temporary = destination.wstring() + L".tmp-" +
                           std::to_wstring(GetCurrentProcessId());

    std::ofstream output(temporary, std::ios::binary | std::ios::trunc);
    if (!output) {
        briefcase::log(L"sdk snapshot: unable to create " + temporary);
        return;
    }
    if (format == SdkSnapshotFormat::Binary) {
        writeBinarySdkSnapshot(output, types, objectCount, runtimeProfile);
    } else {
        output << "{\"schemaVersion\":4,\"target\":";
        writeJsonString(output, runtimeProfile.TargetName);
        output << ",\"sdkAssemblyName\":";
        writeJsonString(output, runtimeProfile.SdkAssemblyName);
        output << ",\"gameBuild\":{\"peTimestamp\":" << runtimeProfile.PeTimestamp
               << ",\"imageSize\":" << runtimeProfile.ImageSize
               << "},\"capturedObjectCount\":" << objectCount << ",\"types\":[";

        bool needsTypeComma = false;
        for (const auto& type : types) {
            const bool enumeration = type.Kind == L"Enum";
            const auto* structure = reinterpret_cast<const UStruct*>(type.Object);
            const auto* enumObject = reinterpret_cast<const UEnum*>(type.Object);
            if ((enumeration && !readable(enumObject, sizeof(UEnum))) ||
                (!enumeration && !readable(structure, sizeof(UStruct))))
                continue;
            if (needsTypeComma) output.put(',');
            output << "{\"path\":";
            writeJsonString(output, type.Path);
            output << ",\"name\":";
            writeJsonString(output, nameToString(type.Object->NamePrivate, RuntimeNameConverter));
            output << ",\"kind\":";
            writeJsonString(output, type.Kind);

            if (enumeration) {
                output << ",\"superPath\":null,\"size\":0,\"properties\":[],"
                          "\"functions\":[],\"values\":[";
                writeEnumValues(output, enumObject);
                output << "]}";
            } else {
                output << ",\"superPath\":";
                if (structure->SuperStruct && readable(structure->SuperStruct, sizeof(UStruct)))
                    writeJsonString(output, objectPath(structure->SuperStruct, RuntimeNameConverter));
                else
                    output << "null";
                output << ",\"size\":" << structure->PropertiesSize << ",\"properties\":[";
                writeProperties(output, structure->ChildProperties, false);
                output << "],\"functions\":[";
                writeFunctions(output, structure->Children);
                output << "],\"values\":[]}";
            }
            needsTypeComma = true;
        }
        output << "]}";
    }
    output.close();
    if (!output) {
        DeleteFileW(temporary.c_str());
        briefcase::log(L"sdk snapshot: write failed for " + temporary);
        return;
    }
    if (!MoveFileExW(temporary.c_str(), destination.c_str(),
                     MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        const auto error = GetLastError();
        DeleteFileW(temporary.c_str());
        briefcase::log(L"sdk snapshot: atomic replace failed error=" + std::to_wstring(error));
        return;
    }
    const auto alternativeExtension = format == SdkSnapshotFormat::Json
        ? std::wstring_view(L".bsnap") : std::wstring_view(L".json");
    const auto alternative = directory / (fileStem + alternativeExtension.data());
    DeleteFileW(alternative.c_str());
    briefcase::log(L"sdk snapshot: wrote " + std::to_wstring(types.size()) +
             L" reflected types as " +
             (format == SdkSnapshotFormat::Json ? L"json" : L"binary") +
             L" to " + destination.wstring());
}

} // namespace briefcase::metadata
