// Packs two full-resolution eye images into a side-by-side frame for a
// projector running in 3D SBS mode. The projector stretches each half back to
// full width, so squeeze-then-stretch round-trips and the warp geometry inside
// each eye image is preserved. _Mono packs the left image into both halves so
// calibration patterns and 2D content display correctly while the projector
// stays in 3D mode.
Shader "PMSDK/StereoPack" {
    Properties {
        _LeftTex ("Left Eye", 2D) = "black" {}
        _RightTex ("Right Eye", 2D) = "black" {}
        _Mono ("Mono Passthrough", Float) = 0
    }
    SubShader {
        Tags { "RenderType"="Opaque" }
        Cull Off ZWrite Off ZTest Always
        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _LeftTex;
            sampler2D _RightTex;
            float _Mono;

            struct appdata {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert (appdata v) {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target {
                float rightHalf = step(0.5, i.uv.x);
                float2 sampleUv = float2(i.uv.x * 2.0 - rightHalf, i.uv.y);
                fixed4 left = tex2D(_LeftTex, sampleUv);
                fixed4 right = tex2D(_RightTex, sampleUv);
                fixed4 eye = lerp(left, right, rightHalf);
                return lerp(eye, left, step(0.5, _Mono));
            }
            ENDCG
        }
    }
}
