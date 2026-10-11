using System;
using System.Collections.Generic;
using System.Net;
using TMPro;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.UI;

// Main menu and lobby for two-player games. Two self-hosted ways to play:
// - Browser: like the table tennis game, CREATE ROOM turns the hosting browser into the server and
//   the friend opens the invite link (?room=CODE). Browsers talk over WebRTC; PeerJS only brokers the handshake.
// - Desktop: direct WebSockets (Unity Transport). A player or a dedicated server hosts on IP:port.
public class MultiplayerMenu : MonoBehaviour
{
    const string RoomAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    const int RoomCodeLength = 5;
    const string InviteQueryKey = "room";

    [SerializeField] MemoryGame memoryGame;
    [SerializeField] NetworkManager networkManager;
    [SerializeField] NetworkObject sessionPrefab;

    [Header("Main menu")]
    [SerializeField] Button singlePlayerButton;
    [SerializeField] TMP_Text singlePlayerInfo;
    [SerializeField] TMP_Text friendInfo;
    [SerializeField] Button hostButton;
    [SerializeField] TMP_Text joinHint;
    [SerializeField] Button joinButton;
    [SerializeField] TMP_InputField addressInput;
    [SerializeField] TMP_InputField portInput;
    [Tooltip("Port label and input. Hidden in browsers, which join rooms by code.")]
    [SerializeField] GameObject portRow;
    [SerializeField] TMP_Text messageText;
    [Tooltip("Background of the message, shown only while there is a message.")]
    [SerializeField] GameObject messageBox;
    [SerializeField] TMP_Text footerText;

    [Header("Lobby")]
    [SerializeField] TMP_Text lobbyTitle;
    [SerializeField] TMP_Text lobbyCodeLabel;
    [Tooltip("Background box of the room code, hidden when there is no code to show.")]
    [SerializeField] GameObject lobbyCodeBox;
    [SerializeField] TMP_Text lobbyCode;
    [SerializeField] TMP_Text lobbyLink;
    [SerializeField] TMP_Text lobbyHint;
    [SerializeField] TMP_Text lobbyStatus;
    [SerializeField] Button shareButton;
    [SerializeField] TMP_Text shareButtonLabel;
    [SerializeField] Button cancelButton;

    [Header("Self-hosted connection")]
    [SerializeField] ushort port = 7777;
    [Tooltip("Address filled in when testing in the editor.")]
    [SerializeField] string defaultAddress = "127.0.0.1";
    [SerializeField] string listenAddress = "0.0.0.0";

    readonly HashSet<ulong> approvedClients = new HashSet<ulong>();
    UnityTransport unityTransport;
    WebRtcTransport webRtcTransport;
    bool leaving;
    bool connected;
    bool starting;
    bool wasBusy;
    bool failureReported;
    string disconnectReason;

    // Lobby
    string roomCode;        // browser room being hosted or joined
    ushort hostedPort;      // desktop host port, 0 when not hosting directly
    string joinedServer;    // server address joined over WebSockets
    string shareText;       // what the share button sends: invite link, room code or address
    string statusText;
    string flashText;
    float flashUntil;
    bool animateStatus;

    static bool IsBrowser => Application.platform == RuntimePlatform.WebGLPlayer;

    // Include the asynchronous shutdown window: a new mode must wait until it closes.
    public bool IsSessionRunning => networkManager != null &&
        (starting || networkManager.IsListening || networkManager.ShutdownInProgress);

    bool UsingRooms => webRtcTransport != null && networkManager.NetworkConfig.NetworkTransport == webRtcTransport;

    bool LobbyVisible => memoryGame.LobbyPage != null && memoryGame.LobbyPage.activeInHierarchy;

