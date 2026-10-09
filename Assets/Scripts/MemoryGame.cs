// MemoryGame.cs
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

    public GameObject ResetButton;

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

    // Oyun sadece iki oyunculu online oturumda oynanır
    private NetworkMemorySession session;

    public bool IsOnline => session != null;

    // Tüm kartların dağıtılması için geçen süre
    public float DealTotalDuration => ElementNumber / 2 * 2 * (dealDuration + dealStagger);

    void Awake()
    {
        SetStatus(null);
        SetResult(null);
    }

    // Yeniden başlatma isteği; tur iki oyuncu da isteyince başlar
    public void ResetGame()
    {
        if (IsOnline)
            session.RequestRestart();
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
                // Deste pozisyonundan kart pozisyonuna git
                PlayCardDealSound();

                dealCardTransform.position = deckTransform.position;
                dealCardTransform.DORotateQuaternion(cardRect.rotation, dealDuration);
                dealCardTransform.DOMove(cardRect.position, dealDuration)
                    .OnComplete(() => {
                        // Kart görünür hale getir
                        if (card != null && card.CurrentState == GridElement.State.Empty)
                            card.ChangeStatusAsBack();
                    });
            });

            dealSequence.AppendInterval(dealDuration + dealStagger);
        }

        dealSequence.OnComplete(() => {
            dealSequence = null;
            deckTransform.gameObject.SetActive(false);
            ResetButton.SetActive(true);
            boardReady = true;
            SetStatus(statusMessage);
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
        dealCardTransform.DOKill();
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
        if (boardReady && IsOnline && session.CanLocalPlayerReveal)
            session.RequestReveal(card.Index);
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
        bool allow = boardReady && IsOnline && session.CanLocalPlayerReveal;

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
    }

    // Kartlar dağıtılırken deste yazının üstüne geldiği için yazı dağıtım bitince görünür
    public void SetStatus(string text)
    {
        statusMessage = text;
        if (statusText == null) return;
        statusText.text = text ?? "";
        statusText.gameObject.SetActive(boardReady && !string.IsNullOrEmpty(text));
    }

    public void SetResult(string text)
    {
        if (resultText == null) return;
        resultText.text = text ?? "";
        resultText.gameObject.SetActive(!string.IsNullOrEmpty(text));
    }

    // --- Online mod ---

    public void AttachSession(NetworkMemorySession networkSession)
    {
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
        KillDealSequence();
        boardReady = false;
        foreach (Transform child in Parent)
            Destroy(child.gameObject);
        cards.Clear();
        deckTransform.gameObject.SetActive(false);
        SetStatus(null);
        SetResult(null);
        ShowPage(StartPage);
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
