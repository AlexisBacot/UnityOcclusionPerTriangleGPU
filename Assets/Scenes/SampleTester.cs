//--------------------------------------------------------------------
// Created by Alexis Bacot - 2021 - www.alexisbacot.com
//--------------------------------------------------------------------
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

//--------------------------------------------------------------------
public class SampleTester : MonoBehaviour
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
    public OcclusionPerTriangleGPU occlusionManager;
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

        if (occlusionManager.IsReadyToComputeVisibility)
            occlusionManager.CheckVisiblityAsync(_allMeshFiltersToCheck);
    }

    //--------------------------------------------------------------------
    void Update()
    {
        // Space to check / refresh visibility
        if (Input.GetKeyDown(KeyCode.Space) && occlusionManager.IsReadyToComputeVisibility)
            occlusionManager.CheckVisiblityAsync(_allMeshFiltersToCheck);

        DoMovement();

        _timeSinceLastCheck += Time.deltaTime;
    }

    //--------------------------------------------------------------------
    private void DoMovement()
    {
        // Move the main camera and occlusion camera around
        float inputHorizontal = Input.GetAxis("Horizontal");
        float inputVertical = Input.GetAxis("Vertical");

        // Move forward / backward
        /*if (Mathf.Abs(inputVertical) > 0)
        {
            //Camera.main.transform.position += inputVertical * speedMoveCam * Time.deltaTime * Camera.main.transform.forward;
            //occlusionManager.camOcclusion.transform.position = Camera.main.transform.position;

            occlusionManager.camOcclusion.transform.position += inputVertical * speedMoveCam * Time.deltaTime * occlusionManager.camOcclusion.transform.forward;

            _hasMoved = true;
        }

        // Turn left/right
        if (Mathf.Abs(inputHorizontal) > 0)
        {
            //Camera.main.transform.Rotate(Vector3.up, inputHorizontal * speedTurnCam * Time.deltaTime);
            //occlusionManager.camOcclusion.transform.rotation = Camera.main.transform.rotation;

            occlusionManager.camOcclusion.transform.Rotate(Vector3.up, inputHorizontal * speedTurnCam * Time.deltaTime);

            _hasMoved = true;
        }*/

        // If we moved and occlusion is ready, we re calculate it
        Vector3 posCamOcc = occlusionManager.camOcclusion.transform.position;
        float distCam = Vector3.Distance(_posLastOccCam, posCamOcc);
        float distAngleCam = Quaternion.Angle(_rotLastOccCam, occlusionManager.camOcclusion.transform.rotation);

        if (isUpdateOcclusionAutoIfMove && (distCam > distMinToRecheck || distAngleCam > angleMinToRecheck) 
            && _timeSinceLastCheck > timeMinBetweenChecks && occlusionManager.IsReadyToComputeVisibility)
        {
            occlusionManager.CheckVisiblityAsync(_allMeshFiltersToCheck);

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