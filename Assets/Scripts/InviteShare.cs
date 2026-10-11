using System.Runtime.InteropServices;
using UnityEngine;

// Sends a room invite: the share sheet on phones, the clipboard elsewhere.
// Only browsers run the JavaScript side; the editor and desktop builds copy with Unity's clipboard.
public static class InviteShare
{
    public enum Result { Failed = 0, Shared = 1, Copied = 2 }

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] static extern int CardMatching_ShareLink(string url, string text);
#else
    static int CardMatching_ShareLink(string url, string text)
    {
        GUIUtility.systemCopyBuffer = url;
        return (int)Result.Copied;
    }
#endif

    public static Result Share(string url, string text) => (Result)CardMatching_ShareLink(url, text ?? "");
}