    void Start()
    {
        if (networkManager == null || memoryGame == null || sessionPrefab == null)
        {
            Debug.LogError("MultiplayerMenu: NetworkManager, MemoryGame or session prefab is missing.");
            enabled = false;
            return;
        }
        Application.runInBackground = true;
        unityTransport = networkManager.GetComponent<UnityTransport>();
        webRtcTransport = networkManager.GetComponent<WebRtcTransport>();
        if (webRtcTransport == null) webRtcTransport = networkManager.gameObject.AddComponent<WebRtcTransport>();
        webRtcTransport.RoomOpened += OnRoomOpened;

        networkManager.NetworkConfig.ConnectionApproval = true;
        networkManager.ConnectionApprovalCallback = ApproveConnection;
        networkManager.OnServerStarted += OnServerStarted;
        networkManager.OnServerStopped += OnServerStopped;
        networkManager.OnClientConnectedCallback += OnClientConnected;
        networkManager.OnClientDisconnectCallback += OnClientDisconnected;
        networkManager.OnClientStopped += OnClientStopped;
        networkManager.OnTransportFailure += OnTransportFailure;

        if (singlePlayerButton != null) singlePlayerButton.onClick.AddListener(memoryGame.StartSinglePlayerTimed);
        if (hostButton != null) hostButton.onClick.AddListener(Host);
        if (joinButton != null) joinButton.onClick.AddListener(OnJoinButton);
        if (addressInput != null) addressInput.onSubmit.AddListener(Join);
        if (shareButton != null) shareButton.onClick.AddListener(ShareInvite);
        if (cancelButton != null) cancelButton.onClick.AddListener(Leave);

        int seconds = Mathf.CeilToInt(Mathf.Max(1f, memoryGame.singlePlayerDuration));
        SetText(singlePlayerInfo, $"Find all {memoryGame.ElementNumber / 2} pairs in {seconds} seconds");
        SetText(friendInfo, IsBrowser
            ? "Create a room and send your friend the invite link."
            : "Host a game and give your friend your address.");
        SetText(joinHint, IsBrowser ? "Got a room code? Join your friend:" : "Join a friend's game by IP address:");
        SetText(footerText, IsBrowser
            ? "Rooms connect your browsers directly. No sign-up needed."
            : "Desktop: friends on your network join with your IP address.");
        if (portRow != null) portRow.SetActive(!IsBrowser);
        if (portInput != null) portInput.text = port.ToString();
        if (addressInput != null)
        {
            if (addressInput.placeholder is TMP_Text placeholder)
                placeholder.text = IsBrowser ? "ROOM CODE" : "HOST IP ADDRESS";
            addressInput.text = Application.isEditor ? defaultAddress : "";
        }
        ShowMessage(null);
        RefreshButtons();
        StartFromCommandLine();

        string invite = IsBrowser ? RoomFromUrl() : null;
        if (invite != null && !IsSessionRunning && addressInput != null)
        {
            addressInput.text = invite;
            ShowMessage("You're invited to room " + invite + "!\nPress JOIN to play with your friend.");
        }

        // Development builds only: hooks for automated two-tab browser tests, as in the table tennis game.
        if (IsBrowser && Debug.isDebugBuild && !IsSessionRunning)
        {
            string autoHost = NormalizeRoomCode(QueryParameter("autohost"));
            if (autoHost != null) HostRoom(autoHost);
            else if (invite != null && QueryParameter("autojoin") == "1") JoinRoom(invite);
        }
    }

    void Update()
    {
        if (wasBusy != IsSessionRunning)
        {
            if (!IsSessionRunning) leaving = false;
            RefreshButtons();
        }
        if (LobbyVisible) UpdateLobbyStatus();
    }

