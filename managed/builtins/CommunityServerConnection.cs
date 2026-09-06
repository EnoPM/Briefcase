using System.Globalization;
using Briefcase.DeceiveInc;
using Briefcase.ModApi;

namespace Briefcase.ClientSupport;

/// <summary>
/// Shared client-side entry point for Deceive Inc. community-server travel.
/// Calls must be made from the Unreal game thread because DirectConnect is a
/// reflected UFunction and ultimately executes through ProcessEvent.
/// </summary>
internal static class CommunityServerConnection
{
    public static string NormalizeEndpoint(string value)
    {
        var candidate = value.Trim();
        if (candidate.Length == 0)
            throw new InvalidOperationException("The server endpoint is empty.");

        string host;
        string portText;
        if (candidate[0] == '[')
        {
            var closingBracket = candidate.IndexOf(']');
            if (closingBracket <= 1 || closingBracket + 1 >= candidate.Length ||
                candidate[closingBracket + 1] != ':')
                throw new InvalidOperationException(
                    "An IPv6 endpoint must use [ADDRESS]:PORT.");
            host = candidate[..(closingBracket + 1)];
            portText = candidate[(closingBracket + 2)..];
        }
        else
        {
            var separator = candidate.LastIndexOf(':');
            if (separator <= 0 || separator == candidate.Length - 1)
                throw new InvalidOperationException(
                    "The server endpoint must use IP:PORT.");
            host = candidate[..separator].Trim();
            portText = candidate[(separator + 1)..].Trim();
            if (host.Contains(':'))
                throw new InvalidOperationException(
                    "An IPv6 endpoint must use [ADDRESS]:PORT.");
        }

        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("The server address is empty.");
        if (!int.TryParse(portText, NumberStyles.None,
                CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
            throw new InvalidOperationException(
                "The server port must be between 1 and 65535.");

        return $"{host}:{port.ToString(CultureInfo.InvariantCulture)}";
    }

    public static void Connect(ModContext context, string endpoint, string password)
    {
        endpoint = NormalizeEndpoint(endpoint);
        var browser = context.Unreal
            .FindObjects(EOSServerBrowserSubsystem.StaticClass)
            .FirstOrDefault(candidate =>
                !candidate.Name.Contains("Default__", StringComparison.Ordinal));
        if (browser is null)
            throw new InvalidOperationException(
                "The live EOSServerBrowserSubsystem was not found. Open the main menu first.");

        browser.DirectConnect(endpoint, password ?? "");
    }
}
