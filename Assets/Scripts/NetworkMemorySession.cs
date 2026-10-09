// NetworkMemorySession.cs
using System.Collections;
using Unity.Netcode;
using UnityEngine;

// İki oyunculu online oturum. Kurallar sadece sunucuda çalışır, istemciler sadece sonuçları gösterir.
public class NetworkMemorySession : NetworkBehaviour
{
    public const int MaxPlayers = 2;
    const ulong NoPlayer = ulong.MaxValue;

    // Slot 0: host, slot 1: ikinci oyuncu
    private readonly NetworkVariable<ulong> player0 = new NetworkVariable<ulong>(NoPlayer);
    private readonly NetworkVariable<ulong> player1 = new NetworkVariable<ulong>(NoPlayer);
    private readonly NetworkVariable<int> currentTurn = new NetworkVariable<int>(0);
    private readonly NetworkVariable<int> score0 = new NetworkVariable<int>(0);
    private readonly NetworkVariable<int> score1 = new NetworkVariable<int>(0);
    private readonly NetworkVariable<bool> roundActive = new NetworkVariable<bool>(false);
    private readonly NetworkVariable<bool> roundFinished = new NetworkVariable<bool>(false);
    // Yeni tur iki oyuncu da isteyince başlar
    private readonly NetworkVariable<bool> restartVote0 = new NetworkVariable<bool>(false);
    private readonly NetworkVariable<bool> restartVote1 = new NetworkVariable<bool>(false);

    private MemoryGame game;
    private int localRevealCount;

    // Sadece sunucuda
    private MemoryRules rules;
    private float inputUnlockTime;
    private int nextFirstPlayer;
    private Coroutine checkCoroutine;
    private Coroutine timeoutCoroutine;

    public int LocalSlot => SlotOf(NetworkManager.LocalClientId);
    public bool IsMyTurn => roundActive.Value && LocalSlot >= 0 && currentTurn.Value == LocalSlot;
    public bool CanLocalPlayerReveal => IsMyTurn && localRevealCount < 2;

    int SlotOf(ulong clientId)
    {
        if (clientId == player0.Value) return 0;
        if (clientId == player1.Value) return 1;
        return -1;
    }

    public override void OnNetworkSpawn()
    {
        game = FindAnyObjectByType<MemoryGame>();
        if (game == null)
        {
            Debug.LogError("NetworkMemorySession: sahnede MemoryGame bulunamadı.");
            return;
        }

        player0.OnValueChanged += OnSlotChanged;
        player1.OnValueChanged += OnSlotChanged;
        currentTurn.OnValueChanged += OnIntChanged;
        score0.OnValueChanged += OnIntChanged;
        score1.OnValueChanged += OnIntChanged;
        roundActive.OnValueChanged += OnBoolChanged;
        roundFinished.OnValueChanged += OnBoolChanged;
        restartVote0.OnValueChanged += OnBoolChanged;
        restartVote1.OnValueChanged += OnBoolChanged;

        if (IsServer)
        {
            NetworkManager.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
            foreach (ulong clientId in NetworkManager.ConnectedClientsIds)
                AssignSlot(clientId);
            TryStartRound();
        }

        game.AttachSession(this);
        RefreshStatus();
    }

    public override void OnNetworkDespawn()
    {
        player0.OnValueChanged -= OnSlotChanged;
        player1.OnValueChanged -= OnSlotChanged;
        currentTurn.OnValueChanged -= OnIntChanged;
        score0.OnValueChanged -= OnIntChanged;
        score1.OnValueChanged -= OnIntChanged;
        roundActive.OnValueChanged -= OnBoolChanged;
        roundFinished.OnValueChanged -= OnBoolChanged;
        restartVote0.OnValueChanged -= OnBoolChanged;
        restartVote1.OnValueChanged -= OnBoolChanged;

        if (IsServer && NetworkManager != null)
        {
            NetworkManager.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        }

        if (game != null)
            game.DetachSession(this);
    }

    void OnSlotChanged(ulong previous, ulong current) => RefreshStatus();
    void OnIntChanged(int previous, int current) => RefreshStatus();
    void OnBoolChanged(bool previous, bool current) => RefreshStatus();

    // --- Sunucu ---

    void AssignSlot(ulong clientId)
    {
        if (SlotOf(clientId) >= 0) return;
        if (player0.Value == NoPlayer) player0.Value = clientId;
        else if (player1.Value == NoPlayer) player1.Value = clientId;
    }

    void OnClientConnected(ulong clientId)
    {
        AssignSlot(clientId);
        TryStartRound();
    }

    void OnClientDisconnected(ulong clientId)
    {
        int slot = SlotOf(clientId);
        if (slot < 0) return;

        if (slot == 0) player0.Value = NoPlayer;
        else player1.Value = NoPlayer;

        // Rakip ayrıldı: kalan oyuncu yeni rakip beklesin
        StopServerCoroutines();
        rules = null;
        roundActive.Value = false;
        roundFinished.Value = false;
        restartVote0.Value = false;
        restartVote1.Value = false;
        score0.Value = 0;
        score1.Value = 0;
        RoundAbortedRpc();
    }

    void TryStartRound()
    {
        if (player0.Value != NoPlayer && player1.Value != NoPlayer && !roundActive.Value && !roundFinished.Value)
            StartRoundServer();
    }

    void StartRoundServer()
    {
        StopServerCoroutines();

        int firstPlayer = nextFirstPlayer;
        nextFirstPlayer = (nextFirstPlayer + 1) % MaxPlayers;

        rules = new MemoryRules(MaxPlayers);
        rules.StartRound(MemoryRules.CreateLayout(game.ElementNumber, new System.Random()), firstPlayer);

        score0.Value = 0;
        score1.Value = 0;
        currentTurn.Value = firstPlayer;
        restartVote0.Value = false;
        restartVote1.Value = false;
        roundFinished.Value = false;
        roundActive.Value = true;

        // Kartlar dağıtılırken seçim yapılamaz
        inputUnlockTime = Time.time + game.DealTotalDuration;
        StartRoundRpc(rules.Layout);
    }

    void StopServerCoroutines()
    {
        if (checkCoroutine != null)
        {
            StopCoroutine(checkCoroutine);
            checkCoroutine = null;
        }
        if (timeoutCoroutine != null)
        {
            StopCoroutine(timeoutCoroutine);
            timeoutCoroutine = null;
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RequestRevealRpc(int index, RpcParams rpcParams = default)
    {
        if (rules == null || !roundActive.Value || Time.time < inputUnlockTime) return;

        int slot = SlotOf(rpcParams.Receive.SenderClientId);
        if (!rules.CanReveal(slot, index)) return;

        rules.Reveal(index);
        RevealRpc(index);

        if (rules.RevealedCount == 1)
        {
            timeoutCoroutine = StartCoroutine(FirstRevealTimeout());
        }
        else
        {
            if (timeoutCoroutine != null)
            {
                StopCoroutine(timeoutCoroutine);
                timeoutCoroutine = null;
            }
            checkCoroutine = StartCoroutine(CheckMatch());
        }
    }

    IEnumerator FirstRevealTimeout()
    {
        yield return new WaitForSeconds(game.firstRevealTimeout);
        timeoutCoroutine = null;

        // Eğer hâlâ tek kart açıksa, kapat
        if (rules != null && rules.RevealedCount == 1)
            HideRpc(rules.HideSingle());
    }

    IEnumerator CheckMatch()
    {
        yield return new WaitForSeconds(game.checkDelay);
        checkCoroutine = null;

        var result = rules.ResolvePair();
        score0.Value = rules.Scores[0];
        score1.Value = rules.Scores[1];
        currentTurn.Value = rules.CurrentPlayer;
        PairResolvedRpc(result.First, result.Second, result.Matched);

        if (rules.IsFinished)
        {
            roundActive.Value = false;
            roundFinished.Value = true;
            GameOverRpc();
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RequestRestartRpc(RpcParams rpcParams = default)
    {
        int slot = SlotOf(rpcParams.Receive.SenderClientId);
        if (slot < 0 || player0.Value == NoPlayer || player1.Value == NoPlayer) return;

        if (slot == 0) restartVote0.Value = true;
        else restartVote1.Value = true;

        if (restartVote0.Value && restartVote1.Value)
            StartRoundServer();
    }

    // --- İstemciler (host dahil) ---

    public void RequestReveal(int index) => RequestRevealRpc(index);

    public void RequestRestart() => RequestRestartRpc();

    [Rpc(SendTo.ClientsAndHost)]
    void StartRoundRpc(int[] layout)
    {
        localRevealCount = 0;
        game.BeginOnlineRound(layout);
        RefreshStatus();
    }

    [Rpc(SendTo.ClientsAndHost)]
    void RevealRpc(int index)
    {
        localRevealCount++;
        game.ShowFront(index);
        game.RefreshInteractable();
    }

    [Rpc(SendTo.ClientsAndHost)]
    void HideRpc(int index)
    {
        localRevealCount = 0;
        game.ShowBack(index);
        game.RefreshInteractable();
    }

    [Rpc(SendTo.ClientsAndHost)]
    void PairResolvedRpc(int first, int second, bool matched)
    {
        localRevealCount = 0;
        game.ApplyPairResult(first, second, matched);
        game.RefreshInteractable();
        RefreshStatus();
    }

    [Rpc(SendTo.ClientsAndHost)]
    void GameOverRpc()
    {
        game.ShowFinish();
        RefreshStatus();
    }

    [Rpc(SendTo.ClientsAndHost)]
    void RoundAbortedRpc()
    {
        localRevealCount = 0;
        game.ShowStartPage();
        if (game.multiplayerMenu != null)
            game.multiplayerMenu.ShowMessage("Your opponent left. Waiting for a new opponent...");
    }

    void RefreshStatus()
    {
        if (game == null || !IsSpawned) return;

        int mySlot = LocalSlot;
        int myScore = mySlot == 1 ? score1.Value : score0.Value;
        int opponentScore = mySlot == 1 ? score0.Value : score1.Value;
        string score = $"You {myScore} - {opponentScore} Opponent";
        bool myVote = mySlot == 1 ? restartVote1.Value : restartVote0.Value;
        bool opponentVote = mySlot == 1 ? restartVote0.Value : restartVote1.Value;

        if (roundActive.Value)
        {
            string vote = myVote ? "\nWaiting for opponent to restart" : opponentVote ? "\nOpponent wants to restart" : "";
            game.SetStatus((IsMyTurn ? "Your turn" : "Opponent's turn") + "\n" + score + vote);
            game.SetResult(null);
        }
        else if (roundFinished.Value)
        {
            string result = myScore > opponentScore ? "You win!" : myScore < opponentScore ? "You lose!" : "Draw!";
            string vote = myVote ? "\nWaiting for opponent to play again" : opponentVote ? "\nOpponent wants a rematch" : "";
            game.SetStatus(null);
            game.SetResult(result + "\n" + score + vote);
        }
        else
        {
            game.SetStatus(null);
            game.SetResult(null);
        }

        game.RefreshInteractable();
    }
}
