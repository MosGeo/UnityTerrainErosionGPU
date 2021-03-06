﻿using System;
using Assets.Scripts.Utils;
using UnityEngine;

namespace Assets.Scripts
{
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public class CustomTerrain : MonoBehaviour
    {
        [Header("Main settings")]
        public GameObject Water;
        public ComputeShader ErosionComputeShader;
        public Texture2D InitialState;
        public Material InitHeightMap;

        [Range(32, 1024)]
        public int Width = 256;
        [Range(32, 1024)]
        public int Height = 256;
        
        
        public float BrushAmount = 0f;

        [Serializable]
        public class SimulationSettings
        {
            [Range(0f, 0.05f)]
            public float TimeDelta = 0.02f;

            [Range(0, 0.05f)]
            public float RainRate = 0.012f;

            [Range(0, 1f)]
            public float Evaporation = 0.015f;

            [Range(0.001f, 600000000)]
            public float PipeArea = 20;

            [Range(0.1f, 20f)]
            public float Gravity = 9.81f;

            [Header("Erosion")]
            [Range(0.1f, 3f)]
            public float SedimentCapacity = 1f;

            [Range(0, 3f)]
            public float ThermalErosionRate = 0.15f;

            [Range(0.1f, 2f)]
            public float SoilSuspensionRate = 0.5f;

            [Range(0.1f, 3f)]
            public float SedimentDepositionRate = 1f;

            [Range(0f, 10f)]
            public float SedimentSofteningRate = 5f;

            [Range(0f, 40f)]
            public float MaximalErosionDepth = 10f;

            [Range(0f, 1f)]
            public float TalusAngleTangentCoeff = 0.8f;

            [Range(0f, 1f)]
            public float TalusAngleTangentBias = 0.1f;

            public float PipeLength = 1f / 256;
            public Vector2 CellSize = new Vector2(1f / 256, 1f / 256);
        }
        
        public SimulationSettings Settings;

        // Computation stuff
        // State texture ARGBFloat
        // R - surface height  [0, +inf]
        // G - water over surface height [0, +inf]
        // B - Suspended sediment amount [0, +inf]
        // A - Hardness of the surface [0, 1]
        private RenderTexture _stateTexture;

        // Output Flux-field texture
        // R - flux to the left cell [0, +inf]
        // G - flux to the right cell [0, +inf]
        // B - flux to the top cell [0, +inf]
        // A - flux to the bottom cell [0, +inf]
        private RenderTexture _fluxTexture;

        // Velocity texture
        // R - X-velocity [-inf, +inf]
        // G - Y-velocity [-inf, +inf]
        private RenderTexture _velocityTexture;

        private readonly string[] _kernelNames = {
            "RainAndControl",
            "FluxComputation",
            "FluxApply",
            "HydraulicErosion",
            "SedimentAdvection",
        };
        private int[] _kernels;
        private uint _threadsPerGroupX;
        private uint _threadsPerGroupY;
        private uint _threadsPerGroupZ;

        // Rendering stuff
        private const string StateTextureKey = "_StateTex";
        private MeshRenderer _surfaceMeshRenderer;
        private MeshFilter _surfaceMeshFilter;
        private Material _surfaceMaterial;
        private MeshRenderer _waterMeshRenderer;
        private MeshFilter _waterMeshFilter;
        private Material _waterMaterial;

        // Brush
        private Plane _floor = new Plane(Vector3.up, Vector3.zero);
        private float _controlsRadius = 0.1f;

        void Start()
        {
            if (Water == null)
                Debug.LogError("Water GameObject should be set");

            // Gather necessary components
            _surfaceMeshRenderer = GetComponent<MeshRenderer>();
            _surfaceMeshFilter = GetComponent<MeshFilter>();
            _surfaceMaterial = _surfaceMeshRenderer.material;
            _waterMeshFilter = Water.GetComponent<MeshFilter>();
            _waterMeshRenderer = Water.GetComponent<MeshRenderer>();
            _waterMaterial = _waterMeshRenderer.material;

            Camera.main.depthTextureMode = DepthTextureMode.Depth;

            // Set everything up
            Initialize();
        }
        
        void Update()
        {
            // Controls
            _controlsRadius = Mathf.Clamp01(_controlsRadius + Input.mouseScrollDelta.y * Time.deltaTime);

            var ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            var amount = 0f;
            var brushX = 0f;
            var brushY = 0f;

            if (_floor.Raycast(ray, out var enter))
            {
                var hitPoint = ray.GetPoint(enter);
                brushX = hitPoint.x / Width;
                brushY = hitPoint.z / Height;

                if (Input.GetMouseButton(0))
                    amount = BrushAmount;
            }
            else
            {
                amount = 0f;
            }

            var inputControls = new Vector4(brushX, brushY, _controlsRadius, amount);
            Shader.SetGlobalVector("_InputControls", inputControls);

            // Compute dispatch
            if (ErosionComputeShader != null)
            {
                if (Settings != null)
                {
                    ErosionComputeShader.SetFloat("_TimeDelta", Settings.TimeDelta);
                    ErosionComputeShader.SetFloat("_PipeArea", Settings.PipeArea);
                    ErosionComputeShader.SetFloat("_Gravity", Settings.Gravity);
                    ErosionComputeShader.SetFloat("_PipeLength", Settings.PipeLength);
                    ErosionComputeShader.SetVector("_CellSize", Settings.CellSize);
                    ErosionComputeShader.SetFloat("_Evaporation", Settings.Evaporation);
                    ErosionComputeShader.SetFloat("_RainRate", Settings.RainRate);

                    // Hydraulic erosion
                    ErosionComputeShader.SetFloat("_SedimentCapacity", Settings.SedimentCapacity);
                    ErosionComputeShader.SetFloat("_MaxErosionDepth", Settings.MaximalErosionDepth);
                    ErosionComputeShader.SetFloat("_SuspensionRate", Settings.SoilSuspensionRate);
                    ErosionComputeShader.SetFloat("_DepositionRate", Settings.SedimentDepositionRate);
                    ErosionComputeShader.SetFloat("_SedimentSofteningRate", Settings.SedimentSofteningRate);

                    ErosionComputeShader.SetVector("_InputControls", inputControls);
                }

                // Dispatch all passes sequentially
                foreach (var kernel in _kernels)
                {
                    ErosionComputeShader.Dispatch(kernel,
                        _stateTexture.width / (int)_threadsPerGroupX,
                        _stateTexture.height / (int)_threadsPerGroupY, 1);
                }
            }
        }

        [ContextMenu("Initialize")]
        public void Initialize()
        {
            /* ========= Setup computation =========== */
            // If there are already existing textures - release them
            if (_stateTexture != null)
                _stateTexture.Release();

            if (_fluxTexture != null)
                _fluxTexture.Release();

            if (_velocityTexture != null)
                _velocityTexture.Release();

            // Initialize texture for storing height map
            _stateTexture = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGBFloat)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            // Initialize texture for storing flow
            _fluxTexture = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGBFloat)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };

            // Velocity texture
            _velocityTexture = new RenderTexture(Width, Height, 0, RenderTextureFormat.RGFloat)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            if (!_stateTexture.IsCreated())
                _stateTexture.Create();

            if (!_fluxTexture.IsCreated())
                _fluxTexture.Create();

            if (!_velocityTexture.IsCreated())
                _velocityTexture.Create();

            if (InitialState != null)
            {
                if (InitHeightMap != null)
                    Graphics.Blit(InitialState, _stateTexture, InitHeightMap);
                else
                    Graphics.Blit(InitialState, _stateTexture);
            }
            
            // Setup computation shader
            if (ErosionComputeShader != null)
            {
                _kernels = new int[_kernelNames.Length];
                var i = 0;
                foreach (var kernelName in _kernelNames)
                {
                    var kernel = ErosionComputeShader.FindKernel(kernelName);;
                    _kernels[i++] = kernel;

                    // Set all textures
                    ErosionComputeShader.SetTexture(kernel, "HeightMap", _stateTexture);
                    ErosionComputeShader.SetTexture(kernel, "VelocityMap", _velocityTexture);
                    ErosionComputeShader.SetTexture(kernel, "FluxMap", _fluxTexture);
                }
                
                ErosionComputeShader.SetInt("_Width", Width);
                ErosionComputeShader.SetInt("_Height", Height);
                ErosionComputeShader.GetKernelThreadGroupSizes(_kernels[0], out _threadsPerGroupX, out _threadsPerGroupY, out _threadsPerGroupZ);
                
            }

            // Debug information
            Debugger.Instance.Display("Width", Width);
            Debugger.Instance.Display("Height", Height);
            Debugger.Instance.Display("HeightMap", _stateTexture);
            Debugger.Instance.Display("FluxMap", _fluxTexture);
            Debugger.Instance.Display("VelocityMap", _velocityTexture);


            /* ========= Setup Rendering =========== */
            // Assign state texture to materials
            _surfaceMaterial.SetTexture(StateTextureKey, _stateTexture);
            _waterMaterial.SetTexture(StateTextureKey, _stateTexture);

            // Generate meshes
            var mesh = MeshUtils.GeneratePlane(
                origin: Vector3.zero, 
                axis0: Vector3.right * Width, 
                axis1: Vector3.forward * Height, 
                axis0Vertices: Width,
                axis1Vertices: Height, 
                uvStart: Vector2.zero, 
                uvEnd: Vector2.one);

            _waterMeshFilter.sharedMesh = mesh;
            _surfaceMeshFilter.sharedMesh = mesh;
        }
    }
}
