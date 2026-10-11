using System;

// Shared by the menu and command-line WebSocket configuration.
public readonly struct SelfHostedEndpoint
{
    public string Host { get; }
    public ushort Port { get; }
    public bool Secure { get; }

    SelfHostedEndpoint(string host, ushort port, bool secure)
    {
        Host = host;
        Port = port;
        Secure = secure;
    }

    public override string ToString()
    {
        string host = Host.Contains(":") ? "[" + Host + "]" : Host;
        return $"{(Secure ? "wss" : "ws")}://{host}:{Port}";
    }

    public static bool TryParse(string input, ushort defaultPort, out SelfHostedEndpoint endpoint)
    {
        endpoint = default;
        string value = (input ?? "").Trim();
        if (value.Length == 0 || defaultPort == 0) return false;
        foreach (char c in value)
            if (char.IsWhiteSpace(c) || c == '\\') return false;

        bool hasScheme = value.Contains("://");
        if (!Uri.TryCreate(hasScheme ? value : "ws://" + value, UriKind.Absolute, out Uri uri)) return false;
        if ((uri.Scheme != "ws" && uri.Scheme != "wss") || string.IsNullOrEmpty(uri.Host)
            || uri.HostNameType == UriHostNameType.Unknown || !string.IsNullOrEmpty(uri.UserInfo)
            || (uri.AbsolutePath != "/" && uri.AbsolutePath != "")
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;

        string authority = hasScheme ? value.Substring(value.IndexOf("://", StringComparison.Ordinal) + 3) : value;
        authority = authority.TrimEnd('/');
        bool hasPort = authority.StartsWith("[", StringComparison.Ordinal)
            ? authority.IndexOf(']') + 1 < authority.Length
            : authority.Contains(":");
        if (hasPort && (!ushort.TryParse(authority.Substring(authority.LastIndexOf(':') + 1), out ushort suppliedPort)
            || suppliedPort == 0)) return false;
        int port = hasScheme || hasPort ? uri.Port : defaultPort;
        if (port < 1 || port > ushort.MaxValue) return false;
        endpoint = new SelfHostedEndpoint(uri.DnsSafeHost.Trim('[', ']'), (ushort)port, uri.Scheme == "wss");
        return true;
    }
}
