using UnityEngine;

namespace ProjectionMappingSample {
    // Stereoscopic content source for the projection-mapping rig. Sits on the
    // Content Camera. In SceneCameras mode it renders the 3D content twice per
    // frame through a parallel L/R eye pair (asymmetric off-axis frusta, zero
    // parallax at the wall plane) into two RenderTextures; in SbsTexture mode
    // the two "eyes" are the halves of a supplied side-by-side texture (e.g. a
    // VideoPlayer RenderTexture). PMSDKStereoComposer consumes the eyes per
    // projector and packs the warped result back into an SBS display frame.
    [RequireComponent(typeof(Camera))]
    public class PMSDKStereoContentRig : MonoBehaviour {
        public enum StereoSource { SceneCameras, SbsTexture }

        [Header("Stereo")]
        [Tooltip("Master switch. Enable together with the projectors' 3D SBS mode.")]
        public bool StereoActive = false;
        public KeyCode ToggleKey = KeyCode.F6;
        [Tooltip("Flips between SceneCameras and SbsTexture at runtime (None = disabled).")]
        public KeyCode SourceToggleKey = KeyCode.F7;
        public StereoSource Source = StereoSource.SceneCameras;
        [Tooltip("Interocular distance in scene units. Larger = stronger depth.")]
        public float EyeSeparation = 0.06f;
        [Tooltip("Distance from the content camera at which L/R coincide (the perceived wall plane). Nearer objects pop out, farther recede.")]
        public float ZeroParallaxDistance = 6f;
        [Tooltip("Side-by-side source texture (left eye in the left half) when Source is SbsTexture.")]
        public Texture SbsSource;
        [Tooltip("When true, an external driver (e.g. HeadTrackedStereoController) sets each eye camera's view/projection every frame; the rig skips its own symmetric off-axis shear. Use for head-tracked fish-tank stereo.")]
        public bool ExternalEyeMatrices = false;
        [Tooltip("Render the two eyes straight into the left/right halves of THIS display (viewport split), instead of into RenderTextures for a projector composer. Use for a direct SBS-3D display/projector (3D SBS mode stretches each half x2). No composer/RenderTexture needed.")]
        public bool DirectScreenSbs = false;

        private Camera baseCamera;
        private Camera leftCamera;
        private Camera rightCamera;
        private RenderTexture leftTexture;
        private RenderTexture rightTexture;
        private bool eyesLive;
        private Canvas[] managedCanvases;
        private float[] savedPlaneDistances;

        private void Awake() {
            baseCamera = GetComponent<Camera>();
        }

        private void OnDisable() {
            StereoActive = false;
            ApplyState();
            ReleaseEyes();
        }

        private void Update() {
            if (ToggleKey != KeyCode.None && Input.GetKeyDown(ToggleKey)) {
                StereoActive = !StereoActive;
                Debug.Log("[PMSDKStereoContentRig] Stereo " + (StereoActive ? "ON" : "OFF"));
            }
            if (SourceToggleKey != KeyCode.None && Input.GetKeyDown(SourceToggleKey)) {
                Source = Source == StereoSource.SceneCameras ? StereoSource.SbsTexture : StereoSource.SceneCameras;
                Debug.Log("[PMSDKStereoContentRig] Source: " + Source);
            }
            ApplyState();
        }

        public bool IsStereoReady() {
            if (!StereoActive) {
                return false;
            }
            if (Source == StereoSource.SbsTexture) {
                return SbsSource != null;
            }
            return leftTexture != null && rightTexture != null;
        }

        public Texture GetEyeTexture(bool rightEye) {
            if (Source == StereoSource.SbsTexture) {
                return SbsSource;
            }
            return rightEye ? rightTexture : leftTexture;
        }

        // Maps eye-source UVs: (scale.x, scale.y, offset.x, offset.y).
        public Vector4 GetEyeScaleOffset(bool rightEye) {
            if (Source == StereoSource.SbsTexture) {
                return new Vector4(0.5f, 1f, rightEye ? 0.5f : 0f, 0f);
            }
            return new Vector4(1f, 1f, 0f, 0f);
        }

        // Runtime eye cameras (created on demand). Null until the eye pair exists; call
        // EnsureEyeCameras() first when driving them externally (ExternalEyeMatrices).
        public Camera LeftEyeCamera { get { return leftCamera; } }
        public Camera RightEyeCamera { get { return rightCamera; } }

        // Force-create the SceneCameras eye pair + render targets so an external driver can grab
        // and matrix-drive them before stereo is toggled on.
        public void EnsureEyeCameras() {
            EnsureEyes();
        }

        private void ApplyState() {
            bool wantEyeCameras = StereoActive && Source == StereoSource.SceneCameras;
            if (wantEyeCameras) {
                EnsureEyes();
                UpdateEye(leftCamera, -0.5f * EyeSeparation, leftTexture, false);
                UpdateEye(rightCamera, 0.5f * EyeSeparation, rightTexture, true);
            } else {
                if (leftCamera != null) {
                    leftCamera.enabled = false;
                }
                if (rightCamera != null) {
                    rightCamera.enabled = false;
                }
            }
            // The mono content camera keeps feeding Projection_RT whenever the
            // eye pair is not rendering (classic pipeline + calibration).
            baseCamera.enabled = !wantEyeCameras;
            if (wantEyeCameras != eyesLive) {
                eyesLive = wantEyeCameras;
                SetCanvasesStereo(wantEyeCameras);
            }
        }

        // Screen Space - Camera canvases bound to the (now disabled) mono camera
        // stop rendering in stereo. Reparked as world-space rects at the
        // zero-parallax plane they render into BOTH eyes at wall depth, which is
        // exactly where flat UI belongs in stereo.
        private void SetCanvasesStereo(bool stereo) {
            if (stereo) {
                Canvas[] all = Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                System.Collections.Generic.List<Canvas> mine = new System.Collections.Generic.List<Canvas>();
                foreach (Canvas candidate in all) {
                    if (candidate.renderMode == RenderMode.ScreenSpaceCamera && candidate.worldCamera == baseCamera) {
                        mine.Add(candidate);
                    }
                }
                managedCanvases = mine.ToArray();
                savedPlaneDistances = new float[managedCanvases.Length];
                for (int i = 0; i < managedCanvases.Length; i++) {
                    Canvas canvas = managedCanvases[i];
                    savedPlaneDistances[i] = canvas.planeDistance;
                    RectTransform rect = canvas.GetComponent<RectTransform>();
                    canvas.renderMode = RenderMode.WorldSpace;
                    rect.position = baseCamera.transform.position + baseCamera.transform.forward * ZeroParallaxDistance;
                    rect.rotation = baseCamera.transform.rotation;
                    float frustumHeight = 2f * ZeroParallaxDistance * Mathf.Tan(baseCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                    float scale = frustumHeight / Mathf.Max(1f, rect.sizeDelta.y);
                    rect.localScale = new Vector3(scale, scale, scale);
                }
            } else if (managedCanvases != null) {
                for (int i = 0; i < managedCanvases.Length; i++) {
                    Canvas canvas = managedCanvases[i];
                    if (canvas == null) {
                        continue;
                    }
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = baseCamera;
                    canvas.planeDistance = savedPlaneDistances[i];
                }
                managedCanvases = null;
            }
        }

        private void EnsureEyes() {
            // Direct-to-screen SBS renders straight into display halves, so no eye RenderTextures.
            if (!DirectScreenSbs) {
                if (leftTexture == null) {
                    leftTexture = new RenderTexture(1920, 1080, 24);
                    leftTexture.name = "PMSDK_Stereo_L";
                    leftTexture.hideFlags = HideFlags.DontSave;
                }
                if (rightTexture == null) {
                    rightTexture = new RenderTexture(1920, 1080, 24);
                    rightTexture.name = "PMSDK_Stereo_R";
                    rightTexture.hideFlags = HideFlags.DontSave;
                }
            }
            if (leftCamera == null) {
                leftCamera = CreateEyeCamera("PMSDK Stereo Eye L");
            }
            if (rightCamera == null) {
                rightCamera = CreateEyeCamera("PMSDK Stereo Eye R");
            }
        }

        private Camera CreateEyeCamera(string cameraName) {
            GameObject go = new GameObject(cameraName);
            go.hideFlags = HideFlags.DontSave;
            go.transform.SetParent(transform, false);
            Camera cam = go.AddComponent<Camera>();
            cam.enabled = false;
            return cam;
        }

        private void UpdateEye(Camera eye, float lateralOffset, RenderTexture target, bool isRight) {
            eye.CopyFrom(baseCamera);
            if (DirectScreenSbs) {
                // Direct SBS-3D: render this eye into half the display; the projector's 3D SBS
                // mode stretches each half x2. No RenderTexture / composer required.
                eye.targetTexture = null;
                eye.rect = isRight ? new Rect(0.5f, 0f, 0.5f, 1f) : new Rect(0f, 0f, 0.5f, 1f);
            } else {
                eye.targetTexture = target;
                eye.rect = new Rect(0f, 0f, 1f, 1f);
            }
            eye.enabled = true;
            if (ExternalEyeMatrices) {
                // An external head-tracked driver sets worldToCameraMatrix/projectionMatrix (and
                // the eye transform) later in LateUpdate; skip the rig's own symmetric shear.
                return;
            }
            eye.transform.position = baseCamera.transform.position + baseCamera.transform.right * lateralOffset;
            eye.transform.rotation = baseCamera.transform.rotation;
            // Parallel eyes with an asymmetric frustum: shift the projection so
            // both eyes agree at ZeroParallaxDistance (toe-in would introduce
            // vertical parallax at the frame edges).
            Matrix4x4 projection = baseCamera.projectionMatrix;
            projection.m02 = projection.m02 - projection.m00 * lateralOffset / Mathf.Max(0.01f, ZeroParallaxDistance);
            eye.projectionMatrix = projection;
        }

        private void ReleaseEyes() {
            if (leftCamera != null) {
                Destroy(leftCamera.gameObject);
                leftCamera = null;
            }
            if (rightCamera != null) {
                Destroy(rightCamera.gameObject);
                rightCamera = null;
            }
            if (leftTexture != null) {
                leftTexture.Release();
                Destroy(leftTexture);
                leftTexture = null;
            }
            if (rightTexture != null) {
                rightTexture.Release();
                Destroy(rightTexture);
                rightTexture = null;
            }
        }
    }
}
