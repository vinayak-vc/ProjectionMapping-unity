using UnityEngine;
using vxpmsdk;
using vxpmsdk.Core;
using static vxpmsdk.Core.Projector;

namespace ProjectionMappingSample
{
    [RequireComponent(typeof(MeshFilter))]
    public class ProjectionMappingSample : MonoBehaviour
    {
        [Header("Projector Settings")]
        [SerializeField] private float aspectRatio = 16.0f / 9.0f;
        [SerializeField] private float throwRatio = 1.2f;

        [Header("Output")]
        [SerializeField] private MeshFilter targetMeshFilter;

        private vxpmsdk.Core.Projector _projector;
        private WarpNode _rootNode;
        private vxpmsdk.Core.Mesh _pmsdkInputMesh;
        private vxpmsdk.Core.Mesh _pmsdkOutputMesh;

        void Start()
        {
            // 1. Initialize SDK Objects
            _projector = new vxpmsdk.Core.Projector();
            _projector.SetAspectRatio(aspectRatio);
            _projector.SetThrowRatio(throwRatio);

            _rootNode = new WarpNode();
            
            // We use the full namespace for the SDK Mesh to avoid conflict with UnityEngine.Mesh
            _pmsdkInputMesh = new vxpmsdk.Core.Mesh();
            _pmsdkOutputMesh = new vxpmsdk.Core.Mesh();

            // 2. Extract geometry from Unity's MeshFilter
            MeshFilter sourceFilter = GetComponent<MeshFilter>();
            if (sourceFilter != null && sourceFilter.sharedMesh != null)
            {
                UnityEngine.Mesh unityMesh = sourceFilter.sharedMesh;
                
                Vector3[] unityVerts = unityMesh.vertices;
                Vector3[] unityNormals = unityMesh.normals;
                Vector2[] unityUvs = unityMesh.uv;
                Color[] unityColors = unityMesh.colors;

                pmsdk_vertex_t[] sdkVertices = new pmsdk_vertex_t[unityVerts.Length];
                for (int i = 0; i < unityVerts.Length; i++)
                {
                    sdkVertices[i] = new pmsdk_vertex_t
                    {
                        position = unityVerts[i].ToNative(),
                        normal = (unityNormals != null && unityNormals.Length > i) ? unityNormals[i].ToNative() : new pmsdk_vec3_t(),
                        uv = (unityUvs != null && unityUvs.Length > i) ? unityUvs[i].ToNative() : new pmsdk_vec2_t(),
                        color = (unityColors != null && unityColors.Length > i) ? unityColors[i].ToNative() : new pmsdk_vec4_t { x = 1, y = 1, z = 1, w = 1 }
                    };
                }

                _pmsdkInputMesh.SetVertices(sdkVertices);
                
                int[] unityIndices = unityMesh.triangles;
                uint[] sdkIndices = new uint[unityIndices.Length];
                for (int i = 0; i < unityIndices.Length; i++)
                {
                    sdkIndices[i] = (uint)unityIndices[i];
                }
                
                _pmsdkInputMesh.SetIndices(sdkIndices);

                Debug.Log($"[vxpmsdk] Loaded {unityVerts.Length} vertices and {unityIndices.Length} indices into SDK input mesh.");
            }
            else
            {
                Debug.LogWarning("[vxpmsdk] No MeshFilter or sharedMesh found on the GameObject. Skipping mesh processing.");
                return;
            }

            // 3. Process mesh through warp hierarchy
            _rootNode.ProcessMesh(_pmsdkInputMesh, _pmsdkOutputMesh);
            
            Debug.Log($"[vxpmsdk] Successfully processed mesh through warp node. Output mesh has {_pmsdkOutputMesh.VertexCount} vertices and {_pmsdkOutputMesh.IndexCount} indices.");
            
            // NOTE: Future SDK updates will allow retrieving the processed vertices via `pmsdk_mesh_get_vertices` 
            // to update the targetMeshFilter's geometry. For now, this confirms the pipeline executes.
        }

        void OnDestroy()
        {
            // 4. Clean up unmanaged resources via IDisposable
            _pmsdkInputMesh?.Dispose();
            _pmsdkOutputMesh?.Dispose();
            _rootNode?.Dispose();
            _projector?.Dispose();
        }
    }
}
