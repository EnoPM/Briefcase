#pragma once

#include "UnrealLayout.h"

#include <Briefcase/BriefcaseModApi.h>

#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>

namespace briefcase::unreal {

using FNameToString = void(__fastcall*)(const FName*, FStringBuffer*);

// Runtime-owned, non-owning views. They are published only after symbol and
// object-array validation succeeds, and remain valid for the process lifetime.
extern const FUObjectArray* RuntimeObjects;
extern FNameToString RuntimeNameConverter;

[[nodiscard]] bool readable(const void* address, std::size_t size);
[[nodiscard]] bool readableRange(const void* address, std::size_t size);
[[nodiscard]] bool writable(void* address, std::size_t size);
[[nodiscard]] bool executable(const void* address);
bool safeCopy(
    void* destination, const void* source, std::size_t size) noexcept;

[[nodiscard]] std::wstring hexadecimal(std::uintptr_t value);
[[nodiscard]] std::wstring nameToString(
    const FName& name, FNameToString convert);
[[nodiscard]] std::wstring objectPath(
    const UObject* object, FNameToString convert);

[[nodiscard]] FUObjectItem* itemAt(
    const FChunkedFixedUObjectArray& array, std::int32_t index);
[[nodiscard]] bool isRegisteredObject(const UObject* object);
[[nodiscard]] bool saneObjectArray(const FUObjectArray* objects);
[[nodiscard]] const UObject* findObjectByPath(std::wstring_view requestedPath);
[[nodiscard]] const UObject* resolveObject(BriefcaseObjectHandle handle);
bool makeHandle(
    const UObject* object, BriefcaseObjectHandle& result);
[[nodiscard]] bool isClassObject(const UObject* object);
[[nodiscard]] bool objectIsA(
    const UObject* object, const UObject* requestedClass);
[[nodiscard]] const FProperty* findProperty(
    const UObject* ownerClass, const std::wstring& propertyName);
[[nodiscard]] const UFunction* findFunction(
    const UObject* ownerClass, const std::wstring& functionName);
[[nodiscard]] const FProperty* findFunctionParameter(
    const UFunction* function,
    std::wstring_view typeName,
    std::int32_t offset,
    std::int32_t size);
[[nodiscard]] std::wstring propertyTypeName(const FProperty* property);

bool decodeUtf8(
    const char* text, std::uint32_t length, std::wstring& result);
[[nodiscard]] BriefcaseUnrealResult writeUtf8(
    const std::wstring& text,
    char* destination,
    std::uint32_t capacity,
    std::uint32_t* requiredBytes);

} // namespace briefcase::unreal
