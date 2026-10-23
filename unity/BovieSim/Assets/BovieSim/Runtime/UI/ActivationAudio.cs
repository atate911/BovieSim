using UnityEngine;

namespace BovieSim
{
    /// <summary>Activation tones: cut and coag have distinct pitches, as on the generator. Procedural square wave.</summary>
    public sealed class ActivationAudio : MonoBehaviour
    {
        AudioSource src;
        volatile float freq = 500, targetGain, gain;
        double phase; int sampleRate;

        void Awake()
        {
            sampleRate = AudioSettings.outputSampleRate;
            src = gameObject.AddComponent<AudioSource>();
            src.playOnAwake = false; src.loop = true; src.spatialBlend = 0;
            src.clip = AudioClip.Create("BovieTone", sampleRate, 1, sampleRate, true, OnRead);
            src.Play();
        }

        public void Set(float hz, bool on, float volume = 0.08f) { freq = hz; targetGain = on ? volume : 0; }

        void OnRead(float[] data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                gain += (targetGain - gain) * 0.002f;
                phase += freq / sampleRate; if (phase >= 1) phase -= 1;
                float sq = phase < 0.5 ? 1f : -1f;
                data[i] = sq * gain * 0.6f + Mathf.Sin((float)(phase * 2 * Mathf.PI)) * gain * 0.4f;
            }
        }
    }
}
