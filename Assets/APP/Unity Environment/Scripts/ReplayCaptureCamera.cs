using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Renderite.Shared;
using Renderite.Unity;
using UnityEngine;

/// <summary>
/// Replay-driven capture camera. Reads capture pose/params from a shared memory control block
/// written by the replayer (GameRecorderIPC IpcReplayer) and renders the scene to a dedicated,
/// offscreen camera. Writes raw RGBA back to a shared memory readback block.
///
/// Perspective renders a normal single view. Equirect360/Equirect180 render a world-aligned
/// cubemap via Camera.RenderToCubemap and project it with Replay/EquirectProjection.
///
/// Idle unless the control block version is > 0 (i.e. the replayer is driving output), so normal
/// renderer operation is unaffected.
/// </summary>
public class ReplayCaptureCamera : MonoBehaviour
{
    const int ControlBufferId = int.MaxValue - 2;
    const int RenderBufferId = int.MaxValue - 1;
    const int DepthBufferId = int.MaxValue - 3;
    const int SemanticBufferId = int.MaxValue - 4;
    // Shared memory capacities must be multiples of 8 (Cloudtoid requirement).
    // semanticOutput lives at offset 64; bytes 68-71 are padding.
    const int ControlBlockSize = 72;

    const int ProjectionPerspective = 0;
    const int ProjectionEquirect360 = 1;
    const int ProjectionEquirect180 = 2;

    // depthOutput control-block values
    const int DepthOutputNone = 0;
    const int DepthOutputDepthOnly = 1;
    const int DepthOutputBoth = 2;

    // Encoded mesh-asset-id denominator. assetId / 65536f is exact for ids < 2^24,
    // matching the readback RFloat precision.
    const float SemanticIdScale = 65536f;

    Camera _camera;
    Texture2D _readbackTex;
    RenderTexture _cubeRT;
    RenderTexture _outputRT;
    Material _projectMat;
    Material _projectMat180;
    int _cubeId;
    int _rotationId;
    int _uvScaleId;
    int _lastVersion = -1;
    int _width = -1;
    int _height = -1;
    bool _diagLogged;
    int _captureFrames;

    // Depth output: render the scene with a replacement shader that outputs Linear01Depth,
    // then (for equirect) project the depth cubemap to a panorama.
    RenderTexture _depthRT;
    RenderTexture _depthCubeRT;
    RenderTexture _depthOutputRT;
    Material _depthReplacementMat;
    Texture2D _depthReadbackTex;
    int _depthOutput;

    // Semantic segmentation output: same material-swap approach as depth, but each
    // renderer is swapped to a per-asset-id flat material so pixels carry their mesh
    // asset id (encoded as id / 65536f) instead of depth. Read back to SemanticBufferId.
    RenderTexture _semanticRT;
    Texture2D _semanticReadbackTex;
    Material _semanticReplacementMat;
    readonly Dictionary<float, Material> _semanticMatCache = new Dictionary<float, Material>();
    int _semanticValueId;
    int _semanticOutput;

