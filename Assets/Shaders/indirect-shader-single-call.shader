Shader "Indirect Shader Single Call" {
    
    Properties {}
    
    SubShader {

        Tags { "LightMode" = "ForwardBase" }

        Pass {
            //Cull Off
            
            CGPROGRAM
            #include "UnityCG.cginc"
            #pragma target 5.0  
            #pragma vertex vert
            #pragma fragment frag

            struct Point {
                int modelid;
                float3 vertex;
                float3 normal;
            };

            struct Other {
                float4x4 mat;
                float4 color;
            };

            struct v2f {
                float4 pos : SV_POSITION;
                float4 col : COLOR;
            };

            uniform fixed4 _LightColor0;
            
            StructuredBuffer<uint> triappend;
            StructuredBuffer<uint> trilist;
            StructuredBuffer<Other> other;
            StructuredBuffer<Point> points;

            v2f vert(uint id : SV_VertexID, uint inst : SV_InstanceID)
            {
                v2f o;

                // Position
                uint tid = triappend[id / 3];
                int idx = tid * 3 + (id % 3);
                Point pt = points[idx];
                int modelid = pt.modelid;
                
                float4x4 mat = other[modelid].mat;
                float4 pos = float4(pt.vertex,1.0f);
                pos = mul(mat, pos);
                pos = UnityObjectToClipPos(pos);

                float4 nor =  float4(pt.normal, 0.0f);
                nor = mul(mat, nor);
                nor = mul(unity_ObjectToWorld, nor); 

                // Lighting
                float3 normalDirection = normalize(nor.xyz);
                float4 AmbientLight = UNITY_LIGHTMODEL_AMBIENT;
                float4 LightDirection = normalize(_WorldSpaceLightPos0);
                float4 DiffuseLight = saturate(dot(LightDirection, normalDirection))*_LightColor0;
                o.col = float4(AmbientLight + DiffuseLight) * other[modelid].color;                
                o.pos = pos;

                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return i.col;
            }

            ENDCG
        }
    }
}