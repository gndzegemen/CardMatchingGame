// MemoryGame.cs
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;
using TMPro;
using UnityEngine.UI;

public class MemoryGame : MonoBehaviour
{
    [Header("Grid Ayarları")]
    public int ElementNumber = 18;
    public GameObject GridElementPrefab;
    public RectTransform Parent;

    [Header("Animasyon Ayarları")]
    [Tooltip("Deste nesnesinin RectTransform'u")]
    public RectTransform dealCardTransform;
    public RectTransform deckTransform;
    [Tooltip("Bir karta gitmesi gereken süre")]
    public float dealDuration = 0.5f;
    [Tooltip("Kartlar arası gecikme (s)")]
    public float dealStagger = 0.1f;

    [Header("Ses Ayarları")]
    [Tooltip("Kart dağıtma sesi")]
    public AudioSource audioSource;
    public AudioClip dealCardSound;
    public AudioClip matchSound;

    [Header("Kontrol Ayarları")]
    [Tooltip("Kartlar eşleşmezse kaç saniye sonra kapanacak?")]
    public float checkDelay = 1f;
    public float firstRevealTimeout = 3f;

    [Header("Sayfalar")]
    public GameObject StartPage;
    public GameObject MainPage;
    public GameObject FinishPage;
    [Tooltip("Oda kurulurken ve rakip beklenirken gösterilen sayfa")]
    public GameObject LobbyPage;

    public GameObject ResetButton;

    [Header("Singleplayer")]
    [Min(1f)] public float singlePlayerDuration = 90f;

    [Header("Multiplayer")]
    public MultiplayerMenu multiplayerMenu;
    [Tooltip("Oyun sırasında sıra ve skoru gösteren yazı")]
    public TMP_Text statusText;
    [Tooltip("Bitiş sayfasında sonucu gösteren yazı")]
    public TMP_Text resultText;

    private readonly List<GridElement> cards = new List<GridElement>();
    private Sequence dealSequence;
    private bool boardReady;
    private string statusMessage;

    private NetworkMemorySession session;
    private MemoryRules singlePlayerRules;
    private Coroutine singlePlayerCheck;
    private Coroutine singlePlayerTimeout;
    private bool singlePlayerActive;
    private bool singlePlayerResolving;
    private bool singlePlayerWon;
    private double singlePlayerDeadline;
    private float remainingSeconds;
    private int displayedSeconds = -1;

    public bool IsOnline => session != null;
    public bool IsSinglePlayer => singlePlayerRules != null;
    public float RemainingSeconds => remainingSeconds;

    // Tüm kartların dağıtılması için geçen süre
    public float DealTotalDuration => ElementNumber / 2 * 2 * (dealDuration + dealStagger);

    void Awake()
    {
        SetStatus(null);
        SetResult(null);
    }

    void Update()
    {
        UpdateSinglePlayerTimer();
    }

    public void StartSinglePlayerTimed()
    {
        if (multiplayerMenu != null && multiplayerMenu.IsSessionRunning) return;
        // Önceki bağlantı hatası menüye dönüldüğünde tekrar görünmesin
        if (multiplayerMenu != null) multiplayerMenu.ShowMessage(null);
        StopSinglePlayer();
        session = null;
        singlePlayerRules = new MemoryRules(1);
        singlePlayerRules.StartRound(MemoryRules.CreateLayout(ElementNumber, new System.Random()), 0);
        remainingSeconds = Mathf.Max(1f, singlePlayerDuration);
        singlePlayerActive = true;
        displayedSeconds = -1;
        SetResult(null);
        BuildBoard(singlePlayerRules.Layout);
    }

    void UpdateSinglePlayerTimer()
    {
        if (!singlePlayerActive || !boardReady || singlePlayerWon) return;
        remainingSeconds = Mathf.Max(0f, (float)(singlePlayerDeadline - Time.unscaledTimeAsDouble));
        UpdateSinglePlayerStatus();
        if (remainingSeconds <= 0f) FinishSinglePlayer(false);
    }

    void UpdateSinglePlayerStatus(bool force = false)
    {
        if (singlePlayerRules == null) return;
        int seconds = Mathf.CeilToInt(remainingSeconds);
        if (!force && seconds == displayedSeconds) return;
        displayedSeconds = seconds;
        SetStatus($"Time {seconds / 60:00}:{seconds % 60:00}  |  Pairs {singlePlayerRules.Scores[0]}/{singlePlayerRules.Layout.Length / 2}");
    }

    void RevealSinglePlayer(int index)
    {
        UpdateSinglePlayerTimer();
        if (!singlePlayerActive || singlePlayerResolving || !singlePlayerRules.CanReveal(0, index)) return;
        singlePlayerRules.Reveal(index);
        ShowFront(index);
        if (singlePlayerRules.RevealedCount == 1)
        {
            singlePlayerTimeout = StartCoroutine(HideSinglePlayerCard());
        }
        else
        {
            if (singlePlayerTimeout != null) StopCoroutine(singlePlayerTimeout);
            singlePlayerTimeout = null;
            // The second click decides the match. A final pair selected before zero wins,
            // even if its visual feedback finishes after the countdown.
            var result = singlePlayerRules.ResolvePair();
            singlePlayerWon = singlePlayerRules.IsFinished;
            singlePlayerResolving = true;
            singlePlayerCheck = StartCoroutine(ResolveSinglePlayerPair(result));
            UpdateSinglePlayerStatus(true);
        }
        RefreshInteractable();
    }

    IEnumerator HideSinglePlayerCard()
    {
        yield return new WaitForSecondsRealtime(firstRevealTimeout);
        singlePlayerTimeout = null;
        if (singlePlayerActive && singlePlayerRules.RevealedCount == 1)
            ShowBack(singlePlayerRules.HideSingle());
        RefreshInteractable();
    }

    IEnumerator ResolveSinglePlayerPair(MemoryRules.PairResult result)
    {
        yield return new WaitForSecondsRealtime(checkDelay);
        singlePlayerCheck = null;
        if (!singlePlayerActive) yield break;
        ApplyPairResult(result.First, result.Second, result.Matched);
        singlePlayerResolving = false;
        if (singlePlayerWon) FinishSinglePlayer(true);
        else RefreshInteractable();
    }

    void FinishSinglePlayer(bool won)
    {
        singlePlayerActive = false;
        StopSinglePlayerCoroutines();
        RefreshInteractable();
        SetStatus(null);
        int seconds = Mathf.CeilToInt(remainingSeconds);
        SetResult((won ? "You win!" : "Time's up!") +
            $"\nPairs {singlePlayerRules.Scores[0]}/{singlePlayerRules.Layout.Length / 2}" +
            (won ? $"  |  Time left {seconds / 60:00}:{seconds % 60:00}" : ""));
        ShowFinish();
    }

    void StopSinglePlayerCoroutines()
    {
        if (singlePlayerCheck != null) StopCoroutine(singlePlayerCheck);
        if (singlePlayerTimeout != null) StopCoroutine(singlePlayerTimeout);
        singlePlayerCheck = null;
        singlePlayerTimeout = null;
        singlePlayerResolving = false;
    }

    void StopSinglePlayer()
    {
        StopSinglePlayerCoroutines();
        singlePlayerRules = null;
        singlePlayerActive = false;
        singlePlayerWon = false;
    }

    // Yeniden başlatma isteği; tur iki oyuncu da isteyince başlar
    public void ResetGame()
    {
        if (IsOnline)
            session.RequestRestart();
        else if (IsSinglePlayer)
            StartSinglePlayerTimed();
    }

    void BuildBoard(int[] layout)
    {
        KillDealSequence();

        // Sahnedeki eski kartları temizle
        foreach (Transform child in Parent)
        {
            child.gameObject.SetActive(false);
            Destroy(child.gameObject);
        }
        cards.Clear();
        boardReady = false;

        ShowPage(MainPage);

        Sprite[] cardSprites = Resources.LoadAll<Sprite>("CardIcons");
        for (int i = 0; i < layout.Length; i++)
        {
            GameObject go = Instantiate(GridElementPrefab, Parent);
            var element = go.GetComponent<GridElement>();
            element.Index = i;
            element.PairId = layout[i];
            element.memoryGame = this;
            if (cardSprites.Length > 0)
                element.image.sprite = cardSprites[layout[i] % cardSprites.Length];
            element.ChangeStatusAsEmpty(); // Başlangıçta empty (görünmez)
            cards.Add(element);
        }

        // Dağıtma animasyonu kartların grid'deki konumlarını kullanıyor
        LayoutRebuilder.ForceRebuildLayoutImmediate(Parent);
        RefreshInteractable();
        DealAnimation();
    }

    void DealAnimation()
    {
        deckTransform.gameObject.SetActive(true);
        ResetButton.SetActive(false);

        dealSequence = DOTween.Sequence();

        for (int i = 0; i < cards.Count; i++)
        {
            GridElement card = cards[i];
            RectTransform cardRect = card.GetComponent<RectTransform>();

            dealSequence.AppendCallback(() => {
                PlayCardDealSound();
                dealCardTransform.position = deckTransform.position;
            });
            // Keep the card movement inside the sequence so its completion and cancellation
            // include every card, even when several animation steps fall within one frame.
            dealSequence.Append(dealCardTransform.DOMove(cardRect.position, dealDuration)
                .From(deckTransform.position)
                .OnComplete(() => {
                    if (card != null && card.CurrentState == GridElement.State.Empty)
                        card.ChangeStatusAsBack();
                }));
            dealSequence.Join(dealCardTransform.DORotateQuaternion(cardRect.rotation, dealDuration));
            dealSequence.AppendInterval(dealStagger);
        }

        dealSequence.OnComplete(() => {
            dealSequence = null;
            deckTransform.gameObject.SetActive(false);
            ResetButton.SetActive(true);
            boardReady = true;
            if (singlePlayerActive)
            {
                singlePlayerDeadline = Time.unscaledTimeAsDouble + remainingSeconds;
                UpdateSinglePlayerStatus(true);
            }
            else SetStatus(statusMessage);
            RefreshInteractable();
        });
    }

    void KillDealSequence()
    {
        if (dealSequence != null)
        {
            dealSequence.Kill();
            dealSequence = null;
        }
        if (dealCardTransform != null) dealCardTransform.DOKill();
    }

    void PlayCardDealSound()
    {
        if (audioSource != null)
        {
            audioSource.clip = dealCardSound;
            audioSource.Play();
        }
    }

    void PlayCardMatchSound()
    {
        if (audioSource != null)
        {
            audioSource.clip = matchSound;
            audioSource.Play();
        }
    }

    public void OnCardClicked(GridElement card)
    {
        if (!boardReady || card == null || !IsValidCard(card.Index) || cards[card.Index] != card) return;
        if (IsOnline && session.CanLocalPlayerReveal)
            session.RequestReveal(card.Index);
        else if (IsSinglePlayer)
            RevealSinglePlayer(card.Index);
    }

    // --- Görsel güncellemeler ---

    public void ShowFront(int index)
    {
        if (IsValidCard(index))
            cards[index].ChangeStatusAsFront();
    }

    public void ShowBack(int index)
    {
        if (IsValidCard(index))
            cards[index].ChangeStatusAsBack();
    }

    public void ApplyPairResult(int first, int second, bool matched)
    {
        if (!IsValidCard(first) || !IsValidCard(second)) return;

        if (matched)
        {
            // Eşleşme: boş slot yap
            cards[first].ChangeStatusAsEmpty();
            cards[second].ChangeStatusAsEmpty();
            PlayCardMatchSound();
        }
        else
        {
            // Eşleşmez: geri kapat
            cards[first].ChangeStatusAsBack();
            cards[second].ChangeStatusAsBack();
        }
    }

    public void ShowFinish()
    {
        ShowPage(FinishPage);
    }

    // Sadece kapalı kartlara ve sırası gelen oyuncuya izin ver
    public void RefreshInteractable()
    {
        bool allow = boardReady && (IsOnline ? session.CanLocalPlayerReveal :
            singlePlayerActive && !singlePlayerResolving && !singlePlayerWon);

        foreach (var card in cards)
            card.button.interactable = allow && card.CurrentState == GridElement.State.Back;
    }

    bool IsValidCard(int index)
    {
        return index >= 0 && index < cards.Count && cards[index] != null;
    }

    void ShowPage(GameObject page)
    {
        StartPage.SetActive(page == StartPage);
        MainPage.SetActive(page == MainPage);
        FinishPage.SetActive(page == FinishPage);
        if (LobbyPage != null) LobbyPage.SetActive(page == LobbyPage);
    }

    // Kartlar dağıtılırken deste yazının üstüne geldiği için yazı dağıtım bitince görünür
    public void SetStatus(string text)
    {
        statusMessage = text;
        if (statusText == null) return;
        statusText.text = text ?? "";
        statusText.gameObject.SetActive(boardReady && !string.IsNullOrEmpty(text));
    }

    // İlk satır başlık olarak büyük yazılır ("You win!"), kalanlar ayrıntıdır
    public void SetResult(string text)
    {
        if (resultText == null) return;
        if (string.IsNullOrEmpty(text))
            resultText.text = "";
        else
        {
            int lineEnd = text.IndexOf('\n');
            resultText.text = lineEnd < 0 ? "<size=150%><b>" + text + "</b></size>"
                : "<size=150%><b>" + text.Substring(0, lineEnd) + "</b></size>" + text.Substring(lineEnd);
        }
        resultText.gameObject.SetActive(!string.IsNullOrEmpty(text));
    }

    // --- Online mod ---

    public void AttachSession(NetworkMemorySession networkSession)
    {
        StopSinglePlayer();
        session = networkSession;
    }

    public void DetachSession(NetworkMemorySession networkSession)
    {
        if (session == networkSession)
            session = null;
    }

    public void BeginOnlineRound(int[] layout)
    {
        SetResult(null);
        BuildBoard(layout);
    }

    // Bağlantı koptuğunda veya oyundan çıkıldığında başlangıç sayfasına dön
    public void ShowStartPage()
    {
        ClearBoard();
        ShowPage(StartPage);
    }

    // Oda kodu ve rakip bekleme ekranı
    public void ShowLobbyPage()
    {
        ClearBoard();
        ShowPage(LobbyPage != null ? LobbyPage : StartPage);
    }

    void ClearBoard()
    {
        StopSinglePlayer();
        KillDealSequence();
        boardReady = false;
        foreach (Transform child in Parent)
            Destroy(child.gameObject);
        cards.Clear();
        deckTransform.gameObject.SetActive(false);
        SetStatus(null);
        SetResult(null);
    }

    void OnDestroy()
    {
        StopSinglePlayerCoroutines();
        KillDealSequence();
    }

    public void QuitGame()
    {
        // Online oyundan çık ve menüye dön
        if (multiplayerMenu != null && multiplayerMenu.IsSessionRunning)
        {
            multiplayerMenu.Leave();
            return;
        }
        ShowStartPage();
    }
}
