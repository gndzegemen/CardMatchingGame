// MultiplayerMenu.cs
using System;
using TMPro;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.UI;

// Başlangıç sayfasındaki online oyun butonları. Tarayıcı (WebGL) sunucu açamaz, sadece bağlanabilir;
// oyunu editör veya masaüstü build'i host eder. Bağlantılar WebSocket üzerinden yapılır.
public class MultiplayerMenu : MonoBehaviour
{
    [SerializeField] MemoryGame memoryGame;
    [SerializeField] NetworkManager networkManager;
    [SerializeField] NetworkObject sessionPrefab;

    [Header("UI")]
    [SerializeField] Button hostButton;
    [SerializeField] Button joinButton;
    [SerializeField] TMP_Text joinButtonLabel;
    [SerializeField] TMP_InputField addressInput;
    [SerializeField] TMP_Text messageText;

    [Header("Bağlantı")]
    [SerializeField] ushort port = 7777;
    [SerializeField] string defaultAddress = "127.0.0.1";

    string joinLabel;
    bool leaving;
    bool connected;
    string disconnectReason;

    public bool IsSessionRunning => networkManager != null && networkManager.IsListening && !networkManager.ShutdownInProgress;

    void Start()
    {
        if (networkManager == null || memoryGame == null || sessionPrefab == null)
        {
            Debug.LogError("MultiplayerMenu: NetworkManager, MemoryGame veya session prefab atanmamış.");
            enabled = false;
            return;
        }

        networkManager.ConnectionApprovalCallback = ApproveConnection;
        networkManager.OnServerStarted += OnServerStarted;
        networkManager.OnClientConnectedCallback += OnClientConnected;
        networkManager.OnClientDisconnectCallback += OnClientDisconnected;
        networkManager.OnClientStopped += OnClientStopped;

        if (joinButtonLabel != null)
            joinLabel = joinButtonLabel.text;

        // Tarayıcılar gelen bağlantı kabul edemez
        if (Application.platform == RuntimePlatform.WebGLPlayer)
            hostButton.gameObject.SetActive(false);

        addressInput.text = DefaultAddress();
        hostButton.onClick.AddListener(Host);
        joinButton.onClick.AddListener(OnJoinButton);
        ShowMessage(null);
        RefreshButtons();
    }

    void OnDestroy()
    {
        if (networkManager == null) return;
        networkManager.OnServerStarted -= OnServerStarted;
        networkManager.OnClientConnectedCallback -= OnClientConnected;
        networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        networkManager.OnClientStopped -= OnClientStopped;
    }

    // WebGL'de oyunun indirildiği sunucu varsayılan adres olur
    string DefaultAddress()
    {
        if (Application.platform == RuntimePlatform.WebGLPlayer
            && Uri.TryCreate(Application.absoluteURL, UriKind.Absolute, out Uri pageUri)
            && !string.IsNullOrEmpty(pageUri.Host))
        {
            return pageUri.Host;
        }
        return defaultAddress;
    }

    UnityTransport Transport()
    {
        var transport = networkManager.NetworkConfig.NetworkTransport as UnityTransport;
        if (transport == null)
            Debug.LogError("MultiplayerMenu: NetworkManager'da UnityTransport yok.");
        else
            transport.UseWebSockets = true;
        return transport;
    }

    void Host()
    {
        if (IsSessionRunning) return;
        var transport = Transport();
        if (transport == null) return;

        transport.SetConnectionData("127.0.0.1", port, "0.0.0.0");
        if (!networkManager.StartHost())
        {
            ShowMessage("Could not start the server. Is port " + port + " already in use?");
            return;
        }
        ShowMessage("Hosting on port " + port + ". Waiting for an opponent...\n" + LocalAddresses());
        RefreshButtons();
    }

    void OnJoinButton()
    {
        // Oturum açıkken buton iptal görevi görür
        if (IsSessionRunning)
        {
            Leave();
            return;
        }
        Join();
    }

    void Join()
    {
        var transport = Transport();
        if (transport == null) return;

        if (!TryParseAddress(addressInput.text, out string address, out ushort joinPort))
        {
            ShowMessage("Enter an address like 192.168.1.20 or 192.168.1.20:7777");
            return;
        }

        transport.SetConnectionData(address, joinPort);
        connected = false;
        disconnectReason = null;
        if (!networkManager.StartClient())
        {
            ShowMessage("Could not start the connection.");
            return;
        }
        ShowMessage("Connecting to " + address + ":" + joinPort + "...");
        RefreshButtons();
    }

    bool TryParseAddress(string input, out string address, out ushort parsedPort)
    {
        address = (input ?? "").Trim();
        parsedPort = port;

        int colon = address.LastIndexOf(':');
        if (colon > 0)
        {
            if (!ushort.TryParse(address.Substring(colon + 1), out parsedPort)) return false;
            address = address.Substring(0, colon);
        }
        return address.Length > 0;
    }

    public void Leave()
    {
        if (networkManager.IsListening)
        {
            leaving = true;
            networkManager.Shutdown();
        }
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
        bool running = IsSessionRunning;
        hostButton.interactable = !running;
        addressInput.interactable = !running;
        if (joinButtonLabel != null)
            joinButtonLabel.text = running ? "CANCEL" : joinLabel;
    }

    void ApproveConnection(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        bool full = networkManager.ConnectedClientsIds.Count >= NetworkMemorySession.MaxPlayers;
        response.Approved = !full;
        response.Reason = full ? "The game is full." : null;
        response.CreatePlayerObject = false;
        response.Pending = false;
    }

    void OnServerStarted()
    {
        NetworkObject session = Instantiate(sessionPrefab);
        session.Spawn();
    }

    void OnClientConnected(ulong clientId)
    {
        if (!networkManager.IsServer && clientId == networkManager.LocalClientId)
        {
            connected = true;
            ShowMessage("Connected. Waiting for the game to start...");
        }
    }

    void OnClientDisconnected(ulong clientId)
    {
        // Sunucuda diğer oyuncuların ayrılmasını NetworkMemorySession yönetir
        if (networkManager.IsServer) return;
        if (!string.IsNullOrEmpty(networkManager.DisconnectReason))
            disconnectReason = networkManager.DisconnectReason;
    }

    // Bağlantı bizim isteğimiz dışında kapandıysa menüye dön ve nedenini göster
    void OnClientStopped(bool wasHost)
    {
        if (!leaving && !wasHost)
        {
            memoryGame.ShowStartPage();
            if (!string.IsNullOrEmpty(disconnectReason))
                ShowMessage("Disconnected: " + disconnectReason);
            else if (connected)
                ShowMessage("Connection to the host was lost.");
            else
                ShowMessage("Could not connect. Check the address and that the host is running.");
        }
        leaving = false;
        RefreshButtons();
    }

    static string LocalAddresses()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        return "";
#else
        var addresses = new System.Collections.Generic.List<string>();
        try
        {
            foreach (var ip in System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList)
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    addresses.Add(ip.ToString());
        }
        catch (Exception e)
        {
            Debug.LogWarning("Yerel IP adresleri alınamadı: " + e.Message);
        }
        return addresses.Count > 0 ? "Your address: " + string.Join(", ", addresses) : "";
#endif
    }
}
