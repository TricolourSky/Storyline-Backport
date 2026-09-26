Shader "Characters/TraiderHair"
{
    Properties
    {
        _MainTex ("Albedo (RGB) Alpha (A)", 2D) = "black" {}
        [HDR] _AlbedoTint ("Albedo Tint", Color) = (1,1,1,1)
        [NoScaleOffset] _SpecGlossMap ("Specular(RGB) Gloss (A)", 2D) = "white" {}
        [HDR] _SpecularTint ("Specular Tint", Color) = (0.5,0.5,0.5,1)
        _Glossiness ("Glossiness", Range(0,1)) = 0.5
        [NoScaleOffset] [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _AlbedoMapWeight ("Albedo", Range(0,1)) = 1
        _SpecularMapWeight ("Specular", Range(0,1)) = 1
        _NormalMapWeight ("Normal", Range(0,1)) = 1
        _GlossMapWeight ("Gloss", Range(0,1)) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _CullMode ("Cull Mode", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        LOD 300
        Cull [_CullMode]
        CGPROGRAM
        #pragma surface surf StandardSpecular fullforwardshadows alpha:fade addshadow
        #pragma target 3.0
        sampler2D _MainTex, _SpecGlossMap, _BumpMap;
        fixed4 _AlbedoTint, _SpecularTint;
        half _AlbedoMapWeight, _SpecularMapWeight, _NormalMapWeight, _GlossMapWeight, _Glossiness;
        struct Input { float2 uv_MainTex; };
        void surf(Input i, inout SurfaceOutputStandardSpecular o)
        {
            fixed4 albedo = tex2D(_MainTex, i.uv_MainTex);
            fixed4 spec = tex2D(_SpecGlossMap, i.uv_MainTex);
            o.Albedo = lerp(1, albedo.rgb * _AlbedoTint.rgb, _AlbedoMapWeight);
            o.Specular = lerp(_SpecularTint.rgb, spec.rgb * _SpecularTint.rgb, _SpecularMapWeight);
            o.Smoothness = lerp(_Glossiness, spec.a, _GlossMapWeight);
            o.Normal = lerp(fixed3(0,0,1), UnpackNormal(tex2D(_BumpMap, i.uv_MainTex)), _NormalMapWeight);
            o.Alpha = albedo.a * _AlbedoTint.a;
        }
        ENDCG
    }
    Fallback "Legacy Shaders/Transparent/Cutout/Specular"
}
