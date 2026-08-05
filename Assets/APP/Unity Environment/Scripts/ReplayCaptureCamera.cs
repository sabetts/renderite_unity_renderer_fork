using System;
using System.Collections;
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
    const int ControlBlockSize = 64;

    const int ProjectionPerspective = 0;
    const int ProjectionEquirect360 = 1;
    const int ProjectionEquirect180 = 2;

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
                var target = rm.SharedMemory.AccessData<byte>(new SharedMemoryBufferDescriptor<byte>
                {
                    bufferId = RenderBufferId,
                    bufferCapacity = totalSize,
                    offset = 0,
                    length = totalSize,
                });

                var data = _readbackTex.GetRawTextureData();
                if (data.Length == totalSize)
                    data.AsSpan().CopyTo(target);
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
        }
        finally
        {
            RenderTexture.active = prevActive;
            _camera.targetTexture = null;
            RenderTexture.ReleaseTemporary(renderTex);
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
            try
            {
                for (int f = 0; f < 6; f++)
                {
                    _camera.transform.rotation = prevRot * Quaternion.Euler(FaceEulerAngles[f]);
                    _camera.Render();
                    Graphics.CopyTexture(faceRT, 0, 0, _cubeRT, f, 0);
                }
            }
            finally
            {
                RenderTexture.ReleaseTemporary(faceRT);
            }

            // Projection pass: clear to black (never uninitialized gray), then blit panorama.
            RenderTexture.active = _outputRT;
            GL.Clear(true, true, Color.black);
            if (haveMat)
                Graphics.Blit(Texture2D.whiteTexture, _outputRT, projMat);
            _readbackTex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
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
    }

    void OnDestroy()
    {
        if (_readbackTex != null) Destroy(_readbackTex);
        if (_cubeRT != null) Destroy(_cubeRT);
        if (_outputRT != null) Destroy(_outputRT);
        if (_projectMat != null) Destroy(_projectMat);
        if (_projectMat180 != null) Destroy(_projectMat180);
    }
}
