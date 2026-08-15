// Replacement shader for semantic-segmentation rendering. Each mesh renderer is
// temporarily swapped to this material with _SemanticId set to its (encoded) mesh
// asset id, so every pixel belonging to that mesh records the same float id.
// Mirrors ReplayDepthReplacement but outputs a constant value instead of depth.
// The value is written to the red channel; the readback buffer is RFloat.
Shader "Replay/SemanticReplacement"
{
    Properties
    {
        _SemanticId ("Semantic Id", Float) = 0
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

            float _SemanticId;

            struct v2f
            {
                float4 pos : SV_POSITION;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(_SemanticId, _SemanticId, _SemanticId, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
