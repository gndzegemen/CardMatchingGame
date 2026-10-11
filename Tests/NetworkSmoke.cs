using System;
using System.Collections;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEditor;
using Unity.Netcode;
using KitWright.Editor.Tools.Scripting;

// Run via KitWright execute_code in Play Mode, with the editor joined to a dedicated server as player 0
// (first to connect) and a second client connected, after the round has started.
// Results: UnityEditor.SessionState.GetString("CardMatching.NetworkSmoke", "").
public class NetworkSmoke : IKitWrightCommand
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
        SessionState.SetString("CardMatching.NetworkSmoke", "RUNNING");
        EditorApplication.update += Step;
        ctx.ReturnValue = "Validating dedicated-server RPCs over a real client connection";
    }
    static void Step()
    {
        try
        {
            if (EditorApplication.timeSinceStartup - started > 90) throw new Exception("Network test timeout");
            if (run.MoveNext()) return;
            End("PASS");
        }
        catch (Exception e) { End("FAIL: " + e); }
    }
    static void End(string status)
    {
        EditorApplication.update -= Step;
        (run as IDisposable)?.Dispose();
        run = null;
        SessionState.SetString("CardMatching.NetworkSmoke", status + "\n" + report);
    }
    static void Check(string name, bool pass)
    {
        if (!pass) throw new Exception(name);
        report.AppendLine("OK " + name);
    }
    static IEnumerator Run()
    {
        var g = UnityEngine.Object.FindAnyObjectByType<MemoryGame>();
        var nm = UnityEngine.Object.FindAnyObjectByType<NetworkManager>();
        var session = UnityEngine.Object.FindAnyObjectByType<NetworkMemorySession>();
        Check("External dedicated server connected", g != null && nm != null && session != null && nm.IsConnectedClient && !nm.IsServer && session.LocalSlot == 0);
        Check("Synchronized 18-card board", g.Parent.childCount == 18 && g.IsOnline && g.MainPage.activeSelf && !g.LobbyPage.activeSelf);
        var cards = g.Parent.GetComponentsInChildren<GridElement>().OrderBy(c => c.Index).ToArray();
        while (!cards.Any(c => c.button.interactable)) yield return null;
        Check("Status shows whose turn it is", g.statusText.gameObject.activeSelf && g.statusText.text.Contains("Your turn"));
        foreach (var pair in cards.GroupBy(c => c.PairId))
        {
            var p = pair.ToArray();
            Check("Two cards in pair " + pair.Key, p.Length == 2);
            g.OnCardClicked(p[0]); g.OnCardClicked(p[1]);
            while (p[0].CurrentState != GridElement.State.Empty || p[1].CurrentState != GridElement.State.Empty) yield return null;
            Check("Server resolved pair " + pair.Key, p.All(c => !c.button.interactable));
            yield return null;
        }
        while (!g.FinishPage.activeSelf) yield return null;
        // The finish page opens from the replicated round state, so the final score arrives with it.
        Check("Finish page shows the final score at once (" + g.resultText.text.Replace('\n', '|') + ")",
            g.resultText.text.Contains("You win!") && g.resultText.text.Contains("You 9 - 0 Opponent"));
        Check("Finished board locked", cards.All(c => !c.button.interactable));
        g.ResetGame();
        yield return null; yield return null;
        while (!g.resultText.text.Contains("Waiting for opponent")) yield return null;
        Check("Single rematch vote does not restart", g.FinishPage.activeSelf && !g.MainPage.activeSelf);
    }
}
