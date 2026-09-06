#pragma once

#include "BriefcaseModApi.h"

#include <string_view>

namespace briefcase::sdk {

class Host final {
public:
    explicit Host(const BriefcaseHostApi* api) noexcept : Api(api) {}

    [[nodiscard]] bool valid() const noexcept {
        return Api && Api->StructSize >= sizeof(BriefcaseHostApi) &&
               Api->ApiVersion == BRIEFCASE_HOST_API_VERSION && Api->Core &&
               Api->Core->StructSize >= sizeof(BriefcaseCoreApi);
    }

    [[nodiscard]] uint64_t capabilities() const noexcept {
        return valid() ? Api->Capabilities : 0;
    }

    void log(BriefcaseLogLevel level, std::string_view message) const noexcept {
        if (!valid() || !Api->Core->Log) return;
        Api->Core->Log(Api->Core->Context, level, message.data(),
                       static_cast<uint32_t>(message.size()));
    }

    void info(std::string_view message) const noexcept { log(BRIEFCASE_LOG_INFO, message); }
    void warning(std::string_view message) const noexcept { log(BRIEFCASE_LOG_WARNING, message); }
    void error(std::string_view message) const noexcept { log(BRIEFCASE_LOG_ERROR, message); }

    [[nodiscard]] BriefcaseVersion frameworkVersion() const noexcept {
        return valid() && Api->Core->GetFrameworkVersion
                   ? Api->Core->GetFrameworkVersion(Api->Core->Context)
                   : BriefcaseVersion{};
    }

    [[nodiscard]] BriefcaseGameBuild gameBuild() const noexcept {
        return valid() && Api->Core->GetGameBuild
                   ? Api->Core->GetGameBuild(Api->Core->Context)
                   : BriefcaseGameBuild{};
    }

private:
    const BriefcaseHostApi* Api;
};

} // namespace briefcase::sdk

