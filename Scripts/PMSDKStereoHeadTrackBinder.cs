using UnityEngine;
using vxholotrack;

namespace ProjectionMappingSample {
    // Runtime glue between the stereo content rig and the head-tracked stereo
    // controller. PMSDKStereoContentRig owns its eye cameras (created on demand at
    // play time), and HeadTrackedStereoController drives their off-axis matrices
    // from the tracked head. This connects the two once the rig has created the
    // eye pair, and puts the rig into external-eye-matrix stereo so the controller
    // is authoritative for the eye view/projection.
    //
    // It ALSO head-tracks the rig's MAIN (mono) camera when stereo is OFF: in
    // SceneCameras stereo the eye pair renders and the base camera is disabled, but
    // in normal/mono mode the rig re-enables the base camera and — without this — it
    // would sit still (only the disabled eye cams were being driven). Here the base
    // camera gets the same off-axis head-tracking as HeadTrackedCameraController so
    // parallax works in both modes. Runs after the rig (which toggles the cameras in
    // Update); this LateUpdate then supplies the matrices.
    [DefaultExecutionOrder(50)]
    public class PMSDKStereoHeadTrackBinder : MonoBehaviour {
        [SerializeField] private PMSDKStereoContentRig rig;
        [SerializeField] private HeadTrackedStereoController controller;

        [Header("Mono (stereo-off) head tracking — optional; auto-discovered if unset")]
        [SerializeField] private PMHTHeadTracker tracker;
        [SerializeField] private HeadTrackingDisplaySurface surface;

        public PMSDKStereoContentRig Rig { get { return rig; } set { rig = value; } }
        public HeadTrackedStereoController Controller { get { return controller; } set { controller = value; } }

        private Camera baseCamera;

        private void Start() {
            if (rig == null || controller == null) {
                Debug.LogError("[PMSDKStereoHeadTrackBinder] Rig or Controller not assigned.", this);
                return;
            }
            // Head-tracked stereo: the rig renders both eyes into its eye textures,
            // but the controller (not the rig's symmetric shear) supplies the
            // per-eye off-axis view/projection. Boots in stereo; F6 toggles to mono,
            // where LateUpdate below head-tracks the (re-enabled) base camera.
            rig.Source = PMSDKStereoContentRig.StereoSource.SceneCameras;
            rig.ExternalEyeMatrices = true;
            rig.StereoActive = true;
            rig.EnsureEyeCameras();
            controller.SetEyeCameras(rig.LeftEyeCamera, rig.RightEyeCamera);

            baseCamera = rig.GetComponent<Camera>();
            if (tracker == null) {
                tracker = Object.FindFirstObjectByType<PMHTHeadTracker>();
            }
            if (surface == null) {
                surface = Object.FindFirstObjectByType<HeadTrackingDisplaySurface>();
            }
        }

        private void LateUpdate() {
            // Only drive the mono camera when the eye pair is NOT rendering. In stereo
            // the base camera is disabled and HeadTrackedStereoController owns the eyes.
            if (rig == null || baseCamera == null || tracker == null || surface == null) {
                return;
            }
            if (rig.StereoActive && rig.Source == PMSDKStereoContentRig.StereoSource.SceneCameras) {
                return;
            }
            if (!tracker.HasViewer) {
                return;
            }

            Vector3 eye = tracker.HeadPositionWorld;
            surface.GetCorners(out Vector3 bl, out Vector3 br, out Vector3 tl);
            if (tracker.TryComputeOffAxis(bl, br, tl, eye,
                                          baseCamera.nearClipPlane, baseCamera.farClipPlane,
                                          out Matrix4x4 view, out Matrix4x4 projection)) {
                // Matrices fully drive rendering; the base camera transform is left
                // alone so the (child) eye cameras are not dragged.
                baseCamera.worldToCameraMatrix = view;
                baseCamera.projectionMatrix = projection;
            }
        }

        private void OnDisable() {
            if (baseCamera != null) {
                baseCamera.ResetWorldToCameraMatrix();
                baseCamera.ResetProjectionMatrix();
            }
        }
    }
}
