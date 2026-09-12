using System.Text.Json;
using Briefcase.Updater;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: Briefcase.UpdateInstaller.exe <install-request.json>");
    return 2;
}

UpdateInstallRequest? request = null;
StreamWriter? fileLog = null;
try
{
    var requestPath = Path.GetFullPath(args[0]);
    request = JsonSerializer.Deserialize(
        File.ReadAllText(requestPath),
        UpdateInstallJsonContext.Default.UpdateInstallRequest)
        ?? throw new InvalidDataException("The update request is empty.");

    var logPath = Path.Combine(
        Path.GetFullPath(request.InstallRoot), "Briefcase", "Briefcase-update.log");
    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
    fileLog = new StreamWriter(logPath, append: true) { AutoFlush = true };
    using var log = new TeeTextWriter(Console.Out, fileLog);
    log.WriteLine("");
    log.WriteLine($"[{DateTimeOffset.Now:O}] Briefcase update {request.Version} started.");
    UpdateInstallerEngine.WaitForParent(request.ParentProcessId, log);
    UpdateInstallerEngine.Apply(request, log);
    TryDelete(request.ArchivePath, log);
    TryDelete(requestPath, log);
    UpdateInstallerEngine.Restart(request, log);
    return 0;
}
catch (Exception exception)
{
    var message = $"Briefcase update failed: {exception}";
    Console.Error.WriteLine(message);
    fileLog?.WriteLine(message);
    if (request?.Restart == true)
    {
        try
        {
            using var errorLog = fileLog is null
                ? TextWriter.Null
                : new TeeTextWriter(Console.Out, fileLog);
            errorLog.WriteLine("Restarting the previous installation after the failed update.");
            UpdateInstallerEngine.Restart(request, errorLog);
        }
        catch (Exception restartException)
        {
            Console.Error.WriteLine($"Could not restart Deceive Inc.: {restartException.Message}");
            fileLog?.WriteLine($"Could not restart Deceive Inc.: {restartException}");
        }
    }
    return 1;
}
finally
{
    fileLog?.Dispose();
}

static void TryDelete(string path, TextWriter log)
{
    try
    {
        if (File.Exists(path)) File.Delete(path);
    }
    catch (Exception exception)
    {
        log.WriteLine($"Could not remove temporary update file '{path}': {exception.Message}");
    }
}

internal sealed class TeeTextWriter(TextWriter first, TextWriter second) : TextWriter
{
    public override System.Text.Encoding Encoding => first.Encoding;

    public override void Write(char value)
    {
        first.Write(value);
        second.Write(value);
    }

    public override void WriteLine(string? value)
    {
        first.WriteLine(value);
        second.WriteLine(value);
    }
}
