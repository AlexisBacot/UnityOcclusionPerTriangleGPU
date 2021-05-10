//-------------------------------------------------------------------
// Created by Alexis Bacot - 2021 - www.alexisbacot.com
//-------------------------------------------------------------------
using UnityEngine;

//-------------------------------------------------------------------
public class DebugTools : MonoBehaviour
{
	//-------------------------------------------------------------------
	public static void PointForGizmo(Vector3 pos, float scale)
	{
		Vector3[] points = new Vector3[]
		{
			pos + (Vector3.up * scale),
			pos - (Vector3.up * scale),
			pos + (Vector3.right * scale),
			pos - (Vector3.right * scale),
			pos + (Vector3.forward * scale),
			pos - (Vector3.forward * scale)
		};

		Gizmos.DrawLine(points[0], points[1]);
		Gizmos.DrawLine(points[2], points[3]);
		Gizmos.DrawLine(points[4], points[5]);
	}

	//-------------------------------------------------------------------
}

//-------------------------------------------------------------------