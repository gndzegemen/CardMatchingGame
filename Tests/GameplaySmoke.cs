using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEditor;
using Unity.Netcode;
using TMPro;
using UnityEngine.UI;
using KitWright.Editor.Tools.Scripting;

// Run via KitWright execute_code while the game is in Play Mode.
// Results: UnityEditor.SessionState.GetString("CardMatching.Smoke", "").
public class GameplaySmoke : IKitWrightCommand
{
    static IEnumerator run;
    static double started;
    static readonly StringBuilder report = new StringBuilder();
    const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    public void Execute(ExecutionContext ctx)
    {
        if (!EditorApplication.isPlaying) { ctx.ReturnValue = "Enter Play Mode first"; return; }
        if (run != null) { ctx.ReturnValue = "Tests already running"; return; }
        report.Clear();
        started = EditorApplication.timeSinceStartup;
        run = Run();
        SessionState.SetString("CardMatching.Smoke", "RUNNING");
        EditorApplication.update += Step;
        ctx.ReturnValue = "Started singleplayer, endpoint and host/server smoke checks";
    }
    static void Step()
    {
        try
        {
            if (EditorApplication.timeSinceStartup - started > 45) throw new Exception("Test timed out");
            if (run.MoveNext()) return;
            Finish("PASS");
        }
        catch (Exception e) { Finish("FAIL: " + e); }
    }
    static void Finish(string result)
    {
        EditorApplication.update -= Step;
        (run as IDisposable)?.Dispose();
        run = null;
        SessionState.SetString("CardMatching.Smoke", result + "\n" + report);
    }
    static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Fields).GetValue(o);
    static void Check(string name, bool pass)
    {
        if (!pass) throw new Exception(name);
        report.AppendLine("OK " + name);
    }
    static GridElement[] Cards(MemoryGame game) => game.Parent.GetComponentsInChildren<GridElement>().OrderBy(c => c.Index).ToArray();
    static IEnumerator Run()
    {
        var g = UnityEngine.Object.FindAnyObjectByType<MemoryGame>();
        var menu = UnityEngine.Object.FindAnyObjectByType<MultiplayerMenu>();
        var nm = UnityEngine.Object.FindAnyObjectByType<NetworkManager>();
        Check("Scene references", g != null && menu != null && nm != null);
        Check("Network initially stopped", !menu.IsSessionRunning);
        var solo = Field<Button>(menu, "singlePlayerButton");
        var port = Field<TMP_InputField>(menu, "portInput");
        int originalCount = g.ElementNumber;
        float duration = g.singlePlayerDuration, deal = g.dealDuration, stagger = g.dealStagger, check = g.checkDelay, timeout = g.firstRevealTimeout;
        string originalPort = port.text;
        try
        {
            foreach (string bad in new[]{ "", "http://example.com", "host:0", "host:65536", "host:-1", "host:abc", "host:", "ws://a/path", "a b", "user@host", "ws://host?x=1", "::1" })
                Check("Reject endpoint " + bad, !SelfHostedEndpoint.TryParse(bad, 7777, out _));
            Check("Default port", SelfHostedEndpoint.TryParse("localhost", 7777, out var endpoint) && endpoint.Port == 7777 && !endpoint.Secure);
            Check("Explicit port 80", SelfHostedEndpoint.TryParse("localhost:80", 7777, out endpoint) && endpoint.Port == 80);
            Check("WSS default 443", SelfHostedEndpoint.TryParse("wss://game.example.com", 7777, out endpoint) && endpoint.Port == 443 && endpoint.Secure);
            Check("IPv6 endpoint", SelfHostedEndpoint.TryParse("[::1]:7788", 7777, out endpoint) && endpoint.Port == 7788 && endpoint.Host == "::1");

            g.ElementNumber = 4;
            g.singlePlayerDuration = 1.5f;
            g.dealDuration = .02f;
            g.dealStagger = 0f;
            g.checkDelay = .15f;
            g.firstRevealTimeout = .15f;
            solo.onClick.Invoke();
            Check("Solo button starts an offline game", g.IsSinglePlayer && !g.IsOnline && !nm.IsListening);
            Check("No countdown during deal", Mathf.Approximately(g.RemainingSeconds, 1.5f) && !Field<bool>(g, "boardReady"));
            while (!Field<bool>(g, "boardReady")) yield return null;
            Check("All cards dealt", Cards(g).Length == 4 && Cards(g).All(c => c.CurrentState == GridElement.State.Back));
            var cards = Cards(g);
            var first = cards[0]; var different = cards.First(c => c.PairId != first.PairId);
            g.OnCardClicked(first); g.OnCardClicked(different);
            var third = cards.First(c => c != first && c != different);
            g.OnCardClicked(third);
            Check("Third card blocked while resolving", third.CurrentState == GridElement.State.Back);
            while (Field<bool>(g, "singlePlayerResolving")) yield return null;
            Check("Mismatch hides both cards", first.CurrentState == GridElement.State.Back && different.CurrentState == GridElement.State.Back);
            g.OnCardClicked(first);
            while (first.CurrentState == GridElement.State.Front) yield return null;
            Check("Single reveal closes after timeout", first.CurrentState == GridElement.State.Back);
            Check("Countdown runs after deal", g.RemainingSeconds < 1.5f && g.RemainingSeconds > 0f);

            while (g.RemainingSeconds > .08f) yield return null;
            g.OnCardClicked(first); g.OnCardClicked(different);
            while (!g.FinishPage.activeSelf) yield return null;
            Check("Zero time ends game and locks cards", g.resultText.text.Contains("Time's up") && Cards(g).All(c => !c.button.interactable));
            double waitUntil = Time.unscaledTimeAsDouble + .25;
            while (Time.unscaledTimeAsDouble < waitUntil) yield return null;
            Check("No late pair callback after timeout", g.FinishPage.activeSelf && g.RemainingSeconds == 0f);

            g.ResetGame();
            while (!Field<bool>(g, "boardReady")) yield return null;
            Check("Restart resets timer and score", g.RemainingSeconds > 1.3f && Field<MemoryRules>(g, "singlePlayerRules").Scores[0] == 0);
            cards = Cards(g);
            var pair = cards.Where(c => c.PairId == cards[0].PairId).ToArray();
            g.OnCardClicked(pair[0]); g.OnCardClicked(pair[1]);
            while (Field<bool>(g, "singlePlayerResolving")) yield return null;
            Check("Matching pair scores and disappears", Field<MemoryRules>(g, "singlePlayerRules").Scores[0] == 1 && pair.All(c => c.CurrentState == GridElement.State.Empty));
            pair = cards.Where(c => c.PairId != pair[0].PairId).ToArray();
            while (g.RemainingSeconds > .08f) yield return null;
            g.OnCardClicked(pair[0]); g.OnCardClicked(pair[1]);
            while (!g.FinishPage.activeSelf) yield return null;
            Check("Final pair before zero wins despite feedback delay", g.resultText.text.Contains("You win!") && g.resultText.text.Contains("2/2"));

            g.ResetGame();
            g.QuitGame();
            waitUntil = Time.unscaledTimeAsDouble + .2;
            while (Time.unscaledTimeAsDouble < waitUntil) yield return null;
            Check("Exit during deal cancels pending animation and clears board", g.StartPage.activeSelf && !g.IsSinglePlayer && Cards(g).Length == 0);
            g.StartSinglePlayerTimed();
            g.ResetGame();
            while (!Field<bool>(g, "boardReady")) yield return null;
            Check("Immediate restart leaves one board", Cards(g).Length == 4);
            g.QuitGame();

            port.text = "17777";
            menu.Host();
            while (!nm.IsHost && menu.IsSessionRunning) yield return null;
            Check("Native player hosts directly", nm.IsHost && nm.IsListening);
            Check("Host owns one of two slots", UnityEngine.Object.FindAnyObjectByType<NetworkMemorySession>().LocalSlot == 0);
            g.StartSinglePlayerTimed();
            Check("Solo disabled while hosting", !g.IsSinglePlayer && !solo.interactable);
            menu.Leave();
            while (menu.IsSessionRunning) yield return null;
            yield return null;
            Check("UI unlocks after shutdown", solo.interactable);
            menu.StartDedicatedServer();
            while (!nm.IsServer && menu.IsSessionRunning) yield return null;
            Check("Dedicated server has no local player", nm.IsServer && !nm.IsClient && nm.ConnectedClientsIds.Count == 0 && UnityEngine.Object.FindAnyObjectByType<NetworkMemorySession>().LocalSlot == -1);
            menu.Leave();
            while (menu.IsSessionRunning) yield return null;
            yield return null;
            solo.onClick.Invoke();
            Check("Solo starts after leaving server", g.IsSinglePlayer && !nm.IsListening);
        }
        finally
        {
            if (menu.IsSessionRunning) menu.Leave();
            g.ShowStartPage();
            g.ElementNumber = originalCount;
            g.singlePlayerDuration = duration;
            g.dealDuration = deal;
            g.dealStagger = stagger;
            g.checkDelay = check;
            g.firstRevealTimeout = timeout;
            port.text = originalPort;
        }
    }
}