    void OnDestroy()
    {
        if (singlePlayerButton != null && memoryGame != null)
            singlePlayerButton.onClick.RemoveListener(memoryGame.StartSinglePlayerTimed);
        if (hostButton != null) hostButton.onClick.RemoveListener(Host);
        if (joinButton != null) joinButton.onClick.RemoveListener(OnJoinButton);
        if (addressInput != null) addressInput.onSubmit.RemoveListener(Join);
        if (shareButton != null) shareButton.onClick.RemoveListener(ShareInvite);
        if (cancelButton != null) cancelButton.onClick.RemoveListener(Leave);
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

    // --- Room codes and invite links ---

    static string RoomFromUrl() => NormalizeRoomCode(QueryParameter(InviteQueryKey));

    static string QueryParameter(string key)
    {
        string url = Application.absoluteURL;
        int query = string.IsNullOrEmpty(url) ? -1 : url.IndexOf('?');
        if (query < 0) return null;
        foreach (string part in url.Substring(query + 1).Split('&', '#'))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part.Substring(0, eq).Equals(key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(part.Substring(eq + 1));
        }
        return null;
    }

    // Accepts a code ("k7p2q") or a pasted invite link ("https://.../?room=K7P2Q").
    public static string NormalizeRoomCode(string input)
    {
        string code = (input ?? "").Trim();
        int key = code.IndexOf(InviteQueryKey + "=", StringComparison.OrdinalIgnoreCase);
        if (key >= 0)
        {
            code = code.Substring(key + InviteQueryKey.Length + 1);
            int end = code.IndexOfAny(new[] { '&', '#' });
            if (end >= 0) code = code.Substring(0, end);
            code = Uri.UnescapeDataString(code).Trim();
        }
        code = code.ToUpperInvariant();
        if (code.Length != RoomCodeLength) return null;
        foreach (char c in code)
            if (RoomAlphabet.IndexOf(c) < 0) return null;
        return code;
    }

    static string NewRoomCode()
    {
        var random = new System.Random();
        var chars = new char[RoomCodeLength];
        for (int i = 0; i < chars.Length; i++) chars[i] = RoomAlphabet[random.Next(RoomAlphabet.Length)];
        return new string(chars);
    }

    static string InviteLink(string code)
    {
        string url = Application.absoluteURL ?? "";
        int cut = url.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) url = url.Substring(0, cut);
        return url.Length == 0 ? code : url + "?" + InviteQueryKey + "=" + code;
    }

    // --- Transports ---

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

    // --- Hosting ---

    public void Host() => StartServer(false);
    public void StartDedicatedServer() => StartServer(true);

