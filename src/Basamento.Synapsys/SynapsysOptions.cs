using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Reflection;

namespace Basamento.Synapsys;

/// <summary>Configures a <see cref="SynapsysWorker"/>.</summary>
public sealed class SynapsysOptions
{
    /// <summary>Initializes options from canonical <c>SYNAPSYS_*</c> environment variables.</summary>
    public SynapsysOptions()
    {
        Enabled = ReadBoolean("SYNAPSYS_ENABLED", true);
        CoreUrl = ReadUri("SYNAPSYS_CORE_URL");
        CoreToken = ReadString("SYNAPSYS_CORE_TOKEN");
        WorkerName = ReadString("SYNAPSYS_WORKER_NAME") ?? DefaultWorkerName();
        Host = ReadString("SYNAPSYS_HOST") ?? DefaultHost();
        HeartbeatInterval = ReadDuration("SYNAPSYS_HEARTBEAT_INTERVAL", TimeSpan.FromSeconds(5));
        FailFast = ReadBoolean("SYNAPSYS_FAIL_FAST", false);
        ConnectTimeout = ReadDuration("SYNAPSYS_CONNECT_TIMEOUT", TimeSpan.FromSeconds(2));
        RequestTimeout = ReadDuration("SYNAPSYS_REQUEST_TIMEOUT", TimeSpan.FromSeconds(5));
        ShutdownTimeout = ReadDuration("SYNAPSYS_SHUTDOWN_TIMEOUT", TimeSpan.FromSeconds(10));
        CaptureConsole = ReadBoolean("SYNAPSYS_CAPTURE_CONSOLE", true);
    }

    /// <summary>Gets or sets whether Synapsys is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the absolute HTTP or HTTPS base URL of Synapsys Core.</summary>
    public Uri? CoreUrl { get; set; }

    /// <summary>Gets or sets the optional Core bearer token.</summary>
    public string? CoreToken { get; set; }

    /// <summary>Gets or sets the stable worker identity reported across restarts.</summary>
    public string? WorkerName { get; set; }

    /// <summary>Gets or sets the host reported to Core. A null or blank value lets Core infer it.</summary>
    public string? Host { get; set; }

    /// <summary>Gets or sets the positive whole-second heartbeat interval.</summary>
    public TimeSpan HeartbeatInterval { get; set; }

    /// <summary>Gets or sets whether an unreachable Core fails worker startup.</summary>
    public bool FailFast { get; set; }

    /// <summary>Gets or sets the TCP connection timeout.</summary>
    public TimeSpan ConnectTimeout { get; set; }

    /// <summary>Gets or sets the whole-request timeout.</summary>
    public TimeSpan RequestTimeout { get; set; }

    /// <summary>Gets or sets the hard budget for the complete shutdown sequence.</summary>
    public TimeSpan ShutdownTimeout { get; set; }

    /// <summary>
    /// Gets or sets whether process-scoped logging sinks are enabled. The .NET SDK never
    /// replaces the process-global <see cref="Console.Out"/> or <see cref="Console.Error"/>.
    /// </summary>
    public bool CaptureConsole { get; set; }

    internal SynapsysOptions Copy() => new()
    {
        Enabled = Enabled,
        CoreUrl = CoreUrl,
        CoreToken = CoreToken,
        WorkerName = WorkerName,
        Host = Host,
        HeartbeatInterval = HeartbeatInterval,
        FailFast = FailFast,
        ConnectTimeout = ConnectTimeout,
        RequestTimeout = RequestTimeout,
        ShutdownTimeout = ShutdownTimeout,
        CaptureConsole = CaptureConsole,
    };

