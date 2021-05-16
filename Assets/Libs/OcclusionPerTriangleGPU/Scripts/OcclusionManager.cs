//--------------------------------------------------------------------
// Created by Alexis Bacot - 2021 - www.alexisbacot.com
// Ported / extracted from Garrett Johnson https://github.com/gkjohnson/unity-rendering-investigation
//--------------------------------------------------------------------
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Runtime.InteropServices;
using UnityEngine.Rendering;

public struct boolMarshal
{
    [MarshalAs(UnmanagedType.Bool)]
    public byte boolean;
}

//--------------------------------------------------------------------
namespace OcclusionPerTriangleGPU
{
    //--------------------------------------------------------------------
    /// <summary>
    /// Using a custom (disabled) camera this will check the visibility of all polygons from a given list of meshes
    /// It uses a render texture to draw the meshes procedurally over some frames (or instantly) to know which polygons are visibles
    /// It then uses compute shader (2x kernels) to parse the render texture and extract a list of visible triangle indexes
    /// 
    /// Advantages:
    /// - Can place and setup the occlusion camera easily (change FoV, far plane, etc.)
    /// - No baking necessary, can work with every renderer, static or not
    /// - Most computations are done on the GPU, using the built-in unity procedural rendering + some compute shaders to finish the job
    /// - Super fast, but visibility is not triangle perfect, especially if you have high density polygons very far from the occlusion camera
    /// 
    /// Limitations:
    /// - The limit in render texture resolution will prevent you from computing infinetely precise visibility if they have high density and are far from the camera
    /// - If just very small part of a triangle is visible the entire triangle will be marked visible
    /// - Impossible to know specifically which object are occluding, you just know that a triangle is visible or not
    /// - Once the visibility check has been done, you still need to figure out which object are related to this triangle (should be easy!)
    /// 
    /// Two parameters are important to correctly setup:
    /// - resolutionRenderTex : a high resolution is needed if you have objects with small polygons far from the camera
    /// because if your resolution is too low then multiple polygons will draw to the same pixel in the render texture, but only one will be marked as visible
    /// 
    /// - nbTrianglesMaxPerFrame : at 1000 then each frame the coroutine will draw 1000 polygons into the render texture. 
    /// If you are checking the visibility for total polygon of 100k then the rendering of the rendertexture will take 100 frames (100k / 1k)
    /// If you set drawInOneFrame to true then nbTrianglesMaxPerFrame is ignored and everything is drawn in one go
    /// 
    /// 1) First it needs to be Init()
    /// 2) Check IsReadyToComputeVisibility before usage (it's an async process)
    /// 3) Use CheckVisiblityAsync with a list of meshes that you want to check visibility on
    /// 4) Needs to be Dispose(true) at the end because of compute buffers 
    /// 
    /// If you want to avoid using the compute shaders (they require shader model 5.0) you can parse the render texture manually on the CPU (need to convert it to a Texture2D first)
    /// and then convert the color back into the triangle id using the same trick present in the ComputeCountVisibleTriangles.compute Compute Shader
    /// 
    /// </summary>
    public class OcclusionManager : MonoBehaviour
    {
        //--------------------------------------------------------------------
        private struct PerModelAttribute
        {
            public Matrix4x4 matrixLocalToWorld; // The shader that draws the triangle Idx into the render texture needs the localToWorld matrix of each object
        }

        //--------------------------------------------------------------------
        private enum EnumOccState
        {
            None,
            InitDone,
            Busy_DrawingVisibilityOrFetchingResults,
            Ready_HasOcclusionResults,
        }

        //--------------------------------------------------------------------
        [Header("Settings")]
        public int resolutionRenderTex = 1024;
        public bool drawInOneFrame = false;
        public int nbTrianglesMaxPerFrame = 1000;

        [Header("Links")]
        public Camera camOcclusion;
        public Material matToDrawTriangleIds;
        public ComputeShader compute;

        [Header("Debug Gizmo")]
        public bool isDrawGizmo = true;
        public bool isDrawGizmoOnlyIfSelected = false;
        public float sizeDebugGizmo = 0.04f;
        public bool isShowGizmoAtEachVertex = true;
        public bool isShowGizmoAtPolyCenter = true;
        public Material matDebugOcclusionRenderTex;
        public bool isDebugColorByModel = true;
        public Color[] allColorsDebugByModel;