    void StartServer(bool dedicated)
    {
        if (IsSessionRunning) return;
        if (IsBrowser)
        {
            if (!dedicated) HostRoom(NewRoomCode());
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
        transport.UseEncryption = false; // TLS can terminate at a self-hosted reverse proxy.
        string loopback = bindAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "::1" : "127.0.0.1";
        transport.SetConnectionData(true, loopback, selectedPort, listenAddress);
        bool started = dedicated ? networkManager.StartServer() : networkManager.StartHost();
        starting = false;
        if (!started)
        {
            memoryGame.ShowStartPage();
            ShowMessage("Could not start the game. Check whether TCP port " + selectedPort + " is already in use.");
        }
        else
        {
            var addresses = IPAddress.IsLoopback(bindAddress) ? new List<string>() : LanAddresses(selectedPort);
            Debug.Log("[SelfHosted] " + (dedicated ? "Dedicated server" : "Hosting") + " on TCP port " + selectedPort +
                (addresses.Count > 0 ? ". LAN: " + string.Join(", ", addresses) : ""));
            if (dedicated)
            {
                memoryGame.ShowStartPage();
                ShowMessage("Dedicated server on TCP port " + selectedPort + ". Waiting for two players...");
            }
            else
            {
                hostedPort = selectedPort;
                ShowHostLobby(null);
            }
        }
        RefreshButtons();
    }

    void HostRoom(string code)
    {
        PrepareStart();
        UseRoomTransport(code);
        roomCode = code;
        bool started = networkManager.StartHost();
        starting = false;
        if (!started)
        {
            roomCode = null;
            memoryGame.ShowStartPage();
            ShowMessage("Could not create a room. Please try again.");
        }
        else ShowHostLobby(null);
        RefreshButtons();
    }

    void OnRoomOpened()
    {
        if (roomCode == null || !networkManager.IsHost) return;
        Debug.Log("[SelfHosted] Room " + roomCode + " is open. Invite link: " + InviteLink(roomCode));
        // The broker can reopen the room after a reconnect; only the lobby reacts to it.
        if (LobbyVisible) ShowHostLobby(null);
    }

    // Lobby of the hosting player: room code and invite link, or the desktop address.
    void ShowHostLobby(string status, string hint = null)
    {
        if (roomCode != null)
        {
            if (!webRtcTransport.RoomReady)
            {
                ShowLobby("CREATING ROOM", "ROOM CODE", "-----", null,
                    "Setting up a private room for you and a friend.", null);
                SetLobbyStatus("Connecting...");
                return;
            }
            string link = InviteLink(roomCode);
            ShowLobby("ROOM READY", "ROOM CODE", roomCode, link != roomCode ? link : null,
                hint ?? "Send the invite link to a friend.\nThe game starts as soon as they join.",
                Application.isMobilePlatform ? "SHARE INVITE LINK" : "COPY INVITE LINK");
            shareText = link;
        }
        else
        {
            var addresses = LanAddresses(hostedPort);
            string address = addresses.Count > 0 ? addresses[0] : "127.0.0.1:" + hostedPort;
            ShowLobby("WAITING FOR A FRIEND", "YOUR ADDRESS", address,
                addresses.Count > 1 ? "Also: " + string.Join(", ", addresses.GetRange(1, addresses.Count - 1)) : null,
                hint ?? "Your friend types this address and presses JOIN.\nYou must be on the same network.",
                "COPY ADDRESS");
            shareText = address;
        }
        SetLobbyStatus(status ?? "Waiting for your friend...");
    }

    void ShareInvite()
    {
        if (string.IsNullOrEmpty(shareText)) return;
        string text = roomCode != null ? "Let's play Card Matching! Join my room " + roomCode + ":" : "Join my Card Matching game:";
        switch (InviteShare.Share(shareText, text))
        {
            case InviteShare.Result.Copied:
                FlashLobbyStatus(roomCode != null ? "Invite link copied! Paste it to your friend." : "Address copied!");
                break;
            case InviteShare.Result.Failed:
                FlashLobbyStatus("Could not copy. Tell your friend the code: " + (roomCode ?? shareText), 6f);
                break;
        }
    }

    // --- Joining ---

    void OnJoinButton() => Join(addressInput != null ? addressInput.text : defaultAddress);

    public void Join(string input)
    {
        if (IsSessionRunning) return;
        input = (input ?? "").Trim();
        string room = NormalizeRoomCode(input);
        if (IsBrowser && (room != null || !LooksLikeAddress(input)))
        {
            if (room == null)
            {
                ShowMessage(input.Length == 0 ? "Enter the room code your friend sent you."
                    : "Room codes have 5 letters or numbers, like K7P2Q.");
                return;
            }
            JoinRoom(room);
            return;
        }
        if (input.Length == 0)
        {
            ShowMessage("Enter the host's IP address, like 192.168.1.20");
            return;
        }
        if (!IsBrowser && room != null && !LooksLikeAddress(input))
        {
            ShowMessage("Room codes work in the browser version.\nOn desktop, enter the host's IP address.");
            return;
        }
        if (!ReadPort(out ushort selectedPort)) return;
        if (!SelfHostedEndpoint.TryParse(input, selectedPort, out SelfHostedEndpoint endpoint))
        {
            ShowMessage("Enter a host address: 192.168.1.20:7777 or wss://game.example.com:443");
            return;
        }
        if (IsBrowser && !endpoint.Secure && Application.absoluteURL.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
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
        if (!started)
        {
            memoryGame.ShowStartPage();
            ShowMessage("Could not start the connection.");
        }
        else
        {
            joinedServer = endpoint.Host.Contains(":") ? "[" + endpoint.Host + "]:" + endpoint.Port : endpoint.Host + ":" + endpoint.Port;
            ShowLobby("JOINING GAME", "SERVER", joinedServer, null, "Connecting to the game.", null);
            SetLobbyStatus("Connecting...");
        }
        RefreshButtons();
    }

    void JoinRoom(string room)
    {
        PrepareStart();
        UseRoomTransport(room);
        roomCode = room;
        bool started = networkManager.StartClient();
        starting = false;
        if (!started)
        {
            roomCode = null;
            memoryGame.ShowStartPage();
            ShowMessage("Could not start the connection.");
        }
        else
        {
            ShowLobby("JOINING ROOM", "ROOM CODE", room, null, "Connecting to your friend's room.", null);
            SetLobbyStatus("Connecting...");
        }
        RefreshButtons();
    }

    static bool LooksLikeAddress(string input) =>
        input.Contains("://") || input.Contains(".") || input.Contains(":") || input.StartsWith("[", StringComparison.Ordinal);

    // --- Session lifetime ---

    void PrepareStart()
    {
        leaving = false;
        connected = false;
        failureReported = false;
        disconnectReason = null;
        roomCode = null;
        hostedPort = 0;
        joinedServer = null;
        approvedClients.Clear();
        starting = true;
        ShowMessage(null);
    }

    public void Leave()
    {
        leaving = true;
        if (networkManager != null && networkManager.IsListening && !networkManager.ShutdownInProgress)
            networkManager.Shutdown();
        roomCode = null;
        hostedPort = 0;
        joinedServer = null;
        memoryGame.ShowStartPage();
        ShowMessage(null);
        RefreshButtons();
    }

    // Called on the remaining player when the other one leaves; the session stays open for a new opponent.
    public void OnOpponentLeft()
    {
        if (networkManager == null || !networkManager.IsListening) return;
        if (networkManager.IsHost)
        {
            ShowHostLobby("Waiting for a new player...", roomCode != null
                ? "Your friend left the game.\nShare the same link to play with someone else."
                : "Your friend left the game.\nAnother player can join with the same address.");
            return;
        }
        ShowLobby("WAITING FOR A PLAYER", joinedServer != null ? "SERVER" : null, joinedServer, null, "Your opponent left the game.", null);
        SetLobbyStatus("Waiting for a new opponent...");
    }

    void ApproveConnection(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        // Reserve approved slots too, so simultaneous joins cannot admit a third player.
        // A dedicated server occupies no player slot.
        bool full = !approvedClients.Contains(request.ClientNetworkId) && approvedClients.Count >= NetworkMemorySession.MaxPlayers;
        response.Approved = !full;
        response.Reason = full ? "This game is already full (2 players)." : "";
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
            if (LobbyVisible)
            {
                SetText(lobbyTitle, "WAITING FOR A PLAYER");
                SetLobbyStatus("Connected! Waiting for the other player...");
            }
            Debug.Log("[SelfHosted] Connected as client " + clientId);
        }
    }

    void OnClientDisconnected(ulong clientId)
    {
        approvedClients.Remove(clientId);
        if (networkManager.IsServer) return;
        if (!string.IsNullOrEmpty(networkManager.DisconnectReason))
            disconnectReason = networkManager.DisconnectReason;
        else if (!connected && UsingRooms && !string.IsNullOrEmpty(webRtcTransport.LastError))
            disconnectReason = RoomError(webRtcTransport.LastError);
    }

    void OnTransportFailure()
    {
        disconnectReason = UsingRooms
            ? RoomError(webRtcTransport.LastError)
            : "The connection stopped. Check the address, TCP port and server availability.";
    }

    static string RoomError(string error)
    {
        switch (error)
        {
            case "unavailable-id": return "That room code is already in use. Please try again.";
            case "peer-unavailable": return "Room not found. Check the code, or ask your friend for a new link.";
            case "timeout": return "The room did not answer in time. Please try again.";
            case "library-load-failed": return "Could not load the multiplayer library. Please reload the page.";
            case "network":
            case "server-error":
            case "socket-error":
            case "socket-closed": return "Could not reach the matchmaking service. Check your internet connection.";
            case "browser-incompatible": return "This browser does not support playing with a friend. Try Chrome, Firefox or Safari.";
            default: return string.IsNullOrEmpty(error) ? "The connection stopped." : "Connection error (" + error + ").";
        }
    }

    void OnClientStopped(bool wasHost) => HandleStopped(wasHost);
    void OnServerStopped(bool wasHost) => HandleStopped(true);

    void HandleStopped(bool wasServer)
    {
        if (!leaving && !failureReported)
        {
            failureReported = true;
            bool rooms = roomCode != null;
            string message;
            if (!string.IsNullOrEmpty(disconnectReason)) message = disconnectReason;
            else if (wasServer) message = "The game stopped unexpectedly.";
            else if (connected) message = rooms ? "Your friend closed the room." : "Connection to the server was lost.";
            else message = rooms ? "Could not join the room. Check the code and try again."
                : "Could not connect. Check the address and that the server is running.";
            memoryGame.ShowStartPage();
            ShowMessage(message);
        }
        approvedClients.Clear();
        roomCode = null;
        hostedPort = 0;
        joinedServer = null;
        RefreshButtons();
    }

    // --- UI ---

    public void ShowMessage(string text)
    {
        bool show = !string.IsNullOrEmpty(text);
        if (messageText != null)
        {
            messageText.text = text ?? "";
            messageText.gameObject.SetActive(show);
        }
        if (messageBox != null) messageBox.SetActive(show);
    }

    void RefreshButtons()
    {
        bool busy = IsSessionRunning;
        wasBusy = busy;
        if (singlePlayerButton != null) singlePlayerButton.interactable = !busy;
        if (hostButton != null) hostButton.interactable = !busy;
        if (joinButton != null) joinButton.interactable = !busy;
        if (addressInput != null) addressInput.interactable = !busy;
        if (portInput != null) portInput.interactable = !busy;
    }

    void ShowLobby(string title, string codeLabel, string code, string link, string hint, string shareLabel)
    {
        memoryGame.ShowLobbyPage();
        ShowMessage(null);
        SetText(lobbyTitle, title);
        SetText(lobbyCodeLabel, codeLabel);
        SetText(lobbyCode, code);
        if (lobbyCodeBox != null) lobbyCodeBox.SetActive(!string.IsNullOrEmpty(code));
        // Spaced letters read well for room codes; addresses need the room.
        if (lobbyCode != null) lobbyCode.characterSpacing = code != null && code.Length <= RoomCodeLength ? 20f : 0f;
        SetText(lobbyLink, link);
        SetText(lobbyHint, hint);
        shareText = null;
        if (shareButton != null) shareButton.gameObject.SetActive(shareLabel != null);
        if (shareLabel != null) SetText(shareButtonLabel, shareLabel);
    }

    // A status ending in "..." animates its dots while the lobby waits.
    void SetLobbyStatus(string text)
    {
        statusText = text ?? "";
        animateStatus = statusText.EndsWith("...", StringComparison.Ordinal);
        flashUntil = 0f;
        UpdateLobbyStatus();
    }

    void FlashLobbyStatus(string text, float seconds = 3f)
    {
        flashText = text;
        flashUntil = Time.unscaledTime + seconds;
        UpdateLobbyStatus();
    }

    void UpdateLobbyStatus()
    {
        if (lobbyStatus == null) return;
        string text = statusText ?? "";
        if (Time.unscaledTime < flashUntil) text = flashText;
        else if (animateStatus)
        {
            // Hidden dots keep the centered line from shifting.
            int dots = 1 + (int)(Time.unscaledTime * 2f) % 3;
            text = text.Substring(0, text.Length - 3) + new string('.', dots) + "<alpha=#00>" + new string('.', 3 - dots);
        }
        if (lobbyStatus.text != text) lobbyStatus.text = text;
    }

    static void SetText(TMP_Text label, string text)
    {
        if (label == null) return;
        label.text = text ?? "";
        label.gameObject.SetActive(!string.IsNullOrEmpty(text));
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

    // Private network addresses first, so the lobby shows the one a friend at home can reach.
    static List<string> LanAddresses(ushort selectedPort)
    {
        var addresses = new List<string>();
#if !UNITY_WEBGL || UNITY_EDITOR
        try
        {
            foreach (var ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    addresses.Add(ip.ToString());
        }
        catch (Exception e)
        {
            Debug.LogWarning("Could not read local addresses: " + e.Message);
        }
        addresses.Sort((a, b) => AddressRank(a).CompareTo(AddressRank(b)));
#endif
        return addresses.ConvertAll(ip => ip + ":" + selectedPort);
    }

    static int AddressRank(string ip) =>
        ip.StartsWith("192.168.", StringComparison.Ordinal) ? 0 : ip.StartsWith("10.", StringComparison.Ordinal) ? 1 : 2;
}
