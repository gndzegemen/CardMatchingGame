using System;
using System.Collections.Generic;
using System.Net;
using TMPro;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.UI;

// Two self-hosted modes:
// - Desktop: direct WebSockets (Unity Transport); a player or a dedicated server hosts by IP:port.
// - Browser: room codes like the table tennis game; the hosting browser is the server and the
//   other browser connects to it over WebRTC (PeerJS only brokers the handshake).
public class MultiplayerMenu : MonoBehaviour
{
    [SerializeField] MemoryGame memoryGame;
    [SerializeField] NetworkManager networkManager;
    [SerializeField] NetworkObject sessionPrefab;

    [Header("UI")]
    [SerializeField] Button singlePlayerButton;
    [SerializeField] Button hostButton;
    [SerializeField] Button joinButton;
    [SerializeField] TMP_Text joinButtonLabel;
    [SerializeField] TMP_InputField addressInput;
    [SerializeField] TMP_InputField portInput;
    [SerializeField] TMP_Text messageText;

    [Header("Self-hosted connection")]
    [SerializeField] ushort port = 7777;
    [SerializeField] string defaultAddress = "127.0.0.1";
    [SerializeField] string listenAddress = "0.0.0.0";

    const string RoomAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    const int RoomCodeLength = 5;

    readonly HashSet<ulong> approvedClients = new HashSet<ulong>();
    string joinLabel;
    bool leaving;
    bool connected;
    bool starting;
    bool wasBusy;
    bool failureReported;
    string disconnectReason;
    UnityTransport unityTransport;
    WebRtcTransport webRtcTransport;
    string hostedRoom;

    static bool IsBrowser => Application.platform == RuntimePlatform.WebGLPlayer;

    // Include the asynchronous shutdown window: a new mode must wait until it closes.
    public bool IsSessionRunning => networkManager != null &&
        (starting || networkManager.IsListening || networkManager.ShutdownInProgress);

    void Start()
    {
        if (networkManager == null || memoryGame == null || sessionPrefab == null)
        {
            Debug.LogError("MultiplayerMenu: NetworkManager, MemoryGame or session prefab is missing.");
            enabled = false;
            return;
        }
        Application.runInBackground = true;
        networkManager.NetworkConfig.ConnectionApproval = true;
        networkManager.ConnectionApprovalCallback = ApproveConnection;
        networkManager.OnServerStarted += OnServerStarted;
        networkManager.OnServerStopped += OnServerStopped;
        networkManager.OnClientConnectedCallback += OnClientConnected;
        networkManager.OnClientDisconnectCallback += OnClientDisconnected;
        networkManager.OnClientStopped += OnClientStopped;
        networkManager.OnTransportFailure += OnTransportFailure;

        unityTransport = networkManager.GetComponent<UnityTransport>();
        webRtcTransport = networkManager.GetComponent<WebRtcTransport>();
        if (webRtcTransport == null) webRtcTransport = networkManager.gameObject.AddComponent<WebRtcTransport>();
        webRtcTransport.RoomOpened += OnRoomOpened;

        joinLabel = joinButtonLabel != null ? joinButtonLabel.text : "JOIN GAME";
        if (hostButton != null)
        {
            hostButton.onClick.AddListener(Host);
            if (IsBrowser) SetLabel(hostButton, "CREATE ROOM");
        }
        if (IsBrowser)
        {
            if (portInput != null) portInput.gameObject.SetActive(false);
            SetPlaceholder(addressInput, "Room code or wss://server:port");
        }
        if (joinButton != null) joinButton.onClick.AddListener(OnJoinButton);
        if (singlePlayerButton != null)
        {
            singlePlayerButton.onClick.AddListener(memoryGame.StartSinglePlayerTimed);
            var label = singlePlayerButton.GetComponentInChildren<TMP_Text>();
            if (label != null) label.text = "TIMED SOLO - " + Mathf.CeilToInt(Mathf.Max(1f, memoryGame.singlePlayerDuration)) + "s";
        }
        if (addressInput != null) addressInput.text = DefaultAddress();
        if (portInput != null) portInput.text = port.ToString();
        ShowMessage(null);
        RefreshButtons();
        StartFromCommandLine();
        if (IsBrowser && addressInput != null && !string.IsNullOrEmpty(addressInput.text))
            ShowMessage("Invited to room " + addressInput.text + ". Press JOIN GAME.");
    }

    void Update()
    {
        if (wasBusy == IsSessionRunning) return;
        if (!IsSessionRunning) leaving = false;
        RefreshButtons();
    }

    void OnDestroy()
    {
        if (hostButton != null) hostButton.onClick.RemoveListener(Host);
        if (joinButton != null) joinButton.onClick.RemoveListener(OnJoinButton);
        if (singlePlayerButton != null && memoryGame != null)
            singlePlayerButton.onClick.RemoveListener(memoryGame.StartSinglePlayerTimed);
        if (webRtcTransport != null) webRtcTransport.RoomOpened -= OnRoomOpened;
        if (networkManager == null) return;
        networkManager.ConnectionApprovalCallback -= ApproveConnection;
        networkManager.OnServerStarted -= OnServerStarted;
        networkManager.OnServerStopped -= OnServerStopped;
        networkManager.OnClientConnectedCallback -= OnClientConnected;
        networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        networkManager.OnClientStopped -= OnClientStopped;
        networkManager.OnTransportFailure -= OnTransportFailure;
    }

    // Browsers start empty unless the link carries ?room=CODE; desktop defaults to localhost.
    string DefaultAddress()
    {
        if (IsBrowser) return RoomFromUrl() ?? "";
        return defaultAddress;
    }

    static string RoomFromUrl()
    {
        if (!Uri.TryCreate(Application.absoluteURL, UriKind.Absolute, out Uri pageUri)) return null;
        foreach (string part in pageUri.Query.TrimStart('?').Split('&'))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part.Substring(0, eq).Equals("room", StringComparison.OrdinalIgnoreCase))
            {
                string code = NormalizeRoomCode(Uri.UnescapeDataString(part.Substring(eq + 1)));
                if (code != null) return code;
            }
        }
        return null;
    }

    // Room codes are short alphanumerics; anything with dots, colons or a scheme is a server address.
    public static string NormalizeRoomCode(string input)
    {
        string code = (input ?? "").Trim().ToUpperInvariant();
        if (code.Length < 4 || code.Length > 8) return null;
        foreach (char c in code)
            if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))) return null;
        return code;
    }

    static string NewRoomCode()
    {
        var random = new System.Random();
        var chars = new char[RoomCodeLength];
        for (int i = 0; i < chars.Length; i++) chars[i] = RoomAlphabet[random.Next(RoomAlphabet.Length)];
        return new string(chars);
    }

    UnityTransport Transport()
    {
        if (unityTransport == null)
        {
            ShowMessage("Unity Transport is missing from NetworkManager.");
            return null;
        }
        unityTransport.UseWebSockets = true;
        networkManager.NetworkConfig.NetworkTransport = unityTransport;
        return unityTransport;
    }

    void UseRoomTransport(string code)
    {
        webRtcTransport.RoomCode = code;
        networkManager.NetworkConfig.NetworkTransport = webRtcTransport;
    }

    bool ReadPort(out ushort selectedPort)
    {
        string value = portInput != null ? portInput.text : port.ToString();
        if (ushort.TryParse(value, out selectedPort) && selectedPort > 0) return true;
        ShowMessage("Enter a port between 1 and 65535.");
        return false;
    }

    public void Host() => StartServer(false);
    public void StartDedicatedServer() => StartServer(true);

    void StartServer(bool dedicated)
    {
        if (IsSessionRunning) return;
        if (IsBrowser)
        {
            if (!dedicated) HostRoom();
            return;
        }
        if (!ReadPort(out ushort selectedPort)) return;
        if (!IPAddress.TryParse(listenAddress, out IPAddress bindAddress))
        {
            ShowMessage("The listen address must be an IP address, such as 0.0.0.0.");
            return;
        }
        var transport = Transport();
        if (transport == null) return;
        PrepareStart();
        approvedClients.Clear();
        transport.UseEncryption = false; // TLS can terminate at a self-hosted reverse proxy.
        string loopback = bindAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "::1" : "127.0.0.1";
        transport.SetConnectionData(true, loopback, selectedPort, listenAddress);
        bool started = dedicated ? networkManager.StartServer() : networkManager.StartHost();
        starting = false;
        if (!started)
            ShowMessage("Could not start the server. Check whether TCP port " + selectedPort + " is already in use.");
        else
        {
            string message = (dedicated ? "Dedicated server" : "Hosting") + " on TCP port " + selectedPort +
                (dedicated ? ". Waiting for two players..." : ". Waiting for an opponent...") + "\n" + LocalAddresses(selectedPort);
            ShowMessage(message);
            Debug.Log("[SelfHosted] " + message);
        }
        RefreshButtons();
    }

    void HostRoom()
    {
        string code = NewRoomCode();
        UseRoomTransport(code);
        PrepareStart();
        approvedClients.Clear();
        hostedRoom = code;
        bool started = networkManager.StartHost();
        starting = false;
        if (!started)
        {
            hostedRoom = null;
            ShowMessage("Could not create a room.");
        }
        else ShowMessage("Creating room " + code + "...");
        RefreshButtons();
    }

    void OnRoomOpened()
    {
        if (hostedRoom == null) return;
        if (addressInput != null) addressInput.text = hostedRoom;
        WebRtcTransport.CopyToClipboard(hostedRoom);
        ShowMessage("Room code: " + hostedRoom + "  (copied)\nShare it with your opponent. Waiting...");
    }

    void PrepareStart()
    {
        hostedRoom = null;
        leaving = false;
        connected = false;
        failureReported = false;
        disconnectReason = null;
        starting = true;
        memoryGame.ShowStartPage();
    }

    void OnJoinButton()
    {
        if (IsSessionRunning) Leave();
        else Join(addressInput != null ? addressInput.text : defaultAddress);
    }

    public void Join(string input)
    {
        if (IsSessionRunning) return;
        string room = NormalizeRoomCode(input);
        if (IsBrowser && room != null)
        {
            UseRoomTransport(room);
            PrepareStart();
            bool joined = networkManager.StartClient();
            starting = false;
            ShowMessage(joined ? "Joining room " + room + "..." : "Could not start the connection.");
            RefreshButtons();
            return;
        }
        if (!ReadPort(out ushort selectedPort)) return;
        if (!SelfHostedEndpoint.TryParse(input, selectedPort, out SelfHostedEndpoint endpoint))
        {
            ShowMessage(IsBrowser ? "Enter a room code (e.g. K7P2Q) or a server address (wss://game.example.com:443)."
                : "Enter a host address: 192.168.1.20:7777 or wss://game.example.com:443");
            return;
        }
        if (Application.platform == RuntimePlatform.WebGLPlayer && !endpoint.Secure
            && Application.absoluteURL.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
        {
            ShowMessage("This HTTPS page needs a secure server address (wss://). Configure TLS on your server's reverse proxy.");
            return;
        }
        var transport = Transport();
        if (transport == null) return;
        PrepareStart();
        transport.UseEncryption = endpoint.Secure;
        if (endpoint.Secure) transport.SetClientSecrets(endpoint.Host);
        transport.SetConnectionData(true, endpoint.Host, endpoint.Port);
        bool started = networkManager.StartClient();
        starting = false;
        ShowMessage(started ? "Connecting to " + endpoint + "..." : "Could not start the connection.");
        RefreshButtons();
    }

    public void Leave()
    {
        leaving = true;
        if (networkManager != null && networkManager.IsListening && !networkManager.ShutdownInProgress)
            networkManager.Shutdown();
        memoryGame.ShowStartPage();
        ShowMessage(null);
        RefreshButtons();
    }

    public void ShowMessage(string text)
    {
        if (messageText == null) return;
        messageText.text = text ?? "";
        messageText.gameObject.SetActive(!string.IsNullOrEmpty(text));
    }

    void RefreshButtons()
    {
        bool busy = IsSessionRunning;
        wasBusy = busy;
        if (hostButton != null) hostButton.interactable = !busy;
        if (singlePlayerButton != null) singlePlayerButton.interactable = !busy;
        if (addressInput != null) addressInput.interactable = !busy;
        if (portInput != null) portInput.interactable = !busy;
        if (joinButton != null) joinButton.interactable = networkManager != null && !networkManager.ShutdownInProgress;
        if (joinButtonLabel != null) joinButtonLabel.text = busy ? "CANCEL" : joinLabel;
    }

    void ApproveConnection(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        // Reserve approved slots too, so simultaneous joins cannot admit a third player.
        // A dedicated server occupies no player slot.
        bool full = !approvedClients.Contains(request.ClientNetworkId) && approvedClients.Count >= NetworkMemorySession.MaxPlayers;
        response.Approved = !full;
        response.Reason = full ? "The game is full (2 players)." : "";
        response.CreatePlayerObject = false;
        response.Pending = false;
        if (!full) approvedClients.Add(request.ClientNetworkId);
    }

    void OnServerStarted()
    {
        NetworkObject session = Instantiate(sessionPrefab);
        session.Spawn();
    }

    void OnClientConnected(ulong clientId)
    {
        if (networkManager.IsServer)
            Debug.Log("[SelfHosted] Player connected. Players: " + networkManager.ConnectedClientsIds.Count);
        else if (clientId == networkManager.LocalClientId)
        {
            connected = true;
            ShowMessage("Connected. Waiting for the other player...");
            Debug.Log("[SelfHosted] Connected as client " + clientId);
        }
    }

    void OnClientDisconnected(ulong clientId)
    {
        approvedClients.Remove(clientId);
        if (networkManager.IsServer) return;
        if (!string.IsNullOrEmpty(networkManager.DisconnectReason))
            disconnectReason = networkManager.DisconnectReason;
        else if (!connected && networkManager.NetworkConfig.NetworkTransport == webRtcTransport
            && !string.IsNullOrEmpty(webRtcTransport.LastError))
            disconnectReason = RoomError(webRtcTransport.LastError);
    }

    void OnTransportFailure()
    {
        disconnectReason = networkManager.NetworkConfig.NetworkTransport == webRtcTransport
            ? RoomError(webRtcTransport.LastError)
            : "The transport stopped. Check the address, TCP port and server availability.";
    }

    static string RoomError(string error)
    {
        switch (error)
        {
            case "unavailable-id": return "That room code is already in use. Try again.";
            case "peer-unavailable": return "Room not found. Check the code.";
            case "timeout": return "The room did not answer in time.";
            case "network":
            case "server-error":
            case "socket-error": return "Could not reach the matchmaking service. Check your internet connection.";
            default: return string.IsNullOrEmpty(error) ? "The connection stopped." : "Connection error (" + error + ").";
        }
    }

    void OnClientStopped(bool wasHost) => HandleStopped();
    void OnServerStopped(bool wasHost) => HandleStopped();

    void HandleStopped()
    {
        if (!leaving && !failureReported)
        {
            failureReported = true;
            memoryGame.ShowStartPage();
            ShowMessage(!string.IsNullOrEmpty(disconnectReason) ? "Disconnected: " + disconnectReason :
                connected ? "Connection to the server was lost." : "Connection stopped. Check the address and that the server is running.");
        }
        approvedClients.Clear();
        hostedRoom = null;
        RefreshButtons();
    }

    static void SetLabel(Button button, string text)
    {
        var label = button.GetComponentInChildren<TMP_Text>();
        if (label != null) label.text = text;
    }

    static void SetPlaceholder(TMP_InputField input, string text)
    {
        if (input != null && input.placeholder is TMP_Text placeholder) placeholder.text = text;
    }

    void StartFromCommandLine()
    {
#if !UNITY_WEBGL && !UNITY_EDITOR
        string[] args = Environment.GetCommandLineArgs();
        bool server = Array.IndexOf(args, "-server") >= 0;
        bool host = Array.IndexOf(args, "-host") >= 0;
#if UNITY_SERVER
        server = true;
#endif
        string connect = null;
        for (int i = 1; i < args.Length; i++)
        {
            string argument = args[i];
            if (argument != "-port" && argument != "-listen" && argument != "-connect") continue;
            if (++i >= args.Length)
            {
                Debug.LogError("Missing value for " + argument);
                return;
            }
            if (argument == "-port")
            {
                if (!ushort.TryParse(args[i], out ushort selectedPort) || selectedPort == 0)
                {
                    Debug.LogError("Invalid -port. Use a number between 1 and 65535.");
                    return;
                }
                port = selectedPort;
                if (portInput != null) portInput.text = port.ToString();
            }
            else if (argument == "-listen") listenAddress = args[i];
            else connect = args[i];
        }
        if ((server ? 1 : 0) + (host ? 1 : 0) + (connect != null ? 1 : 0) > 1)
        {
            Debug.LogError("Choose only one of -server, -host or -connect.");
            return;
        }
        if (server) StartDedicatedServer();
        else if (host) Host();
        else if (connect != null)
        {
            if (addressInput != null) addressInput.text = connect;
            Join(connect);
        }
#endif
    }

    static string LocalAddresses(ushort selectedPort)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        return "";
#else
        var addresses = new List<string>();
        try
        {
            foreach (var ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    addresses.Add(ip + ":" + selectedPort);
        }
        catch (Exception e)
        {
            Debug.LogWarning("Could not read local addresses: " + e.Message);
        }
        return addresses.Count > 0 ? "LAN: " + string.Join(", ", addresses) : "Local: 127.0.0.1:" + selectedPort;
#endif
    }
}
