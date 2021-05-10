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
    [Header("Links")]
    public OcclusionPerTriangleGPU occlusionManager;
    public List<Renderer> allRenderesToCheck;

    //--------------------------------------------------------------------
    void Start()
    {
        occlusionManager.Init();

        if (occlusionManager.IsReadyToComputeVisibility)
            occlusionManager.CheckVisiblityAsync(allRenderesToCheck);
    }

    //--------------------------------------------------------------------
    void Update()
    {
        // Space to check / refresh visibility
        if (Input.GetKeyDown(KeyCode.Space) && occlusionManager.IsReadyToComputeVisibility)
            occlusionManager.CheckVisiblityAsync(allRenderesToCheck);
    }

    //--------------------------------------------------------------------
    private void OnDestroy()
    {
        occlusionManager.Dispose();
    }

    //--------------------------------------------------------------------
}

//--------------------------------------------------------------------