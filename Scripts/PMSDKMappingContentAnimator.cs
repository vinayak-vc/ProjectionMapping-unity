using System.Collections.Generic;
using UnityEngine;

namespace ProjectionMappingSample
{
    /// <summary>
    /// Demo "mapping content" for the object-mapping scene. Drives the emissive colour
    /// of each part of the target object so the projection reads as content wrapped onto
    /// the physical geometry (not just a lit model): a hue wave sweeping across parts,
    /// a global brightness pulse, and a vertical reveal sweep. Pure visual demo content
    /// — the SDK's job (projector pose, warp/blend registration, calibration) is what
    /// makes it land on the real object.
    /// </summary>
    public class PMSDKMappingContentAnimator : MonoBehaviour
    {
        [Tooltip("Root of the target object; all child MeshRenderers are animated.")]
        public Transform Target;

        [Header("Hue wave")]
        public float HueScrollSpeed = 0.15f;
        [Tooltip("Hue offset per part index, cycles a rainbow across the parts.")]
        public float HuePerPart = 0.08f;

        [Header("Brightness")]
        public float PulseSpeed = 1.2f;
        public float MinBrightness = 0.4f;
        public float MaxBrightness = 1.6f;

        [Header("Reveal sweep (world Y)")]
        public bool RevealSweep = true;
        public float SweepSpeed = 0.6f;
        public float SweepSoftness = 0.6f;

        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly int BaseColorId = Shader.PropertyToID("_Color");

        private readonly List<Renderer> parts = new List<Renderer>();
        private MaterialPropertyBlock mpb;
        private float minY, maxY;

        private void OnEnable()
        {
            mpb = new MaterialPropertyBlock();
            Collect();
        }

        private void Collect()
        {
            parts.Clear();
            Transform root = Target != null ? Target : transform;
            root.GetComponentsInChildren(true, parts);

            minY = float.MaxValue; maxY = float.MinValue;
            foreach (var r in parts)
            {
                minY = Mathf.Min(minY, r.bounds.min.y);
                maxY = Mathf.Max(maxY, r.bounds.max.y);
            }
            if (maxY <= minY) maxY = minY + 1f;
        }

        private void Update()
        {
            if (parts.Count == 0) { Collect(); if (parts.Count == 0) return; }

            float t = Time.time;
            float pulse = Mathf.Lerp(MinBrightness, MaxBrightness, 0.5f * (1f + Mathf.Sin(t * PulseSpeed)));
            float sweepY = RevealSweep
                ? Mathf.Lerp(minY, maxY, Mathf.Repeat(t * SweepSpeed, 1f))
                : maxY;

            for (int i = 0; i < parts.Count; i++)
            {
                var r = parts[i];
                if (r == null) continue;

                float hue = Mathf.Repeat(t * HueScrollSpeed + i * HuePerPart, 1f);
                Color rgb = Color.HSVToRGB(hue, 0.85f, 1f);

                float reveal = 1f;
                if (RevealSweep)
                {
                    float partY = r.bounds.center.y;
                    reveal = Mathf.Clamp01((sweepY - partY) / Mathf.Max(0.01f, SweepSoftness) + 0.5f);
                }

                Color emission = rgb * pulse * reveal;
                r.GetPropertyBlock(mpb);
                mpb.SetColor(EmissionColorId, emission);
                mpb.SetColor(BaseColorId, rgb * 0.15f * reveal); // slight base tint so unlit parts aren't pure black
                r.SetPropertyBlock(mpb);
            }
        }
    }
}
