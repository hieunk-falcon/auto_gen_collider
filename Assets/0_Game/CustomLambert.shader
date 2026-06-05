Shader "Custom/LambertCustom"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        [MainTexture] _BaseMap ("Albedo", 2D) = "white" {}
        [HDR] _AmbientColor ("Ambient Color (HDR)", Color) = (0.1, 0.1, 0.1, 1)
        _Diffuse ("Diffuse", Range(0, 2)) = 1.0
        _LightIntensity ("Light Intensity", Range(0, 5)) = 1.0
        _ShadowThreshold ("Shadow Threshold", Range(-1, 1)) = 0.0
        _ShadowSmooth ("Shadow Smooth", Range(0.001, 1)) = 0.1
        [HDR] _ShadowColor ("Shadow Color", Color) = (0.2,0.2,0.3,1)
        _ShadowIntensity ("Shadow Intensity", Range(0, 1)) = 0.8
        [HDR] _SpecColor ("Specular Color", Color) = (1,1,1,1)
        _SpecIntensity ("Specular Intensity", Range(0, 5)) = 0.5
        _SpecPower ("Specular Power", Range(1, 128)) = 32
        _SpecThreshold ("Specular Threshold", Range(0, 1)) = 0.5
        _SpecSmooth ("Specular Smooth", Range(0.001, 1)) = 0.1
        [Toggle] _ReceiveShadow ("Receive Shadow", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);

        UNITY_INSTANCING_BUFFER_START(Props)
            UNITY_DEFINE_INSTANCED_PROP(float4, _BaseMap_ST)
            UNITY_DEFINE_INSTANCED_PROP(half4,  _BaseColor)
            UNITY_DEFINE_INSTANCED_PROP(half4,  _AmbientColor)
            UNITY_DEFINE_INSTANCED_PROP(half,   _Diffuse)
            UNITY_DEFINE_INSTANCED_PROP(half,   _LightIntensity)
            UNITY_DEFINE_INSTANCED_PROP(half,   _ShadowThreshold)
            UNITY_DEFINE_INSTANCED_PROP(half,   _ShadowSmooth)
            UNITY_DEFINE_INSTANCED_PROP(half4,  _ShadowColor)
            UNITY_DEFINE_INSTANCED_PROP(half,   _ShadowIntensity)
            UNITY_DEFINE_INSTANCED_PROP(half4,  _SpecColor)
            UNITY_DEFINE_INSTANCED_PROP(half,   _SpecIntensity)
            UNITY_DEFINE_INSTANCED_PROP(half,   _SpecPower)
            UNITY_DEFINE_INSTANCED_PROP(half,   _SpecThreshold)
            UNITY_DEFINE_INSTANCED_PROP(half,   _SpecSmooth)
            UNITY_DEFINE_INSTANCED_PROP(half,   _ReceiveShadow)
        UNITY_INSTANCING_BUFFER_END(Props)
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                half3  normalWS    : TEXCOORD0;
                float3 positionWS  : TEXCOORD1;
                float2 uv          : TEXCOORD2;
            #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                float4 shadowCoord : TEXCOORD3;
            #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normInputs  = GetVertexNormalInputs(input.normalOS);

                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.normalWS   = normInputs.normalWS;
                float4 baseMapST  = UNITY_ACCESS_INSTANCED_PROP(Props, _BaseMap_ST);
                output.uv         = input.uv * baseMapST.xy + baseMapST.zw;

            #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                output.shadowCoord = GetShadowCoord(posInputs);
            #endif

                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Read instanced properties into locals.
                half4 baseColor     = UNITY_ACCESS_INSTANCED_PROP(Props, _BaseColor);
                half4 ambientColor  = UNITY_ACCESS_INSTANCED_PROP(Props, _AmbientColor);
                half  shadowSmooth  = UNITY_ACCESS_INSTANCED_PROP(Props, _ShadowSmooth);
                half  shadowThresh  = UNITY_ACCESS_INSTANCED_PROP(Props, _ShadowThreshold);
                half4 shadowColor   = UNITY_ACCESS_INSTANCED_PROP(Props, _ShadowColor);
                half  shadowIntens  = UNITY_ACCESS_INSTANCED_PROP(Props, _ShadowIntensity);
                half  specSmooth    = UNITY_ACCESS_INSTANCED_PROP(Props, _SpecSmooth);
                half  specThresh    = UNITY_ACCESS_INSTANCED_PROP(Props, _SpecThreshold);
                half4 specColor     = UNITY_ACCESS_INSTANCED_PROP(Props, _SpecColor);
                half  specIntens    = UNITY_ACCESS_INSTANCED_PROP(Props, _SpecIntensity);
                half  specPower     = UNITY_ACCESS_INSTANCED_PROP(Props, _SpecPower);
                half  receiveShadow = saturate(UNITY_ACCESS_INSTANCED_PROP(Props, _ReceiveShadow));
                half  diffuse       = UNITY_ACCESS_INSTANCED_PROP(Props, _Diffuse);
                half  lightIntens   = UNITY_ACCESS_INSTANCED_PROP(Props, _LightIntensity);
                half  diffScalar    = diffuse * lightIntens;

                half4 albedoTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half3 albedo    = albedoTex.rgb * baseColor.rgb;
                half  alpha     = albedoTex.a   * baseColor.a;

                half3 normalWS  = normalize(input.normalWS);
                half3 viewDirWS = SafeNormalize(GetWorldSpaceViewDir(input.positionWS));

            #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                float4 shadowCoord = input.shadowCoord;
            #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
            #else
                float4 shadowCoord = float4(0,0,0,0);
            #endif

            #if defined(SHADOWS_SHADOWMASK) && !defined(LIGHTMAP_ON)
                half4 shadowMask = unity_ProbesOcclusion;
            #else
                half4 shadowMask = half4(1, 1, 1, 1);
            #endif

                // Invariants reused by main + additional lights.
                half  halfShadowSmooth = shadowSmooth * 0.5h;
                half  shadowEdgeLo     = shadowThresh - halfShadowSmooth;
                half  shadowEdgeHi     = shadowThresh + halfShadowSmooth;
                half  halfSpecSmooth   = specSmooth * 0.5h;
                half  specEdgeLo       = specThresh - halfSpecSmooth;
                half  specEdgeHi       = specThresh + halfSpecSmooth;
                half3 shadowTintConst  = shadowColor.rgb * shadowIntens;
                half3 specTint         = specColor.rgb * specIntens;

                Light mainLight = GetMainLight(shadowCoord, input.positionWS, shadowMask);

                // Diffuse accumulator is "pre-albedo" — multiply by albedo once at the end.
                half  NdotL         = dot(normalWS, mainLight.direction);
                half  shadowAtten   = lerp(1.0h, mainLight.shadowAttenuation, receiveShadow);
                half  lightRamp     = smoothstep(shadowEdgeLo, shadowEdgeHi, NdotL);
                half  finalRamp     = lightRamp * shadowAtten;
                half3 mainLitFactor = diffScalar * mainLight.color;
                half3 diffFactor    = lerp(shadowTintConst, mainLitFactor, finalRamp);

                // Main light specular (additive, not multiplied by albedo).
                half3 halfDirWS = SafeNormalize(mainLight.direction + viewDirWS);
                half  specBase  = pow(saturate(dot(normalWS, halfDirWS)), specPower);
                half  specRamp  = smoothstep(specEdgeLo, specEdgeHi, specBase);
                half3 specular  = (specTint * mainLight.color) * (specRamp * finalRamp);

                // Single albedo multiply at the end folds ambient + diffuse together.
                half3 finalColor = albedo * (ambientColor.rgb + diffFactor) + specular;

                return half4(finalColor, alpha);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct AttrShadow
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct VaryShadow
            {
                float4 positionCS : SV_POSITION;
            };

            VaryShadow ShadowVert(AttrShadow input)
            {
                VaryShadow output;
                UNITY_SETUP_INSTANCE_ID(input);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(input.normalOS);

            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDir = normalize(_LightPosition - positionWS);
            #else
                float3 lightDir = _LightDirection;
            #endif

                output.positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDir));

            #if UNITY_REVERSED_Z
                output.positionCS.z = min(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                output.positionCS.z = max(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif

                return output;
            }

            half4 ShadowFrag(VaryShadow input) : SV_TARGET
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct AttrDepth
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct VaryDepth
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            VaryDepth DepthVert(AttrDepth input)
            {
                VaryDepth output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 DepthFrag(VaryDepth input) : SV_TARGET
            {
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/InternalErrorShader"
}

