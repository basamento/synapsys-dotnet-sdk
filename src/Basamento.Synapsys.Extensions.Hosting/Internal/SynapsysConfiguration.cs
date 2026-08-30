using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Basamento.Synapsys.Extensions.Hosting.Internal;

internal static class SynapsysConfiguration
{
    private static readonly Dictionary<string, Action<SynapsysOptions, string>> Setters =
        new Dictionary<string, Action<SynapsysOptions, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["enabled"] = (options, value) => options.Enabled = ParseBoolean("enabled", value),
            ["coreurl"] = (options, value) => options.CoreUrl = ParseUri("core-url", value),
            ["coretoken"] = (options, value) => options.CoreToken = value.Trim(),
            ["workername"] = (options, value) => options.WorkerName = value.Trim(),
            ["host"] = (options, value) => options.Host = value.Trim(),
            ["heartbeatinterval"] = (options, value) => options.HeartbeatInterval = ParseDuration("heartbeat-interval", value),
            ["failfast"] = (options, value) => options.FailFast = ParseBoolean("fail-fast", value),
            ["connecttimeout"] = (options, value) => options.ConnectTimeout = ParseDuration("connect-timeout", value),
            ["requesttimeout"] = (options, value) => options.RequestTimeout = ParseDuration("request-timeout", value),
            ["shutdowntimeout"] = (options, value) => options.ShutdownTimeout = ParseDuration("shutdown-timeout", value),
            ["captureconsole"] = (options, value) => options.CaptureConsole = ParseBoolean("capture-console", value),
        };

    private static readonly string[] ExpectedKeys =
    {
        "enabled", "core-url", "core-token", "worker-name", "host",
        "heartbeat-interval", "fail-fast", "connect-timeout", "request-timeout",
        "shutdown-timeout", "capture-console",
    };

    internal static void Apply(IConfiguration section, SynapsysOptions options)
    {
        var unknown = new List<string>();
        foreach (var child in section.GetChildren())
        {
            var normalized = Normalize(child.Key);
            if (!Setters.TryGetValue(normalized, out var setter))
            {
                unknown.Add(child.Key);
                continue;
            }

            if (child.Value is null)
            {
                throw new SynapsysConfigurationException(
                    $"Synapsys configuration value '{child.Key}' must be a scalar value.");
            }

            setter(options, child.Value);
        }

        if (unknown.Count > 0)
        {
            var expected = string.Join(
                "', '",
                ExpectedKeys);
            throw new SynapsysConfigurationException(
                $"Unknown Synapsys options: '{string.Join("', '", unknown)}'. Expected any of: '{expected}'.");
        }
    }

    private static string Normalize(string key) =>
        key.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

    private static bool ParseBoolean(string name, string value)
    {
        if (!bool.TryParse(value.Trim(), out var result))
        {
            throw new SynapsysConfigurationException(
                $"{name} must be 'true' or 'false', but was '{value}'.");
        }

        return result;
    }

    private static Uri ParseUri(string name, string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            throw new SynapsysConfigurationException(
                $"{name} is not a valid absolute URL: '{value}'.");
        }

        return uri;
    }

    private static TimeSpan ParseDuration(string name, string value)
    {
        if (double.TryParse(
                value.Trim(),
                NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var milliseconds) &&
            double.IsFinite(milliseconds))
        {
            return TimeSpan.FromMilliseconds(milliseconds);
        }

        return SynapsysOptions.ParseDuration(name, value);
    }
}