    internal IReadOnlyList<string> ValidateForStartup()
    {
        if (CoreUrl is null || !CoreUrl.IsAbsoluteUri ||
            (CoreUrl.Scheme != Uri.UriSchemeHttp && CoreUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new SynapsysConfigurationException(
                "CoreUrl must be an absolute HTTP or HTTPS URL.");
        }

        if (!string.IsNullOrEmpty(CoreUrl.UserInfo) ||
            !string.IsNullOrEmpty(CoreUrl.Query) ||
            !string.IsNullOrEmpty(CoreUrl.Fragment))
        {
            throw new SynapsysConfigurationException(
                "CoreUrl must not contain user information, a query, or a fragment.");
        }

        if (string.IsNullOrWhiteSpace(WorkerName))
        {
            throw new SynapsysConfigurationException(
                "WorkerName is required. Set it explicitly or use SYNAPSYS_WORKER_NAME.");
        }

        if (HeartbeatInterval <= TimeSpan.Zero ||
            HeartbeatInterval.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new SynapsysConfigurationException(
                "HeartbeatInterval must be a positive whole number of seconds.");
        }

        ValidatePositiveDuration(ConnectTimeout, nameof(ConnectTimeout));
        ValidatePositiveDuration(RequestTimeout, nameof(RequestTimeout));
        ValidateNonNegativeDuration(ShutdownTimeout, nameof(ShutdownTimeout));

        var warnings = new List<string>();
        if (string.IsNullOrEmpty(CoreToken))
        {
            warnings.Add("Core token is not configured; Core will reject heartbeats.");
        }

        if (CoreUrl.Scheme == Uri.UriSchemeHttp &&
            !string.IsNullOrEmpty(CoreToken) &&
            !IsLoopback(CoreUrl.Host))
        {
            warnings.Add("Core token will be sent over plaintext HTTP.");
        }

        return warnings;
    }

    internal static TimeSpan ParseDuration(string name, string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            throw new SynapsysConfigurationException(
                $"{name} must include a duration and unit, for example '5s'.");
        }

        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            throw new SynapsysConfigurationException(
                $"{name} must include a unit, for example '5s'; unitless strings are not accepted.");
        }

        var suffixes = new (string Suffix, double Milliseconds)[]
        {
            ("ms", 1),
            ("s", 1_000),
            ("m", 60_000),
            ("h", 3_600_000),
        };

        foreach (var (suffix, milliseconds) in suffixes)
        {
            if (!trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var number = trimmed[..^suffix.Length];
            if (!double.TryParse(
                    number,
                    NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var amount) ||
                !double.IsFinite(amount))
            {
                break;
            }

            try
            {
                return TimeSpan.FromMilliseconds(checked(amount * milliseconds));
            }
            catch (OverflowException exception)
            {
                throw new SynapsysConfigurationException(
                    $"{name} is outside the supported duration range: '{value}'.",
                    exception);
            }
        }

        if (TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out var standard))
        {
            return standard;
        }

        throw new SynapsysConfigurationException(
            $"{name} is not a valid duration: '{value}'. Use forms such as '250ms', '5s', '2m', or '00:00:05'.");
    }

    private static string? ReadString(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value is null ? null : value.Trim();
    }

    private static Uri? ReadUri(string name)
    {
        var value = ReadString(name);
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var result))
        {
            throw new SynapsysConfigurationException(
                $"{name} is not a valid absolute URL: '{value}'.");
        }

        return result;
    }

    private static bool ReadBoolean(string name, bool defaultValue)
    {
        var value = ReadString(name);
        if (value is null)
        {
            return defaultValue;
        }

        if (!bool.TryParse(value, out var result))
        {
            throw new SynapsysConfigurationException(
                $"{name} must be 'true' or 'false', but was '{value}'.");
        }

        return result;
    }

    private static TimeSpan ReadDuration(string name, TimeSpan defaultValue)
    {
        var value = ReadString(name);
        return value is null ? defaultValue : ParseDuration(name, value);
    }

    private static string? DefaultWorkerName() =>
        Assembly.GetEntryAssembly()?.GetName().Name;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Host-name discovery has an intentional null fallback.")]
    private static string? DefaultHost()
    {
        try
        {
            return Dns.GetHostName();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    private static void ValidatePositiveDuration(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new SynapsysConfigurationException($"{name} must be positive.");
        }
    }

    private static void ValidateNonNegativeDuration(TimeSpan value, string name)
    {
        if (value < TimeSpan.Zero)
        {
            throw new SynapsysConfigurationException($"{name} must not be negative.");
        }
    }
}

