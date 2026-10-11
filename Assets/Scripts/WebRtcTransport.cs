using System;
using System.Runtime.InteropServices;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Serverless NGO transport for WebGL builds: the host's browser registers a short room code with the public
/// PeerJS broker, the joining browser looks it up, and the two then talk over direct WebRTC data channels.
/// No game server is involved. Only available in WebGL players (see <see cref="IsSupported"/>).
/// </summary>
[AddComponentMenu("Netcode/WebRTC Transport (PeerJS)")]
public class WebRtcTransport : NetworkTransport
{
    const int EventConnect = 1;
    const int EventData = 2;
    const int EventDisconnect = 3;
    const int EventHostReady = 4;
    const int EventError = 5;

    /// <summary>Prefix that keeps this game's room codes apart from other apps on the shared PeerJS broker.</summary>
    [SerializeField] string m_PeerIdPrefix = "cardmatching-webgl-v1-";

    [Tooltip("PeerJS script, relative to StreamingAssets (shipped with the build so no CDN is needed).")]
    [SerializeField] string m_LibraryFile = "peerjs.min.js";

    /// <summary>Room code to host or join; set before starting NGO.</summary>
    public string RoomCode { get; set; }

    /// <summary>True once the host's room code is registered and joinable.</summary>
    public bool RoomReady { get; private set; }

    /// <summary>Short machine-readable reason of the last failure (PeerJS error type), or empty.</summary>
    public string LastError { get; private set; } = string.Empty;

    public event Action RoomOpened;

    public override ulong ServerClientId => 0;

    public override bool IsSupported => Application.platform == RuntimePlatform.WebGLPlayer;

    bool m_IsHost;
    readonly int[] m_Info = new int[2];
    byte[] m_Buffer = new byte[64 * 1024];

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] static extern void WebRtc_StartHost(string peerId, string libraryUrl);
    [DllImport("__Internal")] static extern void WebRtc_StartClient(string hostPeerId, string libraryUrl);
    [DllImport("__Internal")] static extern void WebRtc_Send(int connectionId, byte[] buffer, int offset, int length, int reliable);
    [DllImport("__Internal")] static extern int WebRtc_Poll(int[] info);
    [DllImport("__Internal")] static extern int WebRtc_ReadData(byte[] buffer, int maxLength);
    [DllImport("__Internal")] static extern void WebRtc_Disconnect(int connectionId);
    [DllImport("__Internal")] static extern void WebRtc_Shutdown();
    [DllImport("__Internal")] static extern int WebRtc_GetRtt(int connectionId);
    [DllImport("__Internal")] static extern string WebRtc_GetLastError();
    [DllImport("__Internal")] static extern void WebRtc_CopyToClipboard(string text);
#else
    static void WebRtc_StartHost(string peerId, string libraryUrl) => NotAvailable();
    static void WebRtc_StartClient(string hostPeerId, string libraryUrl) => NotAvailable();
    static void WebRtc_Send(int connectionId, byte[] buffer, int offset, int length, int reliable) { }
    static int WebRtc_Poll(int[] info) => 0;
    static int WebRtc_ReadData(byte[] buffer, int maxLength) => 0;
    static void WebRtc_Disconnect(int connectionId) { }
    static void WebRtc_Shutdown() { }
    static int WebRtc_GetRtt(int connectionId) => 0;
    static string WebRtc_GetLastError() => "not-supported";
    static void WebRtc_CopyToClipboard(string text) => GUIUtility.systemCopyBuffer = text;
    static void NotAvailable() => Debug.LogError("WebRtcTransport only works in WebGL builds.");
#endif

    public static void CopyToClipboard(string text) => WebRtc_CopyToClipboard(text);

    string PeerId => m_PeerIdPrefix + (RoomCode ?? string.Empty).Trim().ToUpperInvariant();

    string LibraryUrl => Application.streamingAssetsPath + "/" + m_LibraryFile;

    public override void Initialize(NetworkManager networkManager = null)
    {
        RoomReady = false;
        LastError = string.Empty;
    }

    public override bool StartServer()
    {
        if (!IsSupported || string.IsNullOrWhiteSpace(RoomCode))
            return false;
        m_IsHost = true;
        WebRtc_StartHost(PeerId, LibraryUrl);
        return true;
    }

    public override bool StartClient()
    {
        if (!IsSupported || string.IsNullOrWhiteSpace(RoomCode))
            return false;
        m_IsHost = false;
        WebRtc_StartClient(PeerId, LibraryUrl);
        return true;
    }

    public override void Send(ulong clientId, ArraySegment<byte> payload, NetworkDelivery networkDelivery)
    {
        int connection = m_IsHost ? (int)clientId : 1;
        bool reliable = networkDelivery != NetworkDelivery.Unreliable && networkDelivery != NetworkDelivery.UnreliableSequenced;
        WebRtc_Send(connection, payload.Array, payload.Offset, payload.Count, reliable ? 1 : 0);
    }

    public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload, out float receiveTime)
    {
        receiveTime = Time.realtimeSinceStartup;
        payload = default;

        while (true)
        {
            int type = WebRtc_Poll(m_Info);
            int connection = m_Info[0];
            clientId = m_IsHost ? (ulong)connection : ServerClientId;

            switch (type)
            {
                case 0:
                    clientId = 0;
                    return NetworkEvent.Nothing;

                case EventConnect:
                    return NetworkEvent.Connect;

                case EventData:
                {
                    int length = m_Info[1];
                    if (m_Buffer.Length < length)
                        m_Buffer = new byte[Mathf.NextPowerOfTwo(length)];
                    int read = WebRtc_ReadData(m_Buffer, length);
                    payload = new ArraySegment<byte>(m_Buffer, 0, read);
                    return NetworkEvent.Data;
                }

                case EventDisconnect:
                    return NetworkEvent.Disconnect;

                case EventHostReady:
                    RoomReady = true;
                    RoomOpened?.Invoke();
                    continue;

                case EventError:
                    LastError = WebRtc_GetLastError();
                    if (m_IsHost)
                        return NetworkEvent.TransportFailure;
                    clientId = ServerClientId;
                    return NetworkEvent.Disconnect;

                default:
                    continue;
            }
        }
    }

    public override void DisconnectRemoteClient(ulong clientId)
    {
        WebRtc_Disconnect((int)clientId);
    }

    public override void DisconnectLocalClient()
    {
        WebRtc_Disconnect(1);
    }

    public override ulong GetCurrentRtt(ulong clientId)
    {
        int connection = m_IsHost ? (int)clientId : 1;
        return (ulong)Mathf.Max(0, WebRtc_GetRtt(connection));
    }

    public override void Shutdown()
    {
        WebRtc_Shutdown();
        RoomReady = false;
    }
}
