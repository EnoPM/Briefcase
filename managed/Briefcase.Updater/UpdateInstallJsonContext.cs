using System.Text.Json.Serialization;

namespace Briefcase.Updater;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(UpdateInstallRequest))]
public sealed partial class UpdateInstallJsonContext : JsonSerializerContext;
