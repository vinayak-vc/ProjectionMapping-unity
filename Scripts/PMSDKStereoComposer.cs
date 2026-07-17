using UnityEngine;
using vxpmsdk.Components;

namespace ProjectionMappingSample {
    // Per-projector stereo output stage. Renders the projector's EXISTING warp
    // surface twice per frame — once per eye, swapping only the source texture
    // via a MaterialPropertyBlock — and packs the two fully warped/blended
    // images into a side-by-side frame for the projector's 3D SBS mode. Because
    // the same surface, corner pin, and edge blend render both eyes, the
    // calibration applies to both eyes by construction and nothing needs to be
    // kept in sync. While calibration mode is active (or the surface material
    // is taken over by a test pattern / Gray-code sweep) the composer degrades
    // gracefully: calibration mode gets full mono passthrough; a material
    // takeover gets the pattern packed identically into both halves, so sweeps
    // still decode with the projector left in 3D mode.
    [RequireComponent(typeof(Camera))]
    public class PMSDKStereoComposer : MonoBehaviour {
        public PMSDKStereoContentRig Rig;
        public MeshRenderer Surface;

        private Camera displayCamera;
        private Camera eyeCamera;
        private RenderTexture leftWarped;
        private RenderTexture rightWarped;
        private Material packMaterial;
        private MaterialPropertyBlock propertyBlock;
        private Material sliceMaterial;
        private Vector4 sliceScaleOffset;
        private PMSDKCalibrationManager manager;
        private int savedCullingMask;
        private bool composing;

        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int MainTexStId = Shader.PropertyToID("_MainTex_ST");
        private static readonly int LeftTexId = Shader.PropertyToID("_LeftTex");
        private static readonly int RightTexId = Shader.PropertyToID("_RightTex");
        private static readonly int MonoId = Shader.PropertyToID("_Mono");

        private void Awake() {
            displayCamera = GetComponent<Camera>();
            propertyBlock = new MaterialPropertyBlock();
            manager = Object.FindFirstObjectByType<PMSDKCalibrationManager>(FindObjectsInactive.Include);
        }

        private void Start() {
            // Captured at startup, before any test pattern or sweep can swap it.
            if (Surface != null) {
                sliceMaterial = Surface.sharedMaterial;
                sliceScaleOffset = sliceMaterial != null && sliceMaterial.HasProperty(MainTexStId)
                    ? sliceMaterial.GetVector(MainTexStId)
                    : new Vector4(1f, 1f, 0f, 0f);
            }
        }

        private void OnDisable() {
            StopComposing();
            ReleaseResources();
        }

        private void LateUpdate() {
            bool calibrating = manager != null && manager.CalibrationMode;
            bool want = Rig != null && Rig.IsStereoReady() && Surface != null && !calibrating;
            if (!want) {
                StopComposing();
                return;
            }
            EnsureResources();
            if (!composing) {
                savedCullingMask = displayCamera.cullingMask;
                displayCamera.cullingMask = 0;
                composing = true;
            }

            bool takeover = Surface.sharedMaterial != sliceMaterial;
            if (takeover) {
                // Test pattern / Gray-code sweep owns the surface: render it
                // unmodified and show it to both eyes.
                RenderEye(leftWarped, false, false);
                packMaterial.SetFloat(MonoId, 1f);
            } else {
                RenderEye(leftWarped, true, false);
                RenderEye(rightWarped, true, true);
                packMaterial.SetFloat(MonoId, 0f);
            }
            packMaterial.SetTexture(LeftTexId, leftWarped);
            packMaterial.SetTexture(RightTexId, rightWarped);
        }

        private void RenderEye(RenderTexture target, bool overrideTexture, bool rightEye) {
            if (overrideTexture) {
                Vector4 eye = Rig.GetEyeScaleOffset(rightEye);
                Vector4 combined = new Vector4(
                    eye.x * sliceScaleOffset.x,
                    eye.y * sliceScaleOffset.y,
                    eye.z + eye.x * sliceScaleOffset.z,
                    eye.w + eye.y * sliceScaleOffset.w);
                propertyBlock.SetTexture(MainTexId, Rig.GetEyeTexture(rightEye));
                propertyBlock.SetVector(MainTexStId, combined);
                Surface.SetPropertyBlock(propertyBlock);
            }

            eyeCamera.transform.SetPositionAndRotation(displayCamera.transform.position, displayCamera.transform.rotation);
            eyeCamera.orthographic = displayCamera.orthographic;
            eyeCamera.orthographicSize = displayCamera.orthographicSize;
            eyeCamera.nearClipPlane = displayCamera.nearClipPlane;
            eyeCamera.farClipPlane = displayCamera.farClipPlane;
            eyeCamera.cullingMask = 1 << Surface.gameObject.layer;
            eyeCamera.clearFlags = CameraClearFlags.SolidColor;
            eyeCamera.backgroundColor = Color.black;
            eyeCamera.targetTexture = target;
            eyeCamera.Render();
            eyeCamera.targetTexture = null;

            if (overrideTexture) {
                Surface.SetPropertyBlock(null);
            }
        }

        private void OnRenderImage(RenderTexture source, RenderTexture destination) {
            if (composing && packMaterial != null) {
                Graphics.Blit(null, destination, packMaterial);
            } else {
                Graphics.Blit(source, destination);
            }
        }

        private void EnsureResources() {
            if (leftWarped == null) {
                leftWarped = new RenderTexture(1920, 1080, 24);
                leftWarped.name = "PMSDK_Warped_L_" + name;
                leftWarped.hideFlags = HideFlags.DontSave;
            }
            if (rightWarped == null) {
                rightWarped = new RenderTexture(1920, 1080, 24);
                rightWarped.name = "PMSDK_Warped_R_" + name;
                rightWarped.hideFlags = HideFlags.DontSave;
            }
            if (packMaterial == null) {
                packMaterial = new Material(Shader.Find("PMSDK/StereoPack"));
                packMaterial.hideFlags = HideFlags.DontSave;
            }
            if (eyeCamera == null) {
                GameObject go = new GameObject("PMSDK Stereo Eye Renderer");
                go.hideFlags = HideFlags.DontSave;
                go.transform.SetParent(transform, false);
                eyeCamera = go.AddComponent<Camera>();
                eyeCamera.enabled = false;
            }
        }

        private void StopComposing() {
            if (composing) {
                displayCamera.cullingMask = savedCullingMask;
                composing = false;
                if (Surface != null) {
                    Surface.SetPropertyBlock(null);
                }
            }
        }

        private void ReleaseResources() {
            if (eyeCamera != null) {
                Destroy(eyeCamera.gameObject);
                eyeCamera = null;
            }
            if (leftWarped != null) {
                leftWarped.Release();
                Destroy(leftWarped);
                leftWarped = null;
            }
            if (rightWarped != null) {
                rightWarped.Release();
                Destroy(rightWarped);
                rightWarped = null;
            }
            if (packMaterial != null) {
                Destroy(packMaterial);
                packMaterial = null;
            }
        }
    }
}