        [Header("Debug Output")]
        public bool isDebugNbTriangles = true;
        public bool isDebugAllTriIdxVisible = false;
        public bool isDebugTimeToCompute = true;

        // Use this before calling CheckVisiblityAsync to make sure all is ready
        public bool IsReadyToComputeVisibility { get { return _stateCurrent == EnumOccState.InitDone || _stateCurrent == EnumOccState.Ready_HasOcclusionResults; } }

        // Result of the occlusion check
        [System.NonSerialized] public uint[] dataAllTriIdxVisible;
        [System.NonSerialized] public int nbTriangleVisible;

        // Internal
        private EnumOccState _stateCurrent = EnumOccState.None;
        private RenderTexture _renderTexOcclusion;
        private List<Mesh> meshes;
        private ImportStructuredBufferMesh.Point[] _allVertexData;
        private List<PerModelAttribute> _allPerModelAttributes;
        private Coroutine _occlusionCoroutine;
        // Compute Buffers & kernels
        private ComputeBuffer _cbAllVerticesInfos, _cbAllPerModelAttributes;
        private ComputeBuffer _cbAllVisibilityInfos, _cbAllTriIdxVisible;
        private const int ACCUM_KERNEL = 0;
        private const int MAP_KERNEL = 1;
        private const int CLEANVISIBILITY_KERNEL = 2;
        private int _nbTrianglesMaxPerFrameFinal;
        private uint[] _allTrianglesToZero;

        //--------------------------------------------------------------------
        public void Init()
        {
            // Create render texture (can also be done in the editor)
            _renderTexOcclusion = new RenderTexture(resolutionRenderTex, resolutionRenderTex, 16, RenderTextureFormat.ARGB32);
            _renderTexOcclusion.enableRandomWrite = true;
            _renderTexOcclusion.anisoLevel = 0;
            _renderTexOcclusion.antiAliasing = 1;
            _renderTexOcclusion.autoGenerateMips = false;
            _renderTexOcclusion.filterMode = FilterMode.Point;
            _renderTexOcclusion.useDynamicScale = false;
            _renderTexOcclusion.useMipMap = false;
            _renderTexOcclusion.wrapMode = TextureWrapMode.Clamp;

            _renderTexOcclusion.Create();

            // We might want to debug the render texture and display it at runtime to see what's going on
            if (matDebugOcclusionRenderTex) matDebugOcclusionRenderTex.mainTexture = _renderTexOcclusion;

            // Assign render texture to camera
            camOcclusion.targetTexture = _renderTexOcclusion;
            camOcclusion.enabled = false; // occlusion cam doesn't need to be enabled

            _stateCurrent = EnumOccState.InitDone;

            _allPerModelAttributes = new List<PerModelAttribute>();
            meshes = new List<Mesh>();
        }

