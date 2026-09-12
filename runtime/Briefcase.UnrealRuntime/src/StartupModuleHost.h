#pragma once

#include <filesystem>

namespace briefcase {

// Loads native startup companions from Briefcase/Mods/Startup. Successful
// modules stay loaded for the lifetime of the process.
void loadStartupModules(const std::filesystem::path& briefcaseDirectory);

} // namespace briefcase
