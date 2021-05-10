// From Garrett Johnson https://github.com/gkjohnson/unity-rendering-investigation

using System.Runtime.InteropServices;
using UnityEngine;

//--------------------------------------------------------------------
// Utility for converting a unity mesh into
// a buffer of indices and a buffer of attributes
public static class ImportStructuredBufferMesh 
{
    //--------------------------------------------------------------------
    public struct Point
    {
        public int modelid;
        public Vector3 vertex;
        public Vector3 normal;

        public override string ToString()
        {
            return "pos: " + vertex + " normal: " + normal + " modelID: " + modelid;
        }
    }

    //--------------------------------------------------------------------
    public static Point[] ImportAllAndUnpack(Mesh[] meshes_, ref ComputeBuffer AllVerticesInfos_)
    {
        uint[] offsets = new uint[meshes_.Length];
        uint totalTriangles = 0;
        for (int i = 0; i < meshes_.Length; i++)
        {
            offsets[i] = totalTriangles;
            totalTriangles += (uint)meshes_[i].triangles.Length;
        }

        Point[] data = new Point[totalTriangles];
        for (int i = 0; i < meshes_.Length; i++)
        {
            Mesh mesh = meshes_[i];
            uint offset = offsets[i];

            int[] tris = mesh.triangles;
            Vector3[] verts = mesh.vertices;
            Vector3[] norms = mesh.normals;

            for (int j = 0; j < tris.Length; j++)
            {
                int idx = tris[j];
                data[j + offset] = new Point()
                {
                    modelid = i,
                    vertex = verts[idx],
                    normal = norms[idx]
                };
            }
        }

        AllVerticesInfos_ = new ComputeBuffer(data.Length, Marshal.SizeOf(typeof(Point)), ComputeBufferType.Default);
        AllVerticesInfos_.SetData(data);

        return data;
    }

    //--------------------------------------------------------------------
}

//--------------------------------------------------------------------
