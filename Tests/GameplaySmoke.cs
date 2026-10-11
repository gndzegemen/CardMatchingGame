using System;
using System.Collections;
using System.Linq;
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
    public void Execute(ExecutionContext ctx)
    {
        if (!EditorApplication.isPlaying) { ctx.ReturnValue = "Enter Play Mode first"; return; }
        if (run != null) { ctx.ReturnValue = "Tests already running"; return; }
        report.Clear();
        started = EditorApplication.timeSinceStartup;
        run = Run();
        SessionState.SetString("CardMatching.Smoke", "RUNNING");
        EditorApplication.update += Step;
        ctx.ReturnValue = "Started menu, singleplayer, room code, lobby and host/server smoke checks";
    }
    static void Step()
    {
        try
        {
            if (EditorApplication.timeSinceStartup - started > 60) throw new Exception("Test timed out");
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
    static void Check(string name, bool pass)
    {
        if (!pass) throw new Exception(name);
        report.AppendLine("OK " + name);
    }
    static T Find<T>(Transform canvas, string path) where T : Component
    {
        var t = canvas.Find(path);
        var c = t != null ? t.GetComponent<T>() : null;
        if (c == null) throw new Exception("Missing " + path + " (" + typeof(T).Name + ")");
        return c;
    }
    static GridElement[] Cards(MemoryGame game) => game.Parent.GetComponentsInChildren<GridElement>().OrderBy(c => c.Index).ToArray();
    // The deck hides when the deal animation completes.
    static bool BoardReady(MemoryGame game) => Cards(game).Length > 0 && !game.deckTransform.gameObject.activeSelf;
    static IEnumerator Run()
    {
        var g = UnityEngine.Object.FindAnyObjectByType<MemoryGame>();
        var menu = UnityEngine.Object.FindAnyObjectByType<MultiplayerMenu>();
        var nm = UnityEngine.Object.FindAnyObjectByType<NetworkManager>();
        Check("Scene references", g != null && menu != null && nm != null && g.LobbyPage != null);
        Check("Network initially stopped", !menu.IsSessionRunning);
        var canvas = g.transform;
        var solo = Find<Button>(canvas, "Panel/StartPage/SoloPanel/SinglePlayerButton");
        var create = Find<Button>(canvas, "Panel/StartPage/FriendPanel/HostButton");
        var join = Find<Button>(canvas, "Panel/StartPage/FriendPanel/JoinButton");
        var address = Find<TMP_InputField>(canvas, "Panel/StartPage/FriendPanel/AddressInput");
        var port = Find<TMP_InputField>(canvas, "Panel/StartPage/PortRow/PortInput");
        var message = Find<TMP_Text>(canvas, "Panel/StartPage/MessageBox/ConnectionMessage");
        var share = Find<Button>(canvas, "Panel/LobbyPage/LobbyPanel/ShareButton");
        var lobbyCode = Find<TMP_Text>(canvas, "Panel/LobbyPage/LobbyPanel/CodeBox/LobbyCode");
        var lobbyStatus = Find<TMP_Text>(canvas, "Panel/LobbyPage/LobbyPanel/LobbyStatus");
        var lobbyHint = Find<TMP_Text>(canvas, "Panel/LobbyPage/LobbyPanel/LobbyHint");
        var cancel = Find<Button>(canvas, "Panel/LobbyPage/CancelButton");
        var gameMenu = Find<Button>(canvas, "Panel/MainPage/BackToMenuButton");
        var finishMenu = Find<Button>(canvas, "Panel/FinishPage/Button_quit");
        int originalCount = g.ElementNumber;
        float duration = g.singlePlayerDuration, deal = g.dealDuration, stagger = g.dealStagger, check = g.checkDelay, timeout = g.firstRevealTimeout;
        string originalPort = port.text, originalAddress = address.text;
        try
        {
            Check("Main menu shows solo and friend options", g.StartPage.activeSelf && solo.gameObject.activeInHierarchy && solo.interactable
                && create.gameObject.activeInHierarchy && create.interactable && join.interactable && address.interactable);

            Check("Room code is normalized", MultiplayerMenu.NormalizeRoomCode(" k7p2q ") == "K7P2Q");
            Check("Room code from invite link", MultiplayerMenu.NormalizeRoomCode("https://example.com/game/?room=K7P2Q#top") == "K7P2Q");
            Check("Room code from link with more parameters", MultiplayerMenu.NormalizeRoomCode("https://example.com/?x=1&room=abcde&y=2") == "ABCDE");
            foreach (string bad in new[] { "", "K7P2", "K7P2QQ", "HELLO", "K7P2I", "192.168.1.20", "ws://k7p2q" })
                Check("Reject room code '" + bad + "'", MultiplayerMenu.NormalizeRoomCode(bad) == null);

            foreach (string bad in new[] { "", "http://example.com", "host:0", "host:65536", "host:-1", "host:abc", "host:", "ws://a/path", "a b", "user@host", "ws://host?x=1", "::1" })
                Check("Reject endpoint " + bad, !SelfHostedEndpoint.TryParse(bad, 7777, out _));
            Check("Default port", SelfHostedEndpoint.TryParse("localhost", 7777, out var endpoint) && endpoint.Port == 7777 && !endpoint.Secure);
            Check("Explicit port 80", SelfHostedEndpoint.TryParse("localhost:80", 7777, out endpoint) && endpoint.Port == 80);
            Check("WSS default 443", SelfHostedEndpoint.TryParse("wss://game.example.com", 7777, out endpoint) && endpoint.Port == 443 && endpoint.Secure);
            Check("IPv6 endpoint", SelfHostedEndpoint.TryParse("[::1]:7788", 7777, out endpoint) && endpoint.Port == 7788 && endpoint.Host == "::1");

            address.text = "";
            join.onClick.Invoke();
            Check("Empty join explains what to enter", !menu.IsSessionRunning && message.gameObject.activeInHierarchy && message.text.Contains("IP address"));
            address.text = "K7P2Q";
            join.onClick.Invoke();
            Check("Desktop explains room codes are for browsers", !menu.IsSessionRunning && message.text.Contains("browser"));

            g.ElementNumber = 4;
            g.singlePlayerDuration = 1.5f;
            g.dealDuration = .02f;
            g.dealStagger = 0f;
            g.checkDelay = .15f;
            g.firstRevealTimeout = .15f;
            solo.onClick.Invoke();
            Check("Solo button starts an offline game", g.IsSinglePlayer && !g.IsOnline && !nm.IsListening && g.MainPage.activeSelf);
            Check("Menu message cleared when the game starts", !message.gameObject.activeSelf);
            Check("No countdown during deal", Mathf.Approximately(g.RemainingSeconds, 1.5f) && !BoardReady(g));
            while (!BoardReady(g)) yield return null;
            Check("All cards dealt", Cards(g).Length == 4 && Cards(g).All(c => c.CurrentState == GridElement.State.Back));
            Check("Main menu button visible in solo game", gameMenu.gameObject.activeInHierarchy && gameMenu.interactable);
            var cards = Cards(g);
            var first = cards[0]; var different = cards.First(c => c.PairId != first.PairId);
            g.OnCardClicked(first); g.OnCardClicked(different);
            var third = cards.First(c => c != first && c != different);
            g.OnCardClicked(third);
            Check("Third card blocked while resolving", third.CurrentState == GridElement.State.Back);
            while (first.CurrentState == GridElement.State.Front) yield return null;
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

            // A long editor frame (e.g. another execute_code compiling) can jump past the deadline
            // before the final pair is selected; that attempt proves nothing, so it is retried.
            for (int attempt = 1; ; attempt++)
            {
                g.ResetGame();
                while (!BoardReady(g)) yield return null;
                Check("Restart resets timer and score", g.RemainingSeconds > 1.3f && g.statusText.text.Contains("Pairs 0/2"));
                cards = Cards(g);
                var pair = cards.Where(c => c.PairId == cards[0].PairId).ToArray();
                g.OnCardClicked(pair[0]); g.OnCardClicked(pair[1]);
                while (pair.Any(c => c.CurrentState == GridElement.State.Front)) yield return null;
                Check("Matching pair scores and disappears", g.statusText.text.Contains("Pairs 1/2") && pair.All(c => c.CurrentState == GridElement.State.Empty));
                pair = cards.Where(c => c.PairId != pair[0].PairId).ToArray();
                // Select the final pair with less time left than the pair feedback delay (.15s).
                while (g.RemainingSeconds > .12f) yield return null;
                if (g.FinishPage.activeSelf && attempt < 3)
                {
                    report.AppendLine("   (a frame skipped past the deadline; retrying)");
                    continue;
                }
                g.OnCardClicked(pair[0]); g.OnCardClicked(pair[1]);
                while (!g.FinishPage.activeSelf) yield return null;
                Check("Final pair before zero wins despite feedback delay (result: " + g.resultText.text.Replace('\n', '|') + ")",
                    g.resultText.text.Contains("You win!") && g.resultText.text.Contains("2/2"));
                break;
            }

            finishMenu.onClick.Invoke();
            // Destroy() removes the cards at the end of the frame
            waitUntil = Time.unscaledTimeAsDouble + .2;
            while (Cards(g).Length > 0 && Time.unscaledTimeAsDouble < waitUntil) yield return null;
            Check("Finish screen MAIN MENU returns to the menu", g.StartPage.activeSelf && !g.FinishPage.activeSelf && !g.IsSinglePlayer && Cards(g).Length == 0);

            solo.onClick.Invoke();
            gameMenu.onClick.Invoke();
            waitUntil = Time.unscaledTimeAsDouble + .2;
            while (Time.unscaledTimeAsDouble < waitUntil) yield return null;
            Check("MAIN MENU during deal cancels pending animation and clears board", g.StartPage.activeSelf && !g.IsSinglePlayer && Cards(g).Length == 0);
            g.StartSinglePlayerTimed();
            g.ResetGame();
            while (!BoardReady(g)) yield return null;
            Check("Immediate restart leaves one board", Cards(g).Length == 4);
            gameMenu.onClick.Invoke();

            port.text = "17777";
            create.onClick.Invoke();
            while (!nm.IsHost && menu.IsSessionRunning) yield return null;
            Check("Create room hosts directly on desktop", nm.IsHost && nm.IsListening);
            Check("Lobby shows the address and a copy button", g.LobbyPage.activeSelf && !g.StartPage.activeSelf
                && lobbyCode.text.EndsWith(":17777") && share.gameObject.activeInHierarchy);
            Check("Host owns one of two slots", UnityEngine.Object.FindAnyObjectByType<NetworkMemorySession>().LocalSlot == 0);
            string clipboard = GUIUtility.systemCopyBuffer;
            share.onClick.Invoke();
            bool copied = GUIUtility.systemCopyBuffer == lobbyCode.text;
            GUIUtility.systemCopyBuffer = clipboard;
            Check("Copy button copies the address", copied && lobbyStatus.text.Contains("copied"));
            g.StartSinglePlayerTimed();
            Check("Solo disabled while hosting", !g.IsSinglePlayer && !solo.interactable && g.LobbyPage.activeSelf);
            menu.OnOpponentLeft();
            Check("Opponent leaving returns the host to the lobby", g.LobbyPage.activeSelf && lobbyHint.text.Contains("Your friend left")
                && lobbyStatus.text.Contains("Waiting for a new player"));
            cancel.onClick.Invoke();
            while (menu.IsSessionRunning) yield return null;
            yield return null;
            Check("Cancel returns to the menu and unlocks it", g.StartPage.activeSelf && !g.LobbyPage.activeSelf && solo.interactable
                && create.interactable && !message.gameObject.activeInHierarchy);

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
            menu.ShowMessage(null);
            g.ElementNumber = originalCount;
            g.singlePlayerDuration = duration;
            g.dealDuration = deal;
            g.dealStagger = stagger;
            g.checkDelay = check;
            g.firstRevealTimeout = timeout;
            port.text = originalPort;
            address.text = originalAddress;
        }
    }
}