        //--------------------------------------------------------------------
        /// <summary>
        /// Takes all MeshFilter and pack them into an array of Points (position, normal, modelid) to feed to buffers / shaders
        /// This is very costly(4.15ms for 38k triangles on my 2015 GPU), try to avoid doing it every time! Only do it for non static objects that moved
        /// </summary>
        public void PackAllMeshes(List<MeshFilter> allMeshesToCheck_)
        {
            // No renderers to process
            if (allMeshesToCheck_.Count <= 0)
            {
                Debug.LogError("[OcclusionPerTriangleGPU] PackAllMeshes ERROR we need some meshes to check visibility to");
                return;
            }

            ClearAndDispose(false);

            // Setup the meshes to process into lists for compute buffers
            for (int i = 0; i < allMeshesToCheck_.Count; i++)
            {
                meshes.Add(allMeshesToCheck_[i].sharedMesh);

                _allPerModelAttributes.Add(new PerModelAttribute()
                {
                    matrixLocalToWorld = allMeshesToCheck_[i].transform.localToWorldMatrix,
                });
            }

            // All Vertices Infos: Transform the list of meshes into a Compute Buffer of points (modelid, vertex pos, vertex normal)
            _allVertexData = ImportStructuredBufferMesh.ImportAllAndUnpack(meshes.ToArray(), ref _cbAllVerticesInfos);

            _nbTrianglesMaxPerFrameFinal = drawInOneFrame ? _cbAllVerticesInfos.count / 3 : nbTrianglesMaxPerFrame;

            // == Setup compute buffers & material to write visibility into the render texture

            // Per Model Attribute: Computer Buffer with localToWorldMatrix for each renderer
            _cbAllPerModelAttributes = new ComputeBuffer(_allPerModelAttributes.Count, Marshal.SizeOf(typeof(PerModelAttribute)), ComputeBufferType.Default);
            _cbAllPerModelAttributes.SetData(_allPerModelAttributes.ToArray());

            // Set the buffer in the material / shader to render the triangle ids
            matToDrawTriangleIds.SetBuffer("AllPerModelAttributes", _cbAllPerModelAttributes);
            matToDrawTriangleIds.SetBuffer("AllVerticesInfos", _cbAllVerticesInfos);

            // == Setup the compute shader, its two kerner and its compute buffer to parse the render texture and output a list of triangle idx that are visible
            // First setup the buffers to read the render texture and generate the visible triangle index

            // List of bools with all triangle idx, true if it's visible, false if not
            _cbAllVisibilityInfos = new ComputeBuffer(_cbAllVerticesInfos.count / 3, Marshal.SizeOf(typeof(bool)));

            // RESULT => List of triangle idx that are visible by our cam
            _cbAllTriIdxVisible = new ComputeBuffer(_cbAllVerticesInfos.count / 3, Marshal.SizeOf(typeof(uint)), ComputeBufferType.Append);
            _allTrianglesToZero = new uint[_cbAllVerticesInfos.count / 3];

            // Then setup the kernels that will be used to parse the render texture

            // ACCUM_KERNEL takes the render texture of visibility and fills _cbAllVisibilityInfos to true for triangle index where there is a color
            compute.SetBuffer(ACCUM_KERNEL, "_AllVisibilityInfos", _cbAllVisibilityInfos); // true/false list
            compute.SetTexture(ACCUM_KERNEL, "_idTex", _renderTexOcclusion); // render tex

            // MAP_KERNEL makes the result list, it takes _cbAllVisibilityInfos and creates _cbAllTriIdxVisible
            compute.SetBuffer(MAP_KERNEL, "_AllVisibilityInfos", _cbAllVisibilityInfos);
            compute.SetBuffer(MAP_KERNEL, "_AllTriIdxVisible", _cbAllTriIdxVisible);

            // CLEANVISIBILITY_KERNEL
            compute.SetBuffer(CLEANVISIBILITY_KERNEL, "_AllVisibilityInfos", _cbAllVisibilityInfos);
            compute.SetInt("_sizeTexWidth", _renderTexOcclusion.width);
        }

        //--------------------------------------------------------------------
        /// <summary>
        /// Checks the visibility of all meshes that were packed using PackAllMeshes
        /// </summary>
        public void CheckVisiblityAsync()
        {
            // Not Init yet, that's a problem! 
            if (_stateCurrent == EnumOccState.None)
            {
                Debug.LogError("[OcclusionPerTriangleGPU] CheckVisiblityAsync but wasn't Init!");
                return;
            }

            // Already processing, that's a problem!
            if (_stateCurrent == EnumOccState.Busy_DrawingVisibilityOrFetchingResults)
                return;

            // If coroutine still there we have a problem
            if (_occlusionCoroutine != null)
            {
                Debug.LogError("[OcclusionPerTriangleGPU] CheckVisiblityAsync ERROR state is " + _stateCurrent + " but coroutine still not null!");
                return;
            }

            _stateCurrent = EnumOccState.Busy_DrawingVisibilityOrFetchingResults;

            // We don't need to reset _cbAllVerticesInfos because it's set back to false in the shader! also i have no idea how to SetData with an array of bool because it's not blitable
            _cbAllTriIdxVisible.SetData(_allTrianglesToZero); // Otherwise some triangle from previous check will be set!

            // Start the coroutine that will draw over some frames + fetch the results 
            _occlusionCoroutine = StartCoroutine(DrawVisibleTrianglesAndComputeResult());
        }

