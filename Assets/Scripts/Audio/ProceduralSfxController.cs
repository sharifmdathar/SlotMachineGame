using System;
using UnityEngine;

namespace SlotMachine.Audio
{
    /// <summary>
    /// Synthesizes every sound effect at startup with <see cref="AudioClip.Create"/>. No external
    /// audio files are required, so the WebGL build has no extra download or import dependencies.
    ///
    /// The generator uses simple square/triangle/saw waves plus short envelopes to emulate retro
    /// arcade hardware. All clips are mono, 44.1 kHz, and normalized below full scale to avoid
    /// clipping when several one-shots overlap (for example a reel thud during a win jingle).
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(AudioSource))]
    public sealed class ProceduralSfxController : MonoBehaviour
    {
        private const float TwoPi = Mathf.PI * 2f;

        [SerializeField] private AudioSource _audioSource;
        [SerializeField, Range(0f, 1f)] private float _masterVolume = 0.38f;
        [SerializeField, Min(8000)] private int _sampleRate = 44100;

        private AudioClip _buttonClick;
        private AudioClip _leverPull;
        private AudioClip _reelTick;
        private AudioClip _reelStop;
        private AudioClip _winJingle;
        private AudioClip _jackpotFanfare;
        private System.Random _noise;

        /// <summary>Scene-local audio service used by UI buttons for click feedback.</summary>
        public static ProceduralSfxController Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            EnsureAudioSource();
            _noise = new System.Random(0x51A7);
            GenerateClips();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            DestroyClip(_buttonClick);
            DestroyClip(_leverPull);
            DestroyClip(_reelTick);
            DestroyClip(_reelStop);
            DestroyClip(_winJingle);
            DestroyClip(_jackpotFanfare);
        }

        /// <summary>Plays the short UI click. Safe to call when no audio service exists.</summary>
        public static void PlayButtonClickGlobal()
        {
            if (Instance != null)
            {
                Instance.PlayButtonClick();
            }
        }

        public void PlayButtonClick()
        {
            Play(_buttonClick, 0.65f);
        }

        public void PlayLeverPull()
        {
            Play(_leverPull, 0.9f);
        }

        public void PlayReelTick()
        {
            Play(_reelTick, 0.4f);
        }

        public void PlayReelStop()
        {
            Play(_reelStop, 0.85f);
        }

        public void PlayWin()
        {
            Play(_winJingle, 0.8f);
        }

        public void PlayJackpot()
        {
            Play(_jackpotFanfare, 1f);
        }

        private void Play(AudioClip clip, float volumeScale)
        {
            if (clip == null || _audioSource == null)
            {
                return;
            }

            _audioSource.PlayOneShot(clip, Mathf.Clamp01(_masterVolume * volumeScale));
        }

        private void EnsureAudioSource()
        {
            if (_audioSource == null)
            {
                _audioSource = GetComponent<AudioSource>();
            }

            if (_audioSource == null)
            {
                _audioSource = gameObject.AddComponent<AudioSource>();
            }

            _audioSource.playOnAwake = false;
            _audioSource.loop = false;
            _audioSource.spatialBlend = 0f;
            _audioSource.volume = 1f;
        }

        private void GenerateClips()
        {
            _buttonClick = BuildClick();
            _leverPull = BuildLeverPull();
            _reelTick = BuildReelTick();
            _reelStop = BuildReelStop();
            _winJingle = BuildWinJingle();
            _jackpotFanfare = BuildJackpotFanfare();
        }

        private AudioClip BuildClick()
        {
            float[] samples = NewBuffer(0.055f);
            AddTone(samples, 0f, 0.05f, 1150f, 760f, 0.24f, Waveform.Square, 0.002f);
            AddNoise(samples, 0f, 0.012f, 0.08f);
            return BuildClip("Sfx_ButtonClick", samples);
        }

        private AudioClip BuildLeverPull()
        {
            float[] samples = NewBuffer(0.32f);
            AddTone(samples, 0f, 0.3f, 300f, 72f, 0.26f, Waveform.Saw, 0.008f);
            AddTone(samples, 0.02f, 0.24f, 140f, 55f, 0.12f, Waveform.Triangle, 0.01f);
            AddNoise(samples, 0f, 0.13f, 0.045f);
            return BuildClip("Sfx_LeverPull", samples);
        }

        private AudioClip BuildReelTick()
        {
            float[] samples = NewBuffer(0.028f);
            AddTone(samples, 0f, 0.025f, 1800f, 1400f, 0.17f, Waveform.Square, 0.001f);
            return BuildClip("Sfx_ReelTick", samples);
        }

        private AudioClip BuildReelStop()
        {
            float[] samples = NewBuffer(0.24f);
            AddTone(samples, 0f, 0.22f, 105f, 58f, 0.34f, Waveform.Sine, 0.003f);
            AddTone(samples, 0f, 0.12f, 190f, 80f, 0.18f, Waveform.Triangle, 0.002f);
            AddNoise(samples, 0f, 0.055f, 0.12f);
            return BuildClip("Sfx_ReelStop", samples);
        }

        private AudioClip BuildWinJingle()
        {
            float[] samples = NewBuffer(0.78f);
            float[] notes = { 523.25f, 659.25f, 783.99f, 1046.5f };

            for (int i = 0; i < notes.Length; i++)
            {
                AddTone(samples, i * 0.1f, 0.24f, notes[i], notes[i], 0.16f, Waveform.Triangle, 0.01f);
                AddTone(samples, i * 0.1f, 0.18f, notes[i] * 2f, notes[i] * 2f, 0.04f, Waveform.Square, 0.005f);
            }

            AddTone(samples, 0.42f, 0.32f, 1046.5f, 1046.5f, 0.12f, Waveform.Triangle, 0.01f);
            return BuildClip("Sfx_WinJingle", samples);
        }

        private AudioClip BuildJackpotFanfare()
        {
            float[] samples = NewBuffer(1.7f);
            float[] notes = { 523.25f, 659.25f, 783.99f, 1046.5f, 1318.5f, 1568f };

            for (int i = 0; i < notes.Length; i++)
            {
                float start = i * 0.13f;
                AddTone(samples, start, 0.24f, notes[i], notes[i], 0.15f, Waveform.Triangle, 0.006f);
                AddTone(samples, start, 0.2f, notes[i] * 0.5f, notes[i] * 0.5f, 0.06f, Waveform.Square, 0.004f);
            }

            // Sustained major chord under the final sparkle.
            AddTone(samples, 0.82f, 0.8f, 523.25f, 523.25f, 0.11f, Waveform.Triangle, 0.02f);
            AddTone(samples, 0.82f, 0.8f, 659.25f, 659.25f, 0.09f, Waveform.Triangle, 0.02f);
            AddTone(samples, 0.82f, 0.8f, 783.99f, 783.99f, 0.09f, Waveform.Triangle, 0.02f);
            AddTone(samples, 0.82f, 0.8f, 1046.5f, 1046.5f, 0.08f, Waveform.Triangle, 0.02f);

            for (int i = 0; i < 7; i++)
            {
                float start = 0.1f + (i * 0.19f);
                AddTone(samples, start, 0.09f, 2200f + (i * 180f), 2600f + (i * 220f), 0.035f, Waveform.Sine, 0.002f);
            }

            return BuildClip("Sfx_JackpotFanfare", samples);
        }

        private float[] NewBuffer(float durationSeconds)
        {
            int length = Mathf.Max(1, Mathf.CeilToInt(durationSeconds * _sampleRate));
            return new float[length];
        }

        private AudioClip BuildClip(string clipName, float[] samples)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = Mathf.Clamp(samples[i], -0.92f, 0.92f);
            }

            AudioClip clip = AudioClip.Create(clipName, samples.Length, 1, _sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private void AddTone(
            float[] buffer,
            float startSeconds,
            float durationSeconds,
            float startFrequency,
            float endFrequency,
            float amplitude,
            Waveform waveform,
            float attackSeconds)
        {
            int startSample = Mathf.Clamp(Mathf.RoundToInt(startSeconds * _sampleRate), 0, buffer.Length - 1);
            int sampleCount = Mathf.Min(Mathf.RoundToInt(durationSeconds * _sampleRate), buffer.Length - startSample);
            if (sampleCount <= 0)
            {
                return;
            }

            double phase = 0.0;
            float attack = Mathf.Max(0.0001f, attackSeconds);

            for (int i = 0; i < sampleCount; i++)
            {
                float progress = i / (float)sampleCount;
                float frequency = Mathf.Lerp(startFrequency, endFrequency, progress);
                phase += frequency / _sampleRate;

                float envelope = Mathf.Clamp01((i / (float)_sampleRate) / attack);
                envelope *= Mathf.Pow(1f - progress, 1.8f);

                float wave = SampleWaveform(waveform, (float)phase);
                buffer[startSample + i] += wave * amplitude * envelope;
            }
        }

        private void AddNoise(float[] buffer, float startSeconds, float durationSeconds, float amplitude)
        {
            int startSample = Mathf.Clamp(Mathf.RoundToInt(startSeconds * _sampleRate), 0, buffer.Length - 1);
            int sampleCount = Mathf.Min(Mathf.RoundToInt(durationSeconds * _sampleRate), buffer.Length - startSample);
            if (sampleCount <= 0)
            {
                return;
            }

            for (int i = 0; i < sampleCount; i++)
            {
                float progress = i / (float)sampleCount;
                float envelope = Mathf.Pow(1f - progress, 3f);
                float noise = ((float)_noise.NextDouble() * 2f) - 1f;
                buffer[startSample + i] += noise * amplitude * envelope;
            }
        }

        private static float SampleWaveform(Waveform waveform, float phase)
        {
            float wrapped = phase - Mathf.Floor(phase);

            switch (waveform)
            {
                case Waveform.Square:
                    return wrapped < 0.5f ? 1f : -1f;
                case Waveform.Triangle:
                    return (4f * Mathf.Abs(wrapped - 0.5f)) - 1f;
                case Waveform.Saw:
                    return (2f * wrapped) - 1f;
                default:
                    return Mathf.Sin(TwoPi * wrapped);
            }
        }

        private static void DestroyClip(AudioClip clip)
        {
            if (clip != null)
            {
                Destroy(clip);
            }
        }

        private enum Waveform
        {
            Sine,
            Square,
            Triangle,
            Saw
        }
    }
}
