Shader "Replay/EquirectProjection"
{
	Properties
	{
		_Cube ("Cubemap", CUBE) = "" {}
		_Rotation ("Rotation", Matrix) = "identity"
		_UVCoverage ("UV Coverage", Vector) = (6.283185307, 3.141592654, 0, 0)
	}
	SubShader
	{
		Tags { "RenderType"="Opaque" }
		LOD 100

		Pass
		{
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag

			#include "UnityCG.cginc"

			struct appdata
			{
				float4 vertex : POSITION;
				float2 uv : TEXCOORD0;
			};

			struct v2f
			{
				float2 uv : TEXCOORD0;
				float4 vertex : SV_POSITION;
			};

			samplerCUBE _Cube;
			float4x4 _Rotation;

			// x = horizontal angle scale, y = vertical angle scale,
			// z = horizontal uv offset, w = vertical uv offset
			float4 _UVCoverage;

			v2f vert(appdata v)
			{
				v2f o;
				o.vertex = UnityObjectToClipPos(v.vertex);
				o.uv = v.uv;
				return o;
			}

			half4 frag (v2f i) : SV_Target
			{
				// convert UV coordinates to directional vector using equirectangular projection
				float hAngle = (i.uv.x + _UVCoverage.z) * _UVCoverage.x;
				float vAngle = ((1 - i.uv.y) + _UVCoverage.w - 0.5) * _UVCoverage.y;

				float3 dir = float3(0,0,1);

				float3x3 mat = float3x3(
					1, 0, 0,
					0, cos(vAngle), -sin(vAngle),
					0, sin(vAngle), cos(vAngle)
					);

				dir = mul(mat, dir);

				mat = float3x3(
					cos(hAngle), 0, sin(hAngle),
					0, 1, 0,
					-sin(hAngle), 0, cos(hAngle)
					);

				dir = mul(mat, dir);

				dir = mul((float3x3)_Rotation, dir);

				return texCUBE(_Cube, dir);
			}
			ENDCG
		}
	}
}
