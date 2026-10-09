// MemoryRules.cs
using System.Collections.Generic;

// Oyunun kuralları. Tek oyunculu modda yerelde, online modda sadece sunucuda çalışır.
public class MemoryRules
{
    public enum CardState { Hidden, Revealed, Matched }

    public struct PairResult
    {
        public int First;
        public int Second;
        public bool Matched;
    }

    private readonly List<int> revealed = new List<int>(2);
    private int matchedCards;

    public int[] Layout { get; private set; }
    public CardState[] States { get; private set; }
    public int[] Scores { get; }
    public int CurrentPlayer { get; private set; }

    public int RevealedCount => revealed.Count;
    public bool IsResolving => revealed.Count == 2;
    public bool IsFinished => Layout != null && matchedCards == Layout.Length;

    public MemoryRules(int playerCount)
    {
        Scores = new int[playerCount];
    }

    // Her eleman kartın çift numarasıdır (aynı numaralı iki kart eşleşir)
    public static int[] CreateLayout(int cardCount, System.Random random)
    {
        int[] layout = new int[cardCount / 2 * 2];
        for (int i = 0; i < layout.Length; i++)
            layout[i] = i / 2;

        for (int i = layout.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (layout[i], layout[j]) = (layout[j], layout[i]);
        }
        return layout;
    }

    public void StartRound(int[] layout, int firstPlayer)
    {
        Layout = layout;
        States = new CardState[layout.Length];
        revealed.Clear();
        matchedCards = 0;
        for (int i = 0; i < Scores.Length; i++)
            Scores[i] = 0;
        CurrentPlayer = firstPlayer;
    }

    public bool CanReveal(int player, int index)
    {
        return Layout != null
            && player == CurrentPlayer
            && !IsResolving
            && !IsFinished
            && index >= 0 && index < Layout.Length
            && States[index] == CardState.Hidden;
    }

    public void Reveal(int index)
    {
        States[index] = CardState.Revealed;
        revealed.Add(index);
    }

    // Tek açık kart süre dolunca kapanır, sıra değişmez
    public int HideSingle()
    {
        int index = revealed[0];
        States[index] = CardState.Hidden;
        revealed.Clear();
        return index;
    }

    // Eşleşirse oyuncu puan alır ve tekrar oynar, eşleşmezse sıra diğer oyuncuya geçer
    public PairResult ResolvePair()
    {
        var result = new PairResult { First = revealed[0], Second = revealed[1] };
        result.Matched = Layout[result.First] == Layout[result.Second];

        CardState newState = result.Matched ? CardState.Matched : CardState.Hidden;
        States[result.First] = newState;
        States[result.Second] = newState;

        if (result.Matched)
        {
            matchedCards += 2;
            Scores[CurrentPlayer]++;
        }
        else
        {
            CurrentPlayer = (CurrentPlayer + 1) % Scores.Length;
        }

        revealed.Clear();
        return result;
    }
}