    // Camera rotation (euler angles) per cubemap face so each face can be rendered
    // with a plain Camera.Render() call (RenderToCubemap is unreliable in this player).
    // Same order the built-in Camera360 uses: 0=+X, 1=-X, 2=+Y, 3=-Y, 4=-Z, 5=+Z.
    static readonly Vector3[] FaceEulerAngles = new Vector3[]
    {
        new Vector3(0f, -90f, 0f),   // +X
        new Vector3(0f, 90f, 0f),    // -X
        new Vector3(90f, 180f, 0f),  // +Y
        new Vector3(-90f, 180f, 0f), // -Y
        new Vector3(0f, 180f, 0f),   // -Z
        new Vector3(0f, 0f, 0f),     // +Z
    };

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var go = new GameObject("ReplayCapture");
        DontDestroyOnLoad(go);
        go.AddComponent<ReplayCaptureCamera>();
    }

    void Start()
    {
        var camGo = new GameObject("ReplayCaptureCam");
        camGo.transform.SetParent(transform, false);
        _camera = camGo.AddComponent<Camera>();
        _camera.enabled = false;
        _camera.stereoTargetEye = StereoTargetEyeMask.None;
        _camera.depthTextureMode = DepthTextureMode.Depth;
        // Mirror the renderer's on-screen camera (CameraInitializer.cs): everything
        // except Hidden/Overlay, i.e. Default + Temp + Private are all visible.
        _camera.cullingMask = ~LayerMask.GetMask(RenderHelper.HIDDEN_LAYER, RenderHelper.OVERLAY_LAYER);

        _cubeId = Shader.PropertyToID("_Cube");
        _rotationId = Shader.PropertyToID("_Rotation");
        _uvScaleId = Shader.PropertyToID("_UVCoverage");

        // Prefer the renderer's production projection material (EquirectangularProjection.mat
        // -> CubemapProjection.shader, FLIP enabled) — the exact path the built-in Camera360 uses.
        _projectMat = Resources.Load<Material>("EquirectangularProjection");
        if (_projectMat == null)
        {
            var shader = Resources.Load<Shader>("ReplayEquirectProjection");
            if (shader == null)
            {
                Debug.LogError("[ReplayCapture] No equirect projection material/shader found in resources");
            }
            else
            {
                _projectMat = new Material(shader);
            }
        }
        if (_projectMat != null)
            _projectMat.EnableKeyword("FLIP");

        // 180 material: Replay/EquirectProjection with _UVCoverage for the front hemisphere.
        var replayShader = Resources.Load<Shader>("ReplayEquirectProjection");
        if (replayShader == null)
        {
            Debug.LogError("[ReplayCapture] Shader Replay/EquirectProjection not found in resources");
        }
        else
        {
            _projectMat180 = new Material(replayShader);
            _projectMat180.EnableKeyword("FLIP");
        }

        // Depth replacement shader: renders all scene objects with a shader that outputs
        // Linear01Depth, independent of the materials the scene actually uses.
        var depthShader = Resources.Load<Shader>("ReplayDepthReplacement");
        if (depthShader == null)
        {
            Debug.LogError("[ReplayCapture] Shader ReplayDepthReplacement not found in resources");
        }
        else
        {
            _depthReplacementMat = new Material(depthShader);
        }

        // Semantic replacement shader: renders all scene objects with a flat color
        // carrying their encoded mesh asset id.
        var semanticShader = Resources.Load<Shader>("ReplaySemanticReplacement");
        if (semanticShader == null)
        {
            Debug.LogError("[ReplayCapture] Shader ReplaySemanticReplacement not found in resources");
        }
        else
        {
            _semanticReplacementMat = new Material(semanticShader);
            _semanticValueId = Shader.PropertyToID("_SemanticId");
        }

        StartCoroutine(CaptureLoop());
    }

    IEnumerator CaptureLoop()
    {
        while (true)
        {
            yield return new WaitForEndOfFrame();

            try
            {
                var rm = RenderingManager.Instance;
                if (rm == null || rm.SharedMemory == null)
                    continue;

                var control = rm.SharedMemory.AccessData<byte>(new SharedMemoryBufferDescriptor<byte>
                {
                    bufferId = ControlBufferId,
                    bufferCapacity = ControlBlockSize,
                    offset = 0,
                    length = ControlBlockSize,
                });

                int version = MemoryMarshal.Read<int>(control.Slice(28, 4));
                if (version == 0 || version == _lastVersion)
                    continue;
                _lastVersion = version;

                int projection = MemoryMarshal.Read<int>(control.Slice(32, 4));
                float fov = MemoryMarshal.Read<float>(control.Slice(36, 4));
                int width = MemoryMarshal.Read<int>(control.Slice(40, 4));
                int height = MemoryMarshal.Read<int>(control.Slice(44, 4));
                float near = MemoryMarshal.Read<float>(control.Slice(48, 4));
                float far = MemoryMarshal.Read<float>(control.Slice(52, 4));
                int clearMode = MemoryMarshal.Read<int>(control.Slice(56, 4));
                _depthOutput = MemoryMarshal.Read<int>(control.Slice(60, 4));
                _semanticOutput = MemoryMarshal.Read<int>(control.Slice(64, 4));

                if (width <= 0 || height <= 0)
                    continue;

                var pos = new Vector3(
                    MemoryMarshal.Read<float>(control.Slice(0, 4)),
                    MemoryMarshal.Read<float>(control.Slice(4, 4)),
                    MemoryMarshal.Read<float>(control.Slice(8, 4)));
                var rot = new Quaternion(
                    MemoryMarshal.Read<float>(control.Slice(12, 4)),
                    MemoryMarshal.Read<float>(control.Slice(16, 4)),
                    MemoryMarshal.Read<float>(control.Slice(20, 4)),
                    MemoryMarshal.Read<float>(control.Slice(24, 4)));

                _captureFrames++;
                if (_captureFrames <= 3 || _captureFrames % 120 == 0)
                    Debug.Log($"[ReplayCapture] Pose frame={_captureFrames} version={version} readPos=({pos.x:F3}, {pos.y:F3}, {pos.z:F3}) readRot=({rot.x:F3}, {rot.y:F3}, {rot.z:F3}, {rot.w:F3})");

                if (width != _width || height != _height)
                    ResizeBuffers(width, height);

                _camera.transform.position = pos;
                _camera.transform.rotation = rot;
                _camera.nearClipPlane = near;
                _camera.farClipPlane = far;

                if (!_diagLogged)
                {
                    _diagLogged = true;
                    LogCaptureDiagnostics();
                }

                if (projection == ProjectionPerspective)
                    RenderPerspective(width, height, fov, clearMode);
                else
                    RenderEquirect(width, height, projection, clearMode);

                var totalSize = width * height * 4;
                var data = _readbackTex.GetRawTextureData();
                if (data.Length == totalSize)
                {
                    var target = rm.SharedMemory.AccessData<byte>(new SharedMemoryBufferDescriptor<byte>
                    {
                        bufferId = RenderBufferId,
                        bufferCapacity = totalSize,
                        offset = 0,
                        length = totalSize,
                    });
                    data.AsSpan().CopyTo(target);
                }

                if (_depthOutput == DepthOutputDepthOnly || _depthOutput == DepthOutputBoth)
                {
                    var depthData = _depthReadbackTex.GetRawTextureData();
                    if (depthData.Length == totalSize)
                    {
                        var depthTarget = rm.SharedMemory.AccessData<byte>(new SharedMemoryBufferDescriptor<byte>
                        {
                            bufferId = DepthBufferId,
                            bufferCapacity = totalSize,
                            offset = 0,
                            length = totalSize,
                        });
                        depthData.AsSpan().CopyTo(depthTarget);
                    }
                }

                if (_semanticOutput != 0 && _semanticReadbackTex != null)
                {
                    var semanticData = _semanticReadbackTex.GetRawTextureData();
                    if (semanticData.Length == totalSize)
                    {
                        var semanticTarget = rm.SharedMemory.AccessData<byte>(new SharedMemoryBufferDescriptor<byte>
                        {
                            bufferId = SemanticBufferId,
                            bufferCapacity = totalSize,
                            offset = 0,
                            length = totalSize,
                        });
                        semanticData.AsSpan().CopyTo(semanticTarget);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[ReplayCapture] " + e);
            }
        }
    }

    void LogCaptureDiagnostics()
    {
        try
        {
            var planes = GeometryUtility.CalculateFrustumPlanes(_camera);
            Debug.Log($"[ReplayCapture] DIAG frame={Time.frameCount} capture cam name={_camera.name} pos={_camera.transform.position} rot={_camera.transform.eulerAngles} fov={_camera.fieldOfView} near={_camera.nearClipPlane} far={_camera.farClipPlane} mask=0x{_camera.cullingMask:X8}");
            Debug.Log($"[ReplayCapture] DIAG root {name} pos={transform.position} rot={transform.eulerAngles} scale={transform.lossyScale}");

            var main = Camera.main;
            if (main != null)
                Debug.Log($"[ReplayCapture] DIAG main cam name={main.name} pos={main.transform.position} rot={main.transform.eulerAngles} fov={main.fieldOfView} mask=0x{main.cullingMask:X8}");
            else
                Debug.Log("[ReplayCapture] DIAG main cam = null");

            int found = 0;
            foreach (var mr in UnityEngine.Object.FindObjectsOfType<UnityEngine.Renderer>())
            {
                if (mr == null)
                    continue;
                var filter = mr.GetComponent<MeshFilter>();
                int verts = filter != null && filter.sharedMesh != null ? filter.sharedMesh.vertexCount : -1;
                var b = mr.bounds;
                bool inView = GeometryUtility.TestPlanesAABB(planes, b);
                var mat = mr.sharedMaterial;
                Debug.Log($"[ReplayCapture] DIAG renderer name={mr.gameObject.name} type={mr.GetType().Name} layer={mr.gameObject.layer} enabled={mr.enabled} mat={(mat != null ? mat.name + " shader=" + mat.shader?.name : "NULL")} pos={mr.transform.position} rot={mr.transform.eulerAngles} scale={mr.transform.lossyScale} boundsC={b.center} size={b.size} verts={verts} inCaptureFrustum={inView}");
                found++;
            }
            if (found == 0)
                Debug.Log("[ReplayCapture] DIAG no Renderers found in scene");
        }
        catch (Exception e)
        {
            Debug.LogError("[ReplayCapture] DIAG failed: " + e);
        }
    }

    void ResizeBuffers(int width, int height)
    {
        if (_readbackTex != null)
            Destroy(_readbackTex);
        _readbackTex = new Texture2D(width, height, UnityEngine.TextureFormat.RGBA32, false);

        if (_depthOutput == DepthOutputDepthOnly || _depthOutput == DepthOutputBoth)
        {
            if (_depthRT != null)
                Destroy(_depthRT);
            _depthRT = new RenderTexture(width, height, 24, RenderTextureFormat.RFloat);
            _depthRT.Create();

            if (_depthReadbackTex != null)
                Destroy(_depthReadbackTex);
            _depthReadbackTex = new Texture2D(width, height, UnityEngine.TextureFormat.RFloat, false);
        }

        if (_semanticOutput != 0 && _semanticReplacementMat != null)
        {
            if (_semanticRT != null)
                Destroy(_semanticRT);
            _semanticRT = new RenderTexture(width, height, 24, RenderTextureFormat.RFloat);
            _semanticRT.Create();

            if (_semanticReadbackTex != null)
                Destroy(_semanticReadbackTex);
            _semanticReadbackTex = new Texture2D(width, height, UnityEngine.TextureFormat.RFloat, false);
        }

        _width = width;
        _height = height;
    }

    void ApplyClear(Camera cam, int clearMode)
    {
        if (clearMode == 1)
        {
            cam.clearFlags = CameraClearFlags.Skybox;
        }
        else
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
        }
    }

    void RenderPerspective(int width, int height, float fov, int clearMode)
    {
        var renderTex = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
        var prevActive = RenderTexture.active;
        try
        {
            _camera.orthographic = false;
            _camera.fieldOfView = fov;
            _camera.aspect = (float)width / height;
            _camera.targetTexture = renderTex;
            ApplyClear(_camera, clearMode);
            _camera.Render();

            RenderTexture.active = renderTex;
            _readbackTex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);

            if (_depthOutput == DepthOutputDepthOnly || _depthOutput == DepthOutputBoth)
                RenderDepth();

            if (_semanticOutput != 0)
                RenderSemanticMap();
        }
        finally
        {
            RenderTexture.active = prevActive;
            _camera.targetTexture = null;
            RenderTexture.ReleaseTemporary(renderTex);
        }
    }

    // Render depth by temporarily swapping every renderer's material to the depth
    // material, rendering, then restoring. This avoids SetReplacementShader's
    // RenderType-tag matching (which skips objects whose shader has a non-standard
    // tag, e.g. BuiltIn/Null) and works for any material.
    void RenderDepth()
    {
        if (_depthRT == null || _depthReplacementMat == null) return;

        var renderers = UnityEngine.Object.FindObjectsOfType<UnityEngine.Renderer>();
        var savedRenderers = new List<UnityEngine.Renderer>();
        var savedMaterials = new List<Material[]>();
        var prevActive = RenderTexture.active;
        var prevTarget = _camera.targetTexture;
        try
        {
            for (int n = 0; n < renderers.Length; n++)
            {
                var r = renderers[n];
                if (!r.enabled) continue;
                savedRenderers.Add(r);
                savedMaterials.Add(r.sharedMaterials);
                var depthMats = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < depthMats.Length; i++)
                    depthMats[i] = _depthReplacementMat;
                r.materials = depthMats;
            }

            _camera.targetTexture = _depthRT;
            _camera.Render();

            RenderTexture.active = _depthRT;
            _depthReadbackTex.ReadPixels(new Rect(0, 0, _width, _height), 0, 0, false);
        }
        finally
        {
            for (int n = 0; n < savedRenderers.Count; n++)
            {
                try { savedRenderers[n].materials = savedMaterials[n]; } catch { }
            }
            _camera.targetTexture = prevTarget;
            RenderTexture.active = prevActive;
        }
    }

    // Render semantic segmentation by swapping every renderer's material to a flat
    // per-mesh-asset-id material, rendering, then restoring. Perspective only for now;
    // equirect will mirror the depth cubemap path later. The encoded id (assetId /
    // 65536f) is exact for ids < 2^24, which covers all engine AssetIds in practice.
    void RenderSemanticMap()
    {
        if (_semanticRT == null || _semanticReplacementMat == null) return;

        var renderers = UnityEngine.Object.FindObjectsOfType<UnityEngine.Renderer>();
        var savedRenderers = new List<UnityEngine.Renderer>();
        var savedMaterials = new List<Material[]>();
        var prevActive = RenderTexture.active;
        var prevTarget = _camera.targetTexture;
        try
        {
            for (int n = 0; n < renderers.Length; n++)
            {
                var r = renderers[n];
                if (!r.enabled) continue;
                savedRenderers.Add(r);
                savedMaterials.Add(r.sharedMaterials);

                float encodedId = 0f;
                if (MeshAssetIdRegistry.TryGetMeshAssetId(r, out int meshAssetId) && meshAssetId >= 0)
                    encodedId = meshAssetId / SemanticIdScale;

                if (!_semanticMatCache.TryGetValue(encodedId, out var mat))
                {
                    mat = new Material(_semanticReplacementMat);
                    mat.SetFloat(_semanticValueId, encodedId);
                    _semanticMatCache[encodedId] = mat;
                }
                var semanticMats = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < semanticMats.Length; i++)
                    semanticMats[i] = mat;
                r.materials = semanticMats;
            }

            _camera.targetTexture = _semanticRT;
            _camera.Render();

            RenderTexture.active = _semanticRT;
            _semanticReadbackTex.ReadPixels(new Rect(0, 0, _width, _height), 0, 0, false);
        }
        finally
        {
            for (int n = 0; n < savedRenderers.Count; n++)
            {
                try { savedRenderers[n].materials = savedMaterials[n]; } catch { }
            }
            _camera.targetTexture = prevTarget;
            RenderTexture.active = prevActive;
        }
    }

    void RenderEquirect(int width, int height, int projection, int clearMode)
    {
        // Same approach as the built-in Camera360.RenderCubemap: render each of the 6 faces
        // with a plain Camera.Render() call and Graphics.CopyTexture them into a cubemap RT,
        // then project the cubemap to a panorama with the production projection material.

        EnsureEquirectResources(width, height);

        var prevActive = RenderTexture.active;
        var prevTarget = _camera.targetTexture;
        var prevRot = _camera.transform.rotation;
        try
        {
            int cubeSize = _cubeRT.width;

            // Pick the projection material: 180 uses Replay/EquirectProjection with the
            // front-hemisphere _UVCoverage; 360 uses the production material.
            var projMat = projection == ProjectionEquirect180 ? _projectMat180 : _projectMat;
            bool haveMat = projMat != null;

            // Material setup before rendering, matching production Camera360.RenderCubemap.
            if (haveMat)
            {
                if (projection == ProjectionEquirect180)
                    projMat.SetVector(_uvScaleId, new Vector4(Mathf.PI, Mathf.PI, -0.5f, 0f));
                projMat.SetTexture(_cubeId, _cubeRT);
                projMat.SetMatrix(_rotationId, Matrix4x4.TRS(Vector3.zero, prevRot, Vector3.one));
            }

            var faceRT = RenderTexture.GetTemporary(cubeSize, cubeSize, 24, _cubeRT.format);
            _camera.targetTexture = faceRT;
            _camera.fieldOfView = 90f;
            _camera.aspect = 1f;
            _camera.orthographic = false;
            ApplyClear(_camera, clearMode);

            // Depth cubemap: a temp face RT that we render into with depth materials.
            var depthFaceRT = (_depthOutput == DepthOutputDepthOnly || _depthOutput == DepthOutputBoth)
                && _depthReplacementMat != null && _depthCubeRT != null
                ? RenderTexture.GetTemporary(cubeSize, cubeSize, 24, RenderTextureFormat.RFloat) : null;

            // Swap all renderers to depth material for the depth faces (avoids
            // SetReplacementShader's RenderType-tag matching).
            var renderers = depthFaceRT != null ? UnityEngine.Object.FindObjectsOfType<UnityEngine.Renderer>() : null;
            var eqSavedRenderers = new List<UnityEngine.Renderer>();
            var eqSavedMaterials = new List<Material[]>();
            if (renderers != null)
            {
                for (int n = 0; n < renderers.Length; n++)
                {
                    var r = renderers[n];
                    if (!r.enabled) continue;
                    eqSavedRenderers.Add(r);
                    eqSavedMaterials.Add(r.sharedMaterials);
                    var dm = new Material[r.sharedMaterials.Length];
                    for (int i = 0; i < dm.Length; i++) dm[i] = _depthReplacementMat;
                    r.materials = dm;
                }
            }
            try
            {
                for (int f = 0; f < 6; f++)
                {
                    _camera.transform.rotation = prevRot * Quaternion.Euler(FaceEulerAngles[f]);
                    _camera.Render();
                    Graphics.CopyTexture(faceRT, 0, 0, _cubeRT, f, 0);

                    // Per-face depth into the depth cubemap.
                    if (depthFaceRT != null)
                    {
                        _camera.targetTexture = depthFaceRT;
                        _camera.Render();
                        _camera.targetTexture = faceRT;
                        Graphics.CopyTexture(depthFaceRT, 0, 0, _depthCubeRT, f, 0);
                    }
                }
            }
            finally
            {
                for (int n = 0; n < eqSavedRenderers.Count; n++)
                {
                    try { eqSavedRenderers[n].materials = eqSavedMaterials[n]; } catch { }
                }
                if (depthFaceRT != null) RenderTexture.ReleaseTemporary(depthFaceRT);
                RenderTexture.ReleaseTemporary(faceRT);
            }

            // Projection pass: clear to black (never uninitialized gray), then blit panorama.
            RenderTexture.active = _outputRT;
            GL.Clear(true, true, Color.black);
            if (haveMat)
                Graphics.Blit(Texture2D.whiteTexture, _outputRT, projMat);
            _readbackTex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);

            // Depth projection pass: project the depth cubemap to the depth output RT, then read back.
            if (_depthOutput == DepthOutputDepthOnly || _depthOutput == DepthOutputBoth)
            {
                if (haveMat && _depthCubeRT != null && _depthOutputRT != null)
                {
                    projMat.SetTexture(_cubeId, _depthCubeRT);
                    RenderTexture.active = _depthOutputRT;
                    GL.Clear(true, true, Color.black);
                    Graphics.Blit(Texture2D.whiteTexture, _depthOutputRT, projMat);
                    _depthReadbackTex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                    projMat.SetTexture(_cubeId, _cubeRT);
                    RenderTexture.active = prevActive;
                }
            }
        }
        finally
        {
            RenderTexture.active = prevActive;
            _camera.targetTexture = prevTarget;
            _camera.transform.rotation = prevRot;
        }
    }

    void EnsureEquirectResources(int width, int height)
    {
        int cubeSize = Mathf.NextPowerOfTwo((int)Mathf.Sqrt(width * height / 6f));
        if (cubeSize < 256)
            cubeSize = 256;

        if (_cubeRT == null || _cubeRT.width != cubeSize)
        {
            if (_cubeRT != null)
                Destroy(_cubeRT);
            _cubeRT = new RenderTexture(cubeSize, cubeSize, 0, RenderTextureFormat.ARGB32)
            {
                dimension = UnityEngine.Rendering.TextureDimension.Cube,
            };
            _cubeRT.Create();
        }

        if (_outputRT == null || _outputRT.width != width || _outputRT.height != height)
        {
            if (_outputRT != null)
                Destroy(_outputRT);
            _outputRT = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            _outputRT.Create();
        }

        bool wantDepth = _depthOutput == DepthOutputDepthOnly || _depthOutput == DepthOutputBoth;
        if (wantDepth)
        {
            if (_depthCubeRT == null || _depthCubeRT.width != cubeSize)
            {
                if (_depthCubeRT != null)
                    Destroy(_depthCubeRT);
                _depthCubeRT = new RenderTexture(cubeSize, cubeSize, 0, RenderTextureFormat.RFloat)
                {
                    dimension = UnityEngine.Rendering.TextureDimension.Cube,
                };
                _depthCubeRT.Create();
            }

            if (_depthOutputRT == null || _depthOutputRT.width != width || _depthOutputRT.height != height)
            {
                if (_depthOutputRT != null)
                    Destroy(_depthOutputRT);
                _depthOutputRT = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat);
                _depthOutputRT.Create();
            }
        }
    }

    void OnDestroy()
    {
        if (_readbackTex != null) Destroy(_readbackTex);
        if (_depthReadbackTex != null) Destroy(_depthReadbackTex);
        if (_semanticReadbackTex != null) Destroy(_semanticReadbackTex);
        if (_cubeRT != null) Destroy(_cubeRT);
        if (_depthCubeRT != null) Destroy(_depthCubeRT);
        if (_outputRT != null) Destroy(_outputRT);
        if (_depthOutputRT != null) Destroy(_depthOutputRT);
        if (_semanticRT != null) Destroy(_semanticRT);
        if (_projectMat != null) Destroy(_projectMat);
        if (_projectMat180 != null) Destroy(_projectMat180);
        if (_depthReplacementMat != null) Destroy(_depthReplacementMat);
        if (_semanticReplacementMat != null) Destroy(_semanticReplacementMat);
    }
}
