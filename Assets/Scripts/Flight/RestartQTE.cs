using System;
using UnityEngine;
using UnityEngine.InputSystem;

// Quick-time key sequence (restart a disabled car, break free from an arrest). Random keys from
// W/A/S/D/Space/F, each open for a timing window. Wrong key or timeout: the sequence restarts (restart
// QTE) or fails outright (one-shot QTE). One sequence at a time; created on first use.
public class RestartQTE : MonoBehaviour
{
    static readonly Key[] Keys = { Key.W, Key.A, Key.S, Key.D, Key.Space, Key.F };
    static readonly string[] Labels = { "W", "A", "S", "D", "SPACE", "F" };

    static RestartQTE instance;

    public static bool Active => instance != null && instance.running;

    bool running, retryOnFail;
    int[] sequence;
    int index;
    float window, promptEnd;
    string title;
    Action onSuccess, onFail;
    int startFrame;

    public static void Begin(string title, int length, float window, bool retryOnFail, Action onSuccess, Action onFail = null)
    {
        if (instance == null) instance = new GameObject("RestartQTE").AddComponent<RestartQTE>();
        var q = instance;
        q.title = title;
        q.window = window;
        q.retryOnFail = retryOnFail;
        q.onSuccess = onSuccess;
        q.onFail = onFail;
        q.sequence = new int[Mathf.Max(1, length)];
        q.running = true;
        q.startFrame = Time.frameCount; // the key that started it doesn't count as an answer
        q.NewSequence();
    }

    public static void Cancel()
    {
        if (instance != null) instance.running = false;
    }

    void NewSequence()
    {
        for (int i = 0; i < sequence.Length; i++) sequence[i] = UnityEngine.Random.Range(0, Keys.Length);
        index = 0;
        promptEnd = Time.time + window;
    }

    void Update()
    {
        if (!running) return;
        var kb = Keyboard.current;
        if (kb == null) return;

        if (Time.time > promptEnd) { Miss(); return; }
        if (Time.frameCount == startFrame) return;
        for (int k = 0; k < Keys.Length; k++)
        {
            if (!kb[Keys[k]].wasPressedThisFrame) continue;
            if (k != sequence[index]) { Miss(); return; }
            index++;
            promptEnd = Time.time + window;
            if (index >= sequence.Length)
            {
                running = false;
                onSuccess?.Invoke();
            }
            return;
        }
    }

    void Miss()
    {
        if (retryOnFail) { NewSequence(); return; } // costs only time
        running = false;
        onFail?.Invoke();
    }

    void OnGUI()
    {
        if (!running) return;
        float w = 64f, gap = 8f;
        float total = sequence.Length * (w + gap) - gap;
        float x0 = Screen.width / 2f - total / 2f, y = Screen.height * 0.62f;
        GUI.Label(new Rect(Screen.width / 2f - 150, y - 28, 300, 22), title);
        var prev = GUI.color;
        for (int i = 0; i < sequence.Length; i++)
        {
            GUI.color = i < index ? new Color(0.3f, 1f, 0.4f) : i == index ? Color.white : new Color(1f, 1f, 1f, 0.35f);
            GUI.Box(new Rect(x0 + i * (w + gap), y, w, 30), Labels[sequence[i]]);
        }
        // Time left on the current prompt.
        float left = Mathf.Clamp01((promptEnd - Time.time) / window);
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(new Rect(x0, y + 36, total, 6), Texture2D.whiteTexture);
        GUI.color = Color.Lerp(new Color(1f, 0.3f, 0.2f), new Color(0.3f, 0.9f, 1f), left);
        GUI.DrawTexture(new Rect(x0, y + 36, total * left, 6), Texture2D.whiteTexture);
        GUI.color = prev;
    }
}
