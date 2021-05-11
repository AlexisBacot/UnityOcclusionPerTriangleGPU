//--------------------------------------------------------------------
// Created by Alexis Bacot - 2021 - www.alexisbacot.com
//--------------------------------------------------------------------
using System.Collections.Generic;
using UnityEngine;
using OcclusionPerTriangleGPU;

//--------------------------------------------------------------------
public class SampleOcclusionPerTriangleGPU : MonoBehaviour
{
    //--------------------------------------------------------------------
    [Header("Auto Recheck Settings")]
    public bool isUpdateOcclusionAutoIfMove = true;
    public float timeMinBetweenChecks = 2.0f;
    public float distMinToRecheck = 0.25f;
    public float angleMinToRecheck = 15.0f;

    //[Header("Movement Settings")]
    //public bool isUpdateOcclusionAutoIfMove = true;
    //public float speedMoveCam = 2;
    //public float speedTurnCam = 30;

    [Header("Links")]
    public OcclusionManager occlusionManager;
    public GameObject objWithAllGeometry;

    // Internal
    //private bool _hasMoved = false;
    private List<MeshFilter> _allMeshFiltersToCheck;
    private float _timeSinceLastCheck;
    private Vector3 _posLastOccCam;
    private Quaternion _rotLastOccCam;

    //--------------------------------------------------------------------
    void Start()
    {
        occlusionManager.Init();

        _allMeshFiltersToCheck = new List<MeshFilter>();
        objWithAllGeometry.GetComponentsInChildren<MeshFilter>(false, _allMeshFiltersToCheck);

        _posLastOccCam = occlusionManager.camOcclusion.transform.position;
        _rotLastOccCam = occlusionManager.camOcclusion.transform.rotation;

        // Try to do this as less as possible because it's slow, but if your objects are moving you will need to repack them! (not all!)
        occlusionManager.PackAllMeshes(_allMeshFiltersToCheck);

        if (occlusionManager.IsReadyToComputeVisibility)
            occlusionManager.CheckVisiblityAsync();
    }

    //--------------------------------------------------------------------
    void Update()
    {
        // Space to check / refresh visibility
        if (Input.GetKeyDown(KeyCode.Space) && occlusionManager.IsReadyToComputeVisibility)
            occlusionManager.CheckVisiblityAsync();

        DoMovement();

        _timeSinceLastCheck += Time.deltaTime;
    }

    //--------------------------------------------------------------------
    private void DoMovement()
    {
        // If we moved and occlusion is ready, we re calculate it
        Vector3 posCamOcc = occlusionManager.camOcclusion.transform.position;
        float distCam = Vector3.Distance(_posLastOccCam, posCamOcc);
        float distAngleCam = Quaternion.Angle(_rotLastOccCam, occlusionManager.camOcclusion.transform.rotation);

        if (isUpdateOcclusionAutoIfMove && (distCam > distMinToRecheck || distAngleCam > angleMinToRecheck) 
            && _timeSinceLastCheck > timeMinBetweenChecks && occlusionManager.IsReadyToComputeVisibility)
        {
            occlusionManager.CheckVisiblityAsync();

            _timeSinceLastCheck = 0.0f;
            _posLastOccCam = occlusionManager.camOcclusion.transform.position;
            _rotLastOccCam = occlusionManager.camOcclusion.transform.rotation;
        }
    }

    //--------------------------------------------------------------------
    private void OnDestroy()
    {
        occlusionManager.ClearAndDispose(true);
    }

    //--------------------------------------------------------------------
}

//--------------------------------------------------------------------
