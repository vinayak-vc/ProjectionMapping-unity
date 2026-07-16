using UnityEngine;
using UnityEngine.UI;

using vxpmsdk;
using vxpmsdk.Core;

using Projector = vxpmsdk.Core.Projector;

namespace ProjectionMappingSample
{
    public class GuidedProjectionDemo : MonoBehaviour
    {
        [Header("Setup")]
        public MeshFilter curvedScreenMeshFilter;
        public Camera projectorA;
        public Camera projectorB;

        [Header("Wizard UI")]
        public Text titleText;
        public Text descText;
        public Button nextButton;
        public Button backButton;

        private vxpmsdk.Core.Mesh pmsdkMesh;
        private BlendConfig blendConfig;
        private WarpNode rootWarpNode;
        private Projector projA;
        private Projector projB;

        private UnityEngine.Mesh originalMesh;
        private UnityEngine.Mesh displayMesh;
        
        private pmsdk_vertex_t[] originalVertices;
        private uint[] originalIndices;

        private int currentStep = 0;

        private string[] stepTitles = {
            "Step 1: The Physical Screen",
            "Step 2: Virtual Projectors",
            "Step 3: Geometry Warping",
            "Step 4: Edge Blending",
            "Step 5: Interactive Rendering"
        };

        private string[] stepDescriptions = {
            "This mesh represents your physical screen. You must build your real-world screen, create an exact 3D model of it, and import it into Unity. (We are using a placeholder quad).",
            "We place virtual Cameras in Unity at the exact relative positions as your physical projectors. The SDK does not detect them via HDMI ports; you must map them manually.",
            "Unity hands the 3D mesh to the C++ SDK. The SDK runs complex math to deform the mesh, counteracting the optical distortion of the projector on a curved surface.",
            "Where Projector A and B overlap, the SDK calculates precise alpha gradients. This makes the intersection completely seamless.",
            "To render a game: Your interactive game (Characters, Terrain) is captured by a 'Main Camera' into a Render Texture. That texture is painted onto this warped mesh. The stationary Projector Cameras only look at this warped mesh!"
        };

        void Start()
        {
            if (curvedScreenMeshFilter == null || projectorA == null || projectorB == null)
            {
                Debug.LogError("GuidedProjectionDemo: Missing Setup references in inspector!");
                return;
            }

            if (titleText == null || descText == null || nextButton == null || backButton == null)
            {
                Debug.LogError("GuidedProjectionDemo: Missing UI references in inspector!");
                return;
            }

            originalMesh = curvedScreenMeshFilter.sharedMesh;
            displayMesh = Instantiate(originalMesh);
            curvedScreenMeshFilter.mesh = displayMesh;

            ExtractUnityMesh();
            SetupSDK();
            
            nextButton.onClick.AddListener(NextStep);
            backButton.onClick.AddListener(PrevStep);
            
            UpdateWizardState();
        }

        public void NextStep()
        {
            currentStep = Mathf.Min(stepTitles.Length - 1, currentStep + 1);
            UpdateWizardState();
        }

        public void PrevStep()
        {
            currentStep = Mathf.Max(0, currentStep - 1);
            UpdateWizardState();
        }

        private void ExtractUnityMesh()
        {
            Vector3[] unityVerts = originalMesh.vertices;
            Vector3[] unityNormals = originalMesh.normals;
            Vector2[] unityUVs = originalMesh.uv;
            int[] unityIndices = originalMesh.triangles;

            originalVertices = new pmsdk_vertex_t[unityVerts.Length];
            for (int i = 0; i < unityVerts.Length; i++)
            {
                originalVertices[i] = new pmsdk_vertex_t
                {
                    position = new pmsdk_vec3_t { x = unityVerts[i].x, y = unityVerts[i].y, z = unityVerts[i].z },
                    normal = new pmsdk_vec3_t { x = unityNormals[i].x, y = unityNormals[i].y, z = unityNormals[i].z },
                    uv = new pmsdk_vec2_t { x = unityUVs != null && unityUVs.Length > 0 ? unityUVs[i].x : 0, y = unityUVs != null && unityUVs.Length > 0 ? unityUVs[i].y : 0 },
                    color = new pmsdk_vec4_t { x = 1, y = 1, z = 1, w = 1 }
                };
            }

            originalIndices = new uint[unityIndices.Length];
            for (int i = 0; i < unityIndices.Length; i++)
            {
                originalIndices[i] = (uint)unityIndices[i];
            }
        }

        private void SetupSDK()
        {
            pmsdkMesh = new vxpmsdk.Core.Mesh();
            pmsdkMesh.SetVertices(originalVertices);
            pmsdkMesh.SetIndices(originalIndices);

            projA = new Projector();
            projA.SetThrowRatio(1.2f);
            projB = new Projector();
            projB.SetThrowRatio(1.2f);

            blendConfig = new BlendConfig();
            blendConfig.BlackLevel = 0.05f;
            blendConfig.RightEdge.Size = 0.2f;
            blendConfig.LeftEdge.Size = 0.2f;

            rootWarpNode = new WarpNode();
        }

        private void UpdateWizardState()
        {
            titleText.text = stepTitles[currentStep];
            descText.text = stepDescriptions[currentStep];

            backButton.interactable = currentStep > 0;
            nextButton.interactable = currentStep < stepTitles.Length - 1;

            bool applyWarp = currentStep >= 2;
            bool applyBlend = currentStep >= 3;

            // Reset mesh
            pmsdkMesh.SetVertices(originalVertices);

            if (applyBlend)
            {
                pmsdk_vertex_t[] verts = pmsdkMesh.GetVertices();
                for (int i = 0; i < verts.Length; i++)
                {
                    float u = verts[i].uv.x;
                    float alpha = blendConfig.Evaluate(u, 0.5f);
                    verts[i].color = new pmsdk_vec4_t { x=alpha, y=alpha, z=alpha, w=1 };
                }
                pmsdkMesh.SetVertices(verts);
            }

            if (applyWarp)
            {
                using (var warpedMesh = new vxpmsdk.Core.Mesh())
                {
                    rootWarpNode.ProcessMesh(pmsdkMesh, warpedMesh);
                    ApplyToUnityMesh(warpedMesh);
                }
            }
            else
            {
                ApplyToUnityMesh(pmsdkMesh);
            }
            
            curvedScreenMeshFilter.mesh = displayMesh;
        }

        private void ApplyToUnityMesh(vxpmsdk.Core.Mesh sdkMesh)
        {
            pmsdk_vertex_t[] verts = sdkMesh.GetVertices();
            uint[] indices = sdkMesh.GetIndices();

            Vector3[] unityVerts = new Vector3[verts.Length];
            Color[] unityColors = new Color[verts.Length];

            for (int i = 0; i < verts.Length; i++)
            {
                unityVerts[i] = new Vector3(verts[i].position.x, verts[i].position.y, verts[i].position.z);
                unityColors[i] = new Color(verts[i].color.x, verts[i].color.y, verts[i].color.z, verts[i].color.w);
            }

            int[] unityIndices = new int[indices.Length];
            for (int i = 0; i < indices.Length; i++)
            {
                unityIndices[i] = (int)indices[i];
            }

            displayMesh.Clear();
            displayMesh.vertices = unityVerts;
            displayMesh.triangles = unityIndices;
            displayMesh.colors = unityColors;
            displayMesh.RecalculateNormals();
        }

        void OnDestroy()
        {
            pmsdkMesh?.Dispose();
            projA?.Dispose();
            projB?.Dispose();
            blendConfig?.Dispose();
            rootWarpNode?.Dispose();
        }
    }
}