        //--------------------------------------------------------------------
        // coroutine to draw triangles over many frames and to wait for the GPU when reading back the resulting triangle array
        IEnumerator DrawVisibleTrianglesAndComputeResult()
        {
            int totaltris = _cbAllVerticesInfos.count / 3;

            if (isDebugNbTriangles) Debug.Log("[OcclusionPerTriangleGPU] DrawVisibleTrianglesAndComputeResult totaltris: " + totaltris
                + " _nbTrianglesMaxPerFrameFinal: " + _nbTrianglesMaxPerFrameFinal);

            // Render totaltris triangles over several frames depending on _nbTrianglesMaxPerFrameFinal
            for (int i = 0; i < totaltris; i += _nbTrianglesMaxPerFrameFinal)
            {
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = _renderTexOcclusion; // We are now drawing into _renderTexOcclusion

                if (i == 0) GL.Clear(true, true, new Color32(0xFF, 0xFF, 0xFF, 0xFF)); // Clear it, white = nothing

                // Getting ready to draw some triangles
                GL.PushMatrix();
                GL.LoadIdentity();
                GL.modelview = camOcclusion.worldToCameraMatrix;
                GL.LoadProjectionMatrix(camOcclusion.projectionMatrix);
                {
                    matToDrawTriangleIds.SetInt("idOffset", i * 3); // Set the offset used to only draw some triangles per frame
                    matToDrawTriangleIds.SetPass(0);

                    // At this point matToDrawTriangleIds has the list of all points & other per model attributes and the right offset,
                    // we just need to draw procedurally (_nbTrianglesMaxPerFrameFinal * 3) vertices
                    // This will draw the triangle index that are visibile inside the render texture
                    // Each triangleIdx will be transformed into a color using tricky bitwise operation (right shift)
                    Graphics.DrawProceduralNow(MeshTopology.Triangles, _nbTrianglesMaxPerFrameFinal * 3, 1);
                }
                GL.PopMatrix();
                RenderTexture.active = prev;

                if (i + _nbTrianglesMaxPerFrameFinal < totaltris) yield return null;
            }

            // Parse the render texture on the GPU to output a list of all visible triangle idx
            _cbAllTriIdxVisible.SetCounterValue(0);
            compute.Dispatch(CLEANVISIBILITY_KERNEL, _renderTexOcclusion.width, _renderTexOcclusion.height, 1);
            compute.Dispatch(ACCUM_KERNEL, _renderTexOcclusion.width, _renderTexOcclusion.height, 1); // start kernel ACCUM_KERNEL can run in parallel on the entire texture
            compute.Dispatch(MAP_KERNEL, _cbAllVisibilityInfos.count, 1, 1); // start kernel MAP_KERNEL can run in parallel on the entire bool list

            // Note: if you want to avoid using the compute shaders (they require shader model 5.0) you can parse the render texture (need to convert it to a Texture2D first)
            // And then convert the color back into the triangle id using the same trick present in the ComputeCountVisibleTriangles.compute Compute Shader

            if (isDebugAllTriIdxVisible)
            {
                uint[] dataFromTriAppend = new uint[_cbAllTriIdxVisible.count];
                _cbAllTriIdxVisible.GetData(dataFromTriAppend);

                string strDebugAllTriVisible = "";

                for (int i = 0; i < dataFromTriAppend.Length; i++)
                {
                    if (dataFromTriAppend[i] == 0) continue; // at some point we reach 0 and no more triangle index are found in the array

                    strDebugAllTriVisible += dataFromTriAppend[i] + ", ";
                }

                Debug.Log("[OcclusionPerTriangleGPU] All Triangle Idx Visible: " + strDebugAllTriVisible);
            }

            // Compute shader finished but we need to request the data from the GPU, wait for the request to be done, and copy it into an array
            AsyncGPUReadbackRequest requestForVisibleTriIndexes = AsyncGPUReadback.Request(_cbAllTriIdxVisible);

            while (!requestForVisibleTriIndexes.done) yield return null; // wait a bit until request is done

            Unity.Collections.NativeArray<uint> nativeVisibleIdxTri = requestForVisibleTriIndexes.GetData<uint>(); // get the data into a native array
            dataAllTriIdxVisible = new uint[nativeVisibleIdxTri.Length];
            nativeVisibleIdxTri.CopyTo(dataAllTriIdxVisible); // get the data into an array (the native array is not persistent)

            // Determine the real number of triangle indexes in dataAllTriIdxVisible to avoid parsing the trail of 0
            for (int i = 0; i < dataAllTriIdxVisible.Length; i++)
            {
                // When we encounter idxTriangle 0 they are two possibilities : it's the real 0, or it's the start of the 0 trail in the index
                if (dataAllTriIdxVisible[i] == 0 && i < dataAllTriIdxVisible.Length - 1 && dataAllTriIdxVisible[i + 1] == 0)
                {
                    nbTriangleVisible = i; // we found the start of the 0 trail
                    break;
                }
            }

            _stateCurrent = EnumOccState.Ready_HasOcclusionResults; // now we have results!
            _occlusionCoroutine = null;
            // _renderTexOcclusion.DiscardContents(); // not needed if you use the render texture a lot (check visibility a lot)
        }

        //--------------------------------------------------------------------
        public void ClearAndDispose(bool isFinal)
        {
            meshes.Clear();
            _allPerModelAttributes.Clear();

            //_renderTexOcclusion.DiscardContents();
            if (isFinal) _renderTexOcclusion.Release();

            dataAllTriIdxVisible = null;

            if (_cbAllVerticesInfos != null) { _cbAllVerticesInfos.Release(); _cbAllVerticesInfos = null; }
            if (_cbAllPerModelAttributes != null) { _cbAllPerModelAttributes.Release(); _cbAllPerModelAttributes = null; }
            if (_cbAllTriIdxVisible != null) { _cbAllTriIdxVisible.Release(); _cbAllTriIdxVisible = null; }
            if (_cbAllVisibilityInfos != null) { _cbAllVisibilityInfos.Release(); _cbAllVisibilityInfos = null; }
        }

        //--------------------------------------------------------------------
        private void OnDrawGizmos()
        {
            if (isDrawGizmo && !isDrawGizmoOnlyIfSelected) DoGizmo();
        }

        //--------------------------------------------------------------------
        private void OnDrawGizmosSelected()
        {
            if (isDrawGizmo && isDrawGizmoOnlyIfSelected) DoGizmo();
        }

        //--------------------------------------------------------------------
        private void DoGizmo()
        {
            if (_stateCurrent != EnumOccState.Ready_HasOcclusionResults) return;

            Gizmos.color = Color.red;

            for (int i = 0; i < dataAllTriIdxVisible.Length; i++)
            {
                int idxTriangle = (int)dataAllTriIdxVisible[i];

                if (idxTriangle == 0) break; // We can stop as soon as we reach empty part of array

                // All vertex index are found from the idxTriangle:
                int idxV0 = idxTriangle * 3;
                int idxV1 = idxTriangle * 3 + 1;
                int idxV2 = idxTriangle * 3 + 2;

                // Position of each vertex
                Vector3 posLocal0 = _allVertexData[idxV0].vertex;
                Vector3 posLocal1 = _allVertexData[idxV1].vertex;
                Vector3 posLocal2 = _allVertexData[idxV2].vertex;

                int modelid = _allVertexData[idxV0].modelid;

                if (isDebugColorByModel) Gizmos.color = allColorsDebugByModel[modelid % allColorsDebugByModel.Length];

                if (isShowGizmoAtPolyCenter)
                {
                    Vector3 posLocalBarycenter = (posLocal0 + posLocal1 + posLocal2) / 3.0f;

                    Vector3 posWorldBarycenter = _allPerModelAttributes[modelid].matrixLocalToWorld.MultiplyPoint(posLocalBarycenter);

                    DebugTools.PointForGizmo(posWorldBarycenter, sizeDebugGizmo * 1.5f);
                }

                if (isShowGizmoAtEachVertex)
                {
                    Vector3 posWorld0 = _allPerModelAttributes[modelid].matrixLocalToWorld.MultiplyPoint(posLocal0);
                    Vector3 posWorld1 = _allPerModelAttributes[modelid].matrixLocalToWorld.MultiplyPoint(posLocal1);
                    Vector3 posWorld2 = _allPerModelAttributes[modelid].matrixLocalToWorld.MultiplyPoint(posLocal2);

                    DebugTools.PointForGizmo(posWorld0, sizeDebugGizmo);
                    DebugTools.PointForGizmo(posWorld1, sizeDebugGizmo);
                    DebugTools.PointForGizmo(posWorld2, sizeDebugGizmo);
                }
            }
        }

        //--------------------------------------------------------------------
    }

    //--------------------------------------------------------------------
}