using System;
using NAudio.CoreAudioApi;
using UnityEngine;

/// <summary>
/// Listens to the Windows output device (music, videos, calls, games - anything that reaches the speakers)
/// so Susu can wait for a quiet moment before speaking by herself.
/// It only polls while <see cref="Monitoring"/> is on.
/// </summary>
public class SusuSystemAudio : MonoBehaviour
{
    public static SusuSystemAudio Instance { get; private set; }

    [Tooltip("Output peak (0-1) above this counts as sound.")]
    public float threshold = 0.008f;
    [Tooltip("Seconds between samples. The peak meter only covers a few milliseconds, so sample often.")]
    public float pollSeconds = 0.25f;

    /// <summary>Turn on a little before the answer is needed; turning it on restarts the quiet timer.</summary>
    public bool Monitoring
    {
        get => monitoring;
        set
        {
            if (monitoring == value) return;
            monitoring = value;
            if (value) { lastLoudAt = Time.unscaledTime; nextPollAt = 0f; }
            else ReleaseDevice();
        }
    }

    /// <summary>Seconds since sound was last heard (counted from the moment monitoring started).</summary>
    public float QuietFor => monitoring ? Time.unscaledTime - lastLoudAt : 0f;

    /// <summary>False when the audio device cannot be read; callers should then not wait for silence.</summary>
    public bool Available { get; private set; } = true;

    bool monitoring;
    float lastLoudAt, nextPollAt, deviceRefreshAt;
    int failures;
    MMDeviceEnumerator enumerator;
    MMDevice device;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    void OnDestroy()
    {
        ReleaseDevice();
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        if (!monitoring || Time.unscaledTime < nextPollAt) return;
        nextPollAt = Time.unscaledTime + pollSeconds;

        try
        {
            // Re-read the default device now and then: headphones get plugged in, outputs get switched.
            if (device == null || Time.unscaledTime >= deviceRefreshAt)
            {
                device?.Dispose();
                enumerator ??= new MMDeviceEnumerator();
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                deviceRefreshAt = Time.unscaledTime + 10f;
            }
            if (device.AudioMeterInformation.MasterPeakValue > threshold) lastLoudAt = Time.unscaledTime;
            failures = 0;
            Available = true;
        }
        catch (Exception e)
        {
            device?.Dispose();
            device = null;
            if (++failures == 5)
            {
                Available = false; // e.g. no output device at all
                Debug.LogWarning("[SusuVoice] Cannot read the Windows audio level: " + e.Message);
            }
        }
    }

    void ReleaseDevice()
    {
        device?.Dispose(); device = null;
        enumerator?.Dispose(); enumerator = null;
    }
}
