using UnityEngine;
using vxholotrack;

namespace ProjectionMappingSample {
    // Runtime glue between the stereo content rig and the head-tracked stereo
    // controller. PMSDKStereoContentRig owns its eye cameras (created on demand at
    // play time), and HeadTrackedStereoController drives their off-axis matrices
    // from the tracked head. This connects the two once the rig has created the
    // eye pair, and puts the rig into external-eye-matrix stereo so the controller
    // is authoritative for the eye view/projection. Runs after the rig.
    [DefaultExecutionOrder(50)]
    public class PMSDKStereoHeadTrackBinder : MonoBehaviour {
        [SerializeField] private PMSDKStereoContentRig rig;
        [SerializeField] private HeadTrackedStereoController controller;

        public PMSDKStereoContentRig Rig { get { return rig; } set { rig = value; } }
        public HeadTrackedStereoController Controller { get { return controller; } set { controller = value; } }

        private void Start() {
            if (rig == null || controller == null) {
                Debug.LogError("[PMSDKStereoHeadTrackBinder] Rig or Controller not assigned.", this);
                return;
            }
            // Head-tracked stereo: the rig renders both eyes into its eye textures,
            // but the controller (not the rig's symmetric shear) supplies the
            // per-eye off-axis view/projection.
            rig.Source = PMSDKStereoContentRig.StereoSource.SceneCameras;
            rig.ExternalEyeMatrices = true;
            rig.StereoActive = true;
            rig.EnsureEyeCameras();
            controller.SetEyeCameras(rig.LeftEyeCamera, rig.RightEyeCamera);
        }
    }
}
