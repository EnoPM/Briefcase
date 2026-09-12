#pragma once

#include "RuntimeProfile.h"

#include <filesystem>

namespace briefcase::metadata {

// Production captures are immutable per executable build. Returning false
// avoids scanning the entire live UObject registry on every game launch.
// Set sdkSnapshotRefresh to "always" in loader.json for RE/development runs.
bool shouldCaptureSdkSnapshot(
    const std::filesystem::path& gameBinaryDirectory,
    const profile::RuntimeProfile& runtimeProfile);

// Collects the currently loaded reflected types and atomically writes the
// build-specific SDK snapshot selected by Briefcase/loader.json.
void writeSdkSnapshot(
    const std::filesystem::path& gameBinaryDirectory,
    const profile::RuntimeProfile& runtimeProfile);

} // namespace briefcase::metadata
