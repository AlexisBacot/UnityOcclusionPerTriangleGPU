//--------------------------------------------------------------------
// Created by Alexis Bacot - 2021 - www.alexisbacot.com
// Ported / extracted from Garrett Johnson https://github.com/gkjohnson/unity-rendering-investigation
//--------------------------------------------------------------------
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Runtime.InteropServices;

//--------------------------------------------------------------------
/// <summary>
/// Using a custom (disabled) camera this will check the visibility of all polygons from a given list of renderers
/// It uses a render texture to draw the meshes procedurally over some frames to know which polygons are visibles
/// It then uses compute shader (2x kernels) to parse the render texture and extract a list of visible triangle indexes
/// 
/// Advantages:
/// - Can place and setup the occlusion camera easily
/// - No baking necessary, can work with every renderer, static or not
/// - All computations are done on the GPU, using the built-in unity procedural rendering + some compute shaders to finish the job
/// - Super fast, but visibility is not triangle perfect, especially if you have high density polygons far from the occlusion camera
/// 
/// Limitations:
/// - The limit in render texture resolution will prevent you from computing precise visibility if they have high density and are far from the camera
/// - If just very small part of a triangle is visible the entire triangle will be marked visible
/// 
/// Two parameters are important to correctly setup:
/// - resolutionRenderTex : a high resolution is needed if you have objects with small polygons far from the camera
/// because if your resolution is too low then multiple polygons will draw to the same pixel in the render texture, but only one will be marked as visible
/// 
/// - nbTrianglesMaxPerFrame : at 1000 then each frame the coroutine will draw 1000 polygons into the render texture. 
/// If you are checking the visibility for total polygon of 100k then the rendering of the rendertexture will take 100 frames (100k / 1k)
/// 
/// 1) First it needs to be Init()
/// 2) Check IsReadyToComputeVisibility before usage (it's an async process)
/// 3) Use CheckVisiblityAsync
/// 4) Needs to be Dispose() because of compute buffers 
/// 
/// Depending on your needs & number of poly you need to check you can easily make this process not async because it runs on the GPU and is pretty fast
/// 
/// If you want to avoid using the compute shaders (they require shader model 5.0) you can parse the render texture manually on the CPU (need to convert it to a Texture2D first)
/// and then convert the color back into the triangle id using the same trick present in the ComputeCountVisibleTriangles.compute Compute Shader
/// 
/// </summary>
public class OcclusionPerTriangleGPU : MonoBehaviour
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
    public int nbTrianglesMaxPerFrame = 1000;

    [Header("Links")]
    public Camera camOcclusion;
    public Material matToDrawTriangleIds;
    public ComputeShader compute;

    [Header("Debug")]
    public float sizeDebugPos = 0.04f;
    public bool isShowGizmoAtEachVertex = true;
    public bool isShowGizmoAtPolyCenter = true;
    public Material matDebugOcclusionRenderTex;
    public bool isDebugShowGizmoWhenResults = true;
    public bool isDebugNbTriangles = true;
    public bool isDebugAllTriIdxVisible = false;
    public bool isDebugColorByModel = false;
    public Color[] allColorsDebugByModel;

    // Use this before calling CheckVisiblityAsync to make sure all is ready
    public bool IsReadyToComputeVisibility { get { return _stateCurrent == EnumOccState.InitDone || _stateCurrent == EnumOccState.Ready_HasOcclusionResults; } }

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

    //--------------------------------------------------------------------
    public void Init()
    {
        // Create render texture (can also be done in the editor)
        _renderTexOcclusion = new RenderTexture(resolutionRenderTex, resolutionRenderTex, 16, RenderTextureFormat.ARGB32);
        _renderTexOcclusion.enableRandomWrite = true;
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
    public void CheckVisiblityAsync(List<Renderer> allRenderersToCheck_)
    {
        // == Multiple Sanity Check

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

        // No renderers to process
        if (allRenderersToCheck_.Count <= 0)
        {
            Debug.LogError("[OcclusionPerTriangleGPU] CheckVisiblityAsync ERROR we need some renderers to check visibility to");
            return;
        }

        _stateCurrent = EnumOccState.Busy_DrawingVisibilityOrFetchingResults;

        // == Free all buffers / lists if they exist
        Dispose();

        // == Setup compute buffers & material to write visibility into the render texture

        // Setup the renderers to process into lists for compute buffers
        foreach (Renderer renderer in allRenderersToCheck_)
        {
            meshes.Add(renderer.GetComponent<MeshFilter>().sharedMesh);

            _allPerModelAttributes.Add(new PerModelAttribute()
            {
                matrixLocalToWorld = renderer.transform.localToWorldMatrix,
            });
        }

        // All Vertices Infos: Transform the list of meshes into a Compute Buffer of points (modelid, vertex pos, vertex normal)
        _allVertexData = ImportStructuredBufferMesh.ImportAllAndUnpack(meshes.ToArray(), ref _cbAllVerticesInfos);

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

        // Then setup the kernels that will be used to parse the render texture

        // ACCUM_KERNEL takes the render texture of visibility and fills _cbAllVisibilityInfos to true for triangle index where there is a color
        compute.SetBuffer(ACCUM_KERNEL, "_AllVisibilityInfos", _cbAllVisibilityInfos); // true/false list
        compute.SetTexture(ACCUM_KERNEL, "_idTex", _renderTexOcclusion); // render tex

        // MAP_KERNEL makes the result list, it takes _cbAllVisibilityInfos and creates _cbAllTriIdxVisible
        compute.SetBuffer(MAP_KERNEL, "_AllVisibilityInfos", _cbAllVisibilityInfos);
        compute.SetBuffer(MAP_KERNEL, "_AllTriIdxVisible", _cbAllTriIdxVisible);

        // Start the coroutine that will draw over some frames + fetch the results 
        _occlusionCoroutine = StartCoroutine(DrawVisibleTrianglesAndComputeResult());
    }

    //--------------------------------------------------------------------
    // Coroutine that will procedurally draw the triangles using the camOcclusion into the _renderTexOcclusion and then use compute shader to create an array of visible triangle idx
    IEnumerator DrawVisibleTrianglesAndComputeResult()
    {
        int totaltris = _cbAllVerticesInfos.count / 3;

        if (isDebugNbTriangles) Debug.Log("[OcclusionPerTriangleGPU] DrawVisibleTrianglesAndComputeResult totaltris: " + totaltris + " nbTrianglesMaxPerFrame: " + nbTrianglesMaxPerFrame);

        // Render totaltris triangles over several frames depending on nbTrianglesMaxPerFrame
        for (int i = 0; i < totaltris; i += nbTrianglesMaxPerFrame)
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
                matToDrawTriangleIds.SetPass(0); // Correct pass

                // At this point matToDrawTriangleIds has the list of all points & other per model attributes and the right offset,
                // we just need to draw procedurally (nbTrianglesMaxPerFrame * 3) vertices
                // This will draw the triangle index that are visibile inside the render texture
                // Each triangleIdx will be transformed into a color using tricky bitwise operation (right shift)
                Graphics.DrawProceduralNow(MeshTopology.Triangles, nbTrianglesMaxPerFrame * 3, 1);
            }
            GL.PopMatrix();
            RenderTexture.active = prev;

            if (i + nbTrianglesMaxPerFrame < totaltris) yield return null;
        }

        // It seems that only one yield here can sometimes result in compute shader running on unfinished render texture
        yield return null; // *** Finished drawing all visible triangle idx into the occlusion render texture
        yield return null;

        // Parse the render texture on the GPU to output a list of all visible triangle idx
        _cbAllTriIdxVisible.SetCounterValue(0);
        compute.Dispatch(ACCUM_KERNEL, _renderTexOcclusion.width, _renderTexOcclusion.height, 1); // start kernel ACCUM_KERNEL can run in parallel on the entire texture
        compute.Dispatch(MAP_KERNEL, _cbAllVisibilityInfos.count, 1, 1); // start kernel MAP_KERNEL can run in parallel on the entire bool list

        // Note: if you want to avoid using the compute shaders (they require shader model 5.0) you can parse the render texture (need to convert it to a Texture2D first)
        // And then convert the color back into the triangle id using the same trick present in the ComputeCountVisibleTriangles.compute Compute Shader

        yield return new WaitForEndOfFrame(); // *** Finished running the compute shader, our result is stored inside our compute buffer _cbAllTriIdxVisible

        _stateCurrent = EnumOccState.Ready_HasOcclusionResults;
        _occlusionCoroutine = null;

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
    }

    //--------------------------------------------------------------------
    public void Dispose()
    {
        meshes.Clear();
        _allPerModelAttributes.Clear();

        if (_cbAllPerModelAttributes != null) _cbAllVerticesInfos.Dispose();
        if (_cbAllPerModelAttributes != null) _cbAllPerModelAttributes.Dispose();
        if (_cbAllTriIdxVisible != null) _cbAllTriIdxVisible.Dispose();
        if (_cbAllVisibilityInfos != null) _cbAllVisibilityInfos.Dispose();
    }

    //--------------------------------------------------------------------
    private void OnDrawGizmosSelected()
    {
        if (!isDebugShowGizmoWhenResults) return;
        if (_stateCurrent != EnumOccState.Ready_HasOcclusionResults) return;

        Gizmos.color = Color.red;

        // When we have results, extract the visible triangle index from the _cbAllTriIdxVisible compute buffer and draw a little gizmo at their world pos
        uint[] dataFromTriAppend = new uint[_cbAllTriIdxVisible.count];
        _cbAllTriIdxVisible.GetData(dataFromTriAppend);

        for (int i = 0; i < dataFromTriAppend.Length; i++)
        {
            int idxTriangle = (int)dataFromTriAppend[i];

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

                DebugTools.PointForGizmo(posWorldBarycenter, sizeDebugPos * 1.5f);
            }

            if (isShowGizmoAtEachVertex)
            {
                Vector3 posWorld0 = _allPerModelAttributes[modelid].matrixLocalToWorld.MultiplyPoint(posLocal0);
                Vector3 posWorld1 = _allPerModelAttributes[modelid].matrixLocalToWorld.MultiplyPoint(posLocal1);
                Vector3 posWorld2 = _allPerModelAttributes[modelid].matrixLocalToWorld.MultiplyPoint(posLocal2);

                DebugTools.PointForGizmo(posWorld0, sizeDebugPos);
                DebugTools.PointForGizmo(posWorld1, sizeDebugPos);
                DebugTools.PointForGizmo(posWorld2, sizeDebugPos);
            }
        }
    }

    //--------------------------------------------------------------------
}

//--------------------------------------------------------------------