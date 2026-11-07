using System.Globalization;

namespace CXAudioOutput;

public sealed record LaunchOptions(int Port, bool Demo)
{
    public const int DefaultPort = 5240;

    public static LaunchOptions Parse(string[] args)
    {
        var port = DefaultPort;
        var demo = false;

        var envPort = Environment.GetEnvironmentVariable("CXAUDIO_PORT");
        if (int.TryParse(envPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var envParsed)
            && envParsed is > 0 and < 65536)
        {
            port = envParsed;
        }

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--demo":
                    demo = true;
                    break;
                case "--no-browser":
                    // Kept for command-line compatibility. The native UI has an explicit browser button.
                    break;
                case "--port" when i + 1 < args.Length:
                    if (TryParsePort(args[++i], out var p)) port = p;
                    break;
                default:
                    if (arg.StartsWith("--port=", StringComparison.Ordinal))
                    {
                        if (TryParsePort(arg[7..], out var inlinePort)) port = inlinePort;
                    }
                    break;
            }
        }

        return new LaunchOptions(port, demo);
    }

    private static bool TryParsePort(string value, out int port) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
        && port is > 0 and < 65536;
}
