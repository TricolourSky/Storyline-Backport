Shader "Custom/Billboard_FogSheet_Simple"
{
    Properties
    {
        _MainTex ("Texture Image", 2D) = "white" {}
        _Color ("Tint", Color) = (0,0,0,1)
        _DiffuseIntensity ("Opacity Intensity", Float) = 1.9
        _DistanceFadingAlphaMax ("Max Distance Alpha", Range(0,3)) = 1
        _DistanceFadingAlphaMultiplier ("Distance Alpha Multiplier", Range(0,5)) = 1
        _DistanceFadingAlphaStartOffset ("Start Offset", Range(-20,0)) = -0.2
        _DiffuseScale ("Diffuse UV Scale", Float) = 1
        _ScrollXSpeed ("X Scroll Speed", Range(0,100)) = 0.1
        _ScrollYSpeed ("Y Scroll Speed", Range(0,100)) = 0.1
        _AngleFadeStrengthFront ("Front Fade", Range(0,10)) = 5
        _AngleFadeStrengthBack ("Back Fade", Range(0,10)) = 5
        _RadialPower ("Radial Contrast", Range(0,10)) = 1
    }
    SubShader
    {
        Tags { "Queue"="Transparent+100" "RenderType"="Transparent" "IgnoreProjector"="True" }
        // Keep the legacy additive light-volume look, but mask the emitted RGB
        // with the source texture and cap each sheet so overlaps cannot white out.
        Cull Off ZWrite Off ZTest LEqual Blend SrcAlpha One
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _MainTex; float4 _MainTex_ST, _Color;
            float _DiffuseIntensity, _DistanceFadingAlphaMax, _DistanceFadingAlphaMultiplier;
            float _DistanceFadingAlphaStartOffset, _DiffuseScale, _ScrollXSpeed, _ScrollYSpeed;
            float _AngleFadeStrengthFront, _AngleFadeStrengthBack, _RadialPower;
            struct appdata { float4 vertex:POSITION; float3 normal:NORMAL; float2 uv:TEXCOORD0; };
            struct v2f { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; float3 world:TEXCOORD1; float3 normal:TEXCOORD2; };
            v2f vert(appdata v) { v2f o; o.pos=UnityObjectToClipPos(v.vertex); o.uv=TRANSFORM_TEX(v.uv,_MainTex); o.world=mul(unity_ObjectToWorld,v.vertex).xyz; o.normal=UnityObjectToWorldNormal(v.normal); return o; }
            fixed4 frag(v2f i):SV_Target
            {
                float2 uv=i.uv*_DiffuseScale+_Time.y*float2(_ScrollXSpeed,_ScrollYSpeed);
                fixed3 tex=tex2D(_MainTex,uv).rgb; half lum=max(tex.r,max(tex.g,tex.b));
                half face=dot(normalize(i.normal),normalize(_WorldSpaceCameraPos-i.world));
                half angle=pow(saturate(abs(face)),max(0.01,face>=0?_AngleFadeStrengthFront:_AngleFadeStrengthBack));
                half radial=pow(saturate(1-length(i.uv-0.5)*2),max(0.01,_RadialPower));
                half dist=saturate(_DistanceFadingAlphaMax-(distance(_WorldSpaceCameraPos,i.world)+_DistanceFadingAlphaStartOffset)*0.02*_DistanceFadingAlphaMultiplier);
                half gain=(_DiffuseIntensity<1.0)?0.08:0.012;
                half alpha=min(lum*_DiffuseIntensity*gain,0.14)*angle*lerp(1,radial,saturate(_RadialPower))*dist*_Color.a;
                return fixed4(_Color.rgb*tex,alpha);
            }
            ENDCG
        }
    }
}
