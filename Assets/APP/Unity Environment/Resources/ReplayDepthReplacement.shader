// Replacement shader for depth-only rendering. One subShader per standard Unity
// RenderType tag so that SetReplacementShader(shader, "") captures every object
// regardless of what shader it originally used. Each subShader outputs the same
// Linear01Depth value (eye-space distance / far plane) to all color channels.
Shader "Replay/DepthReplacement"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
                float depth : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.depth = -UnityObjectToViewPos(v.vertex).z / _ProjectionParams.z;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(i.depth, i.depth, i.depth, 1.0);
            }
            ENDCG
        }
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
                float depth : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.depth = -UnityObjectToViewPos(v.vertex).z / _ProjectionParams.z;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(i.depth, i.depth, i.depth, 1.0);
            }
            ENDCG
        }
    }

    SubShader
    {
        Tags { "RenderType"="TransparentCutout" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
                float depth : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.depth = -UnityObjectToViewPos(v.vertex).z / _ProjectionParams.z;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(i.depth, i.depth, i.depth, 1.0);
            }
            ENDCG
        }
    }

    SubShader
    {
        Tags { "RenderType"="TreeBark" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
                float depth : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.depth = -UnityObjectToViewPos(v.vertex).z / _ProjectionParams.z;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(i.depth, i.depth, i.depth, 1.0);
            }
            ENDCG
        }
    }

    SubShader
    {
        Tags { "RenderType"="TreeLeaf" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
                float depth : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.depth = -UnityObjectToViewPos(v.vertex).z / _ProjectionParams.z;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(i.depth, i.depth, i.depth, 1.0);
            }
            ENDCG
        }
    }

    SubShader
    {
        Tags { "RenderType"="TreeOpaque" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
                float depth : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.depth = -UnityObjectToViewPos(v.vertex).z / _ProjectionParams.z;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(i.depth, i.depth, i.depth, 1.0);
            }
            ENDCG
        }
    }

    SubShader
    {
        Tags { "RenderType"="TreeTransparent" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
                float depth : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.depth = -UnityObjectToViewPos(v.vertex).z / _ProjectionParams.z;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(i.depth, i.depth, i.depth, 1.0);
            }
            ENDCG
        }
    }

    SubShader
    {
        Tags { "RenderType"="TreeBillboard" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
                float depth : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.depth = -UnityObjectToViewPos(v.vertex).z / _ProjectionParams.z;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(i.depth, i.depth, i.depth, 1.0);
            }
            ENDCG
        }
    }

    SubShader
    {
        Tags { "RenderType"="Grass" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
                float depth : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.depth = -UnityObjectToViewPos(v.vertex).z / _ProjectionParams.z;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(i.depth, i.depth, i.depth, 1.0);
            }
            ENDCG
        }
    }

    SubShader
    {
        Tags { "RenderType"="GrassBillboard" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
                float depth : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.depth = -UnityObjectToViewPos(v.vertex).z / _ProjectionParams.z;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(i.depth, i.depth, i.depth, 1.0);
            }
            ENDCG
        }
    }

    SubShader
    {
        Tags { "RenderType"="Geometry" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
                float depth : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.depth = -UnityObjectToViewPos(v.vertex).z / _ProjectionParams.z;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(i.depth, i.depth, i.depth, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
