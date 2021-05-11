//--------------------------------------------------------------------
// Ported / extracted from Garrett Johnson https://github.com/gkjohnson/unity-rendering-investigation
//--------------------------------------------------------------------

Shader "Occlusion/Indirect Shader Single Call Ids" 
{
    //--------------------------------------------------------------------
    Properties {}
    
    //--------------------------------------------------------------------
    SubShader {

        //--------------------------------------------------------------------
        Tags { "LightMode" = "ForwardBase" }

        //--------------------------------------------------------------------
        Pass 
        {
            //--------------------------------------------------------------------
            CGPROGRAM
            #include "UnityCG.cginc"
            #pragma target 5.0  
            #pragma vertex vert
            #pragma fragment frag

            //--------------------------------------------------------------------
            struct Point {
                int modelid;
                float3 vertex;
                float3 normal;
            };

            //--------------------------------------------------------------------
            struct PerModelAttributes {
                float4x4 matrixLocalToWorld;
            };

            //--------------------------------------------------------------------
            struct v2f {
                float4 pos : SV_POSITION;
                float4 id : COLOR;
            };

            //--------------------------------------------------------------------
            StructuredBuffer<PerModelAttributes> AllPerModelAttributes;
            StructuredBuffer<Point> AllVerticesInfos;
            uint idOffset = 0;

            //--------------------------------------------------------------------
            v2f vert(uint id : SV_VertexID, uint inst : SV_InstanceID)
            {
                v2f o;

                // idOffset is used to only draw some vertices each frame
                uint idxVertex = id + idOffset;

                int modelid = AllVerticesInfos[idxVertex].modelid;
                float4x4 matrixLocalToWorld = AllPerModelAttributes[modelid].matrixLocalToWorld;
                float4 pos = float4(AllVerticesInfos[idxVertex].vertex,1.0f);
                pos = mul(matrixLocalToWorld, pos);
                pos = UnityObjectToClipPos(pos);
                
                uint idTriangle = floor(idxVertex / 3);

                // Transform the vertex id (an unsigned integer) into a RGBA float4 color using bitwise operations
                // Check this article for more information : https://en.wikipedia.org/wiki/Bitwise_operations_in_C#Right_shift_%3E%3E
                o.id = float4(
                    ((idTriangle >> 0) & 0xFF) / 255.0,
                    ((idTriangle >> 8) & 0xFF) / 255.0,
                    ((idTriangle >> 16) & 0xFF) / 255.0,
                    ((idTriangle >> 24) & 0xFF) / 255.0
                );

                o.pos = pos;
                
                return o;
            }

            //--------------------------------------------------------------------
            float4 frag(v2f i) : SV_Target
            {
                return i.id; // We draw the id into the render texture
            }

            //--------------------------------------------------------------------
            ENDCG
        }

        //--------------------------------------------------------------------
    }

    //--------------------------------------------------------------------
}

//--------------------------------------------------------------------