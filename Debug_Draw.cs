// Debug_Draw.cs
// IL2CPP-safe debug overlay for Vampire Survivors (MelonLoader).
// Handles RenderTexture → RawImage pipelines (with letterbox scaling).
// F8 = toggle overlay, F6/F7 = toggle autopilot, F9 = toggle sensors, F10 = cycle cameras.

using System;
using UnityEngine;
using UnityEngine.UI; // RawImage, Canvas
using Il2CppInterop.Runtime.Injection;
using EnemyController = Il2CppVampireSurvivors.Objects.Characters.EnemyController;

namespace AutoEvade
{
    // ---------- Autopilot master switch ----------
    internal static class AutoEvadeState
    {
        public static bool Enabled = true; // Toggle with F6/F7
    }

    // ---------- Debug overlay state ----------
    internal static class DebugDrawState
    {
        public static bool Enabled = true;     // F8
        public static bool ShowSensors = true; // F9

        public static Vector2 PlayerPos;
        public static Vector2 BestDir;
        public static float   PlayerSpeed;
        public static float   Lookahead;       // used for the lookahead ring

        // Called by the AI patch every frame
        public static void Update(Vector2 pos, Vector2 dir, float speed, float lookahead)
        {
            PlayerPos   = pos;
            BestDir     = dir;
            PlayerSpeed = speed;
            Lookahead   = lookahead;
        }
    }

    // No attribute — we register via ClassInjector at runtime.
    public class DebugDrawer : MonoBehaviour
    {
        // --- Required Il2Cpp constructors ---
        public DebugDrawer(IntPtr ptr) : base(ptr) { }
        public DebugDrawer() : base(ClassInjector.DerivedConstructorPointer<DebugDrawer>())
            => ClassInjector.DerivedConstructorBody(this);

        private static Texture2D _tex;

        // Gameplay camera we project with (world→viewport)
        private Camera _cam;

        // If gameplay renders to a RenderTexture shown in UI:
        private RawImage _rtPresenter;     // the RawImage that presents the RT
        private Rect _rtScreenRect;        // its rect on screen (pixels, origin bottom-left)
        private Camera _presenterEventCam; // the Canvas.worldCamera used by that RawImage

        // Likely UI/event camera (heuristic)
        private Camera _uiCam;

        // Optional player anchor (to double-check centering)
        private Transform _playerCamTarget;

        // throttles
        private int _nextCamScanFrame = 0;
        private int _nextPresenterScanFrame = 0;
        private int _nextFindPlayerTargetFrame = 0;

        // camera cycling
        private Camera[] _camList = Array.Empty<Camera>();
        private int _camIndex = -1;

        // labels
        private GUIStyle _label;
        private GUIStyle _centeredLabel;
        private int _lastLabelTryFrame = -1;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            EnsureTexture();
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8))
                DebugDrawState.Enabled = !DebugDrawState.Enabled;

            if (Input.GetKeyDown(KeyCode.F7) || Input.GetKeyDown(KeyCode.F6))
                AutoEvadeState.Enabled = !AutoEvadeState.Enabled;

            if (Input.GetKeyDown(KeyCode.F9))
                DebugDrawState.ShowSensors = !DebugDrawState.ShowSensors;

            // F10: cycle through all cameras (diagnostic)
            if (Input.GetKeyDown(KeyCode.F10))
            {
                _camList = Camera.allCameras ?? Array.Empty<Camera>();
                if (_camList.Length > 0)
                {
                    _camIndex = (_camIndex + 1) % _camList.Length;
                    _cam = _camList[_camIndex];
                }
            }

            // Auto-pick gameplay camera occasionally (unless user is cycling)
            if ((_cam == null || !_cam.isActiveAndEnabled) && Time.frameCount >= _nextCamScanFrame && _camIndex < 0)
            {
                _cam = FindGameplayCamera();
                _nextCamScanFrame = Time.frameCount + 20;
            }

            // Resolve presenter (RawImage that shows the gameplay RT) periodically even if camera.targetTexture is null,
            // because some pipelines assign the RT directly to a RawImage.
            if (Time.frameCount >= _nextPresenterScanFrame)
            {
                ResolveRenderTargetPresenter();
                _nextPresenterScanFrame = Time.frameCount + 20;
            }

            // Track likely UI/event camera (used by Screen Space - Camera canvases)
            if ((_uiCam == null || !_uiCam.isActiveAndEnabled) && Time.frameCount >= _nextPresenterScanFrame)
            {
                _uiCam = FindUiCamera();
                _nextPresenterScanFrame = Time.frameCount + 20;
            }

            // Try to locate PlayerCameraTarget occasionally
            if (_playerCamTarget == null && Time.frameCount >= _nextFindPlayerTargetFrame)
            {
                TryFindPlayerCameraTarget();
                _nextFindPlayerTargetFrame = Time.frameCount + 20;
            }
        }

        private void OnGUI()
        {
            if (!DebugDrawState.Enabled) return;
            EnsureTexture();

            // Minimal status always visible so you’re never “blind”
            if (EnsureLabelReady())
            {
                string baseStatus =
                    "AutoEvade: " + (AutoEvadeState.Enabled ? "ON" : "OFF") + " (F6/F7)   " +
                    "Debug: " + (DebugDrawState.Enabled ? "ON" : "OFF") + " (F8)   " +
                    "Sensors: " + (DebugDrawState.ShowSensors ? "ON" : "OFF") + " (F9)";
                GUI.color = Color.black; GUI.Label(new Rect(11, 11, 900, 22), baseStatus, _label);
                GUI.color = Color.white; GUI.Label(new Rect(10, 10, 900, 22), baseStatus, _label);
                GUI.color = Color.white;
            }

            if (_cam == null || _tex == null) return;

            // Determine the output rect we should draw into (in screen pixels; origin bottom-left).
            Rect outRect = GetEffectiveOutputRect();

            // WORLD anchor: prefer PlayerCameraTarget (to verify parity), otherwise PlayerPos
            Vector3 anchorW = (_playerCamTarget != null) ? _playerCamTarget.position
                                                         : new Vector3(DebugDrawState.PlayerPos.x, DebugDrawState.PlayerPos.y, 0f);

            // Convert world → GUI point inside outRect
            if (!WorldToGuiInOutputRect(anchorW, outRect, out Vector2 anchorGui)) return;

            Vector2 dir = DebugDrawState.BestDir;
            float spd   = Mathf.Max(0f, DebugDrawState.PlayerSpeed);
            float tSec  = 0.6f;
            Vector2 endW = (Vector2)anchorW + (dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector2.zero) * spd * tSec;
            if (!WorldToGuiInOutputRect(new Vector3(endW.x, endW.y, 0f), outRect, out Vector2 endGui)) return;

            // Outline the output rect so we can verify alignment
            DrawScreenRectOutline(outRect, new Color(0f, 0f, 0f, 0.35f));

            // Camera target marker
            if (_playerCamTarget != null)
                DrawCross(anchorGui, 10f, Color.magenta);

            // Player anchor + planned path
            DrawBox(anchorGui, 8f, Color.cyan);
            DrawLine(anchorGui, endGui, 2f, Color.yellow);
            DrawBox(endGui, 6f, Color.yellow);

            // Angle label near the player
            if (EnsureLabelReady())
            {
                float angDeg = (dir.sqrMagnitude > 1e-6f) ? Mathf.Repeat(Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg + 360f, 360f) : 0f;
                string tag = "θ " + angDeg.ToString("0") + "°  v " + spd.ToString("0.00") + "\nCam: " + _cam.name;
                Rect r = new Rect(anchorGui.x - 90f, anchorGui.y - 36f, 180f, 34f);
                GUI.color = Color.black; GUI.Label(new Rect(r.x+1, r.y+1, r.width, r.height), tag, _centeredLabel);
                GUI.color = Color.white; GUI.Label(r, tag, _centeredLabel);
                GUI.color = Color.white;
            }

            // Sensors: lookahead ring, walls, enemy LoS
            if (DebugDrawState.ShowSensors)
            {
                DrawWorldCircleInRect((Vector2)anchorW, DebugDrawState.Lookahead, 36, outRect, new Color(1f,1f,0f,0.6f));

                var walls = WallCache.WallRects;
                for (int i = 0; i < walls.Count; i++)
                    DrawWorldRectOutlineInRect(walls[i], outRect, new Color(0f, 1f, 0.2f, 0.6f));

                EnemyController[] enemies = null;
                try { enemies = UnityEngine.Object.FindObjectsOfType<EnemyController>(); } catch { }
                if (enemies != null)
                {
                    for (int i = 0; i < enemies.Length; i++)
                    {
                        var e = enemies[i];
                        if (e == null || e.IsDead) continue;

                        Vector3 ew = e._cachedTransform != null ? e._cachedTransform.position : Vector3.zero;
                        if (!WorldToGuiInOutputRect(ew, outRect, out Vector2 eg)) continue;

                        // LoS test vs walls (world space)
                        bool blocked = false;
                        Vector2 p0 = (Vector2)anchorW;
                        Vector2 p1 = new Vector2(ew.x, ew.y);
                        var wr = WallCache.WallRects;
                        for (int r = 0; r < wr.Count; r++)
                        {
                            if (WallCache.SegmentIntersectsRect(p0, p1, wr[r])) { blocked = true; break; }
                        }

                        DrawBox(eg, 6f, blocked ? new Color(0.65f,0.65f,0.65f,0.95f) : new Color(1f,0.2f,0.2f,0.95f));
                        DrawLine(anchorGui, eg, 1.5f, blocked ? new Color(0.65f,0.65f,0.65f,0.6f) : new Color(1f,0.2f,0.2f,0.8f));
                    }
                }
            }

            // Diagnostics (always show)
            if (EnsureLabelReady())
            {
                string presenterCam = (_presenterEventCam != null) ? _presenterEventCam.name : "<none>";
                string uiCam = (_uiCam != null) ? _uiCam.name : "<none>";
                string srcStr = "unknown";
                if (_rtPresenter != null && _rtPresenter.texture is RenderTexture rtA)
                    srcStr = rtA.width.ToString() + "x" + rtA.height.ToString();
                else if (_cam != null)
                    srcStr = _cam.pixelWidth.ToString() + "x" + _cam.pixelHeight.ToString();

                string diag = "OutRect: " + outRect.xMin.ToString("0") + "," + outRect.yMin.ToString("0") + " " +
                              outRect.width.ToString("0") + "x" + outRect.height.ToString("0") +
                              "    Src: " + srcStr +
                              "    PresenterCam: " + presenterCam +
                              "    UICam: " + uiCam +
                              "    F10: cycle cameras (" + Camera.allCamerasCount + " found)";

                Rect r = new Rect(10, 36, 1300, 60);
                GUI.color = Color.black; GUI.Label(new Rect(r.x+1, r.y+1, r.width, r.height), diag, _label);
                GUI.color = Color.white; GUI.Label(r, diag, _label);
                GUI.color = Color.white;
            }
        }

        // ---------- Camera + output rect discovery ----------

        private Camera FindGameplayCamera()
        {
            try
            {
                Camera[] cams = Camera.allCameras;
                if (cams == null || cams.Length == 0) return Camera.main;

                Camera best = null;
                float bestScore = float.NegativeInfinity;

                for (int i = 0; i < cams.Length; i++)
                {
                    Camera c = cams[i];
                    if (c == null || !c.isActiveAndEnabled) continue;

                    float score = 0f;

                    // Strongly prefer non-UI cameras
                    string nm = c.gameObject != null && c.gameObject.name != null ? c.gameObject.name : "";
                    if (nm.IndexOf("UI", StringComparison.OrdinalIgnoreCase) >= 0) score -= 2.0f;

                    // Bonus for gameplay systems and RT usage
                    if (c.targetTexture != null) score += 2.0f;
                    score += c.depth * 0.5f;
                    if (nm.IndexOf("Main Camera", StringComparison.OrdinalIgnoreCase) >= 0) score += 0.5f;

                    // Components hint
                    var comps = c.gameObject.GetComponents<UnityEngine.Component>();
                    for (int k = 0; k < comps.Length; k++)
                    {
                        var comp = comps[k];
                        if (comp == null) continue;
                        string t = comp.GetType().FullName ?? "";
                        if (t.Contains("ProCamera2D") || t.Contains("PhaserCamera")) score += 0.75f;
                        if (t.Contains("UICamera")) score -= 2.0f;
                    }

                    // PixelRect check (fullscreen-ish bonus)
                    Rect r = c.pixelRect;
                    bool fullscreen = Mathf.Approximately(r.x, 0f) && Mathf.Approximately(r.y, 0f) &&
                                      Mathf.Abs(r.width - Screen.width) < 2f &&
                                      Mathf.Abs(r.height - Screen.height) < 2f;
                    if (fullscreen) score += 0.25f;

                    if (score > bestScore) { bestScore = score; best = c; }
                }
                return best ?? Camera.main ?? cams[0];
            }
            catch { return Camera.main; }
        }

        private Camera FindUiCamera()
        {
            try
            {
                Camera[] cams = Camera.allCameras;
                Camera best = null;
                for (int i = 0; i < cams.Length; i++)
                {
                    Camera c = cams[i];
                    if (c == null || !c.isActiveAndEnabled) continue;
                    string nm = c.gameObject != null && c.gameObject.name != null ? c.gameObject.name : "";

                    if (nm.IndexOf("UI", StringComparison.OrdinalIgnoreCase) >= 0) { best = c; break; }

                    var comps = c.gameObject.GetComponents<UnityEngine.Component>();
                    for (int k = 0; k < comps.Length; k++)
                    {
                        var comp = comps[k];
                        if (comp == null) continue;
                        string t = comp.GetType().FullName ?? "";
                        if (t.Contains("UICamera")) { best = c; break; }
                    }
                    if (best != null) break;
                }
                return best;
            }
            catch { return null; }
        }

        private Camera GetEventCamForCanvas(Canvas canvas)
        {
            if (canvas == null) return null;

            RenderMode mode = canvas.renderMode;
            if (mode == RenderMode.ScreenSpaceOverlay)
                return null; // Overlay must use null eventCam

            if (mode == RenderMode.ScreenSpaceCamera)
                return (canvas.worldCamera != null) ? canvas.worldCamera : (_uiCam != null ? _uiCam : _cam);

            // World Space
            return (_uiCam != null) ? _uiCam : _cam;
        }

        private void ResolveRenderTargetPresenter()
        {
            _rtPresenter = null;
            _rtScreenRect = default(Rect);
            _presenterEventCam = null;

            try
            {
                // 1) Fast path: known name
                GameObject go = GameObject.Find("GameRenderOutput");
                if (go != null)
                {
                    RawImage ri2 = go.GetComponent<RawImage>();
                    if (ri2 != null && ri2.isActiveAndEnabled && ri2.texture is RenderTexture)
                    {
                        Canvas canvas = ri2.canvas;
                        Camera evCam  = GetEventCamForCanvas(canvas);
                        _rtPresenter = ri2;
                        _presenterEventCam = evCam;
                        _rtScreenRect = RectTransformToScreenSpace(ri2.rectTransform, evCam);
                        if (_rtScreenRect.width > 2f && _rtScreenRect.height > 2f) return;
                    }
                }

                // 2) General case: largest active RawImage that shows a RenderTexture
                RawImage best = null;
                Rect bestRect = default(Rect);
                Camera bestEvCam = null;
                float bestArea = 0f;

                RawImage[] all = Resources.FindObjectsOfTypeAll<RawImage>();
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        RawImage ri = all[i];
                        if (ri == null || !ri.isActiveAndEnabled) continue;
                        if (ri.texture == null || !(ri.texture is RenderTexture)) continue;

                        Canvas canvas = ri.canvas;
                        Camera evCam  = GetEventCamForCanvas(canvas);
                        Rect rect  = RectTransformToScreenSpace(ri.rectTransform, evCam);
                        float area = rect.width * rect.height;
                        if (rect.width <= 2f || rect.height <= 2f) continue;

                        if (area > bestArea)
                        {
                            bestArea = area;
                            best = ri;
                            bestRect = rect;
                            bestEvCam = evCam;
                        }
                    }
                }

                if (best != null)
                {
                    _rtPresenter = best;
                    _rtScreenRect = bestRect;
                    _presenterEventCam = bestEvCam;
                    return;
                }
            }
            catch
            {
                // ignore
            }
        }

        // --- Output rect with robust letterbox correction ---
        private Rect GetEffectiveOutputRect()
        {
            // If a UI RawImage is presenting the RT, start with its rect
            if (_rtPresenter != null)
            {
                Rect r = _rtScreenRect;

                RenderTexture rt = _rtPresenter.texture as RenderTexture;
                if (rt != null)
                {
                    // If the presenter rect looks like the *raw RT size* at (0,0), or is much smaller than screen,
                    // compute the centered, aspect-fit screen rect instead.
                    bool looksRaw =
                        Mathf.Abs(r.xMin) < 2f && Mathf.Abs(r.yMin) < 2f &&
                        Mathf.Abs(r.width - rt.width) <= 2f &&
                        Mathf.Abs(r.height - rt.height) <= 2f;

                    bool muchSmaller =
                        r.width < Screen.width * 0.8f && r.height < Screen.height * 0.8f;

                    if (looksRaw || muchSmaller)
                        return FitToScreen(rt.width, rt.height);
                }

                return r;
            }

            // Otherwise, fit by camera pixel dims/aspect.
            if (_cam != null)
            {
                int pw = _cam.pixelWidth;
                int ph = _cam.pixelHeight;
                if (pw > 0 && ph > 0)
                    return FitToScreen(pw, ph);
            }

            return new Rect(0f, 0f, Screen.width, Screen.height);
        }

        private Rect FitToScreen(float srcW, float srcH)
        {
            float sw = Screen.width;
            float sh = Screen.height;
            if (srcW <= 0f || srcH <= 0f || sw <= 0f || sh <= 0f)
                return new Rect(0f, 0f, sw, sh);

            float srcAspect = srcW / srcH;
            float screenAspect = sw / sh;

            float outW, outH, x, y;
            if (screenAspect > srcAspect)
            {
                // pillarbox: match height
                outH = sh;
                outW = outH * srcAspect;
                x = (sw - outW) * 0.5f;
                y = 0f;
            }
            else
            {
                // letterbox: match width
                outW = sw;
                outH = outW / srcAspect;
                x = 0f;
                y = (sh - outH) * 0.5f;
            }
            return new Rect(x, y, outW, outH);
        }

        private Rect RectTransformToScreenSpace(RectTransform t, Camera eventCam)
        {
            try
            {
                Vector3[] corners = new Vector3[4];
                t.GetWorldCorners(corners);

                // Use the canvas’ worldCamera if Screen Space - Camera (null for Overlay)
                for (int i = 0; i < 4; i++)
                    corners[i] = RectTransformUtility.WorldToScreenPoint(eventCam, corners[i]);

                float xMin = Mathf.Min(Mathf.Min(corners[0].x, corners[1].x), Mathf.Min(corners[2].x, corners[3].x));
                float xMax = Mathf.Max(Mathf.Max(corners[0].x, corners[1].x), Mathf.Max(corners[2].x, corners[3].x));
                float yMin = Mathf.Min(Mathf.Min(corners[0].y, corners[1].y), Mathf.Min(corners[2].y, corners[3].y));
                float yMax = Mathf.Max(Mathf.Max(corners[0].y, corners[1].y), Mathf.Max(corners[2].y, corners[3].y));

                // MinMaxRect(xmin, ymin, xmax, ymax)
                return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
            }
            catch
            {
                return new Rect(0, 0, Screen.width, Screen.height);
            }
        }

        // ---------- World→GUI mapping inside output rect ----------

        private bool WorldToGuiInOutputRect(Vector3 world, Rect outRect, out Vector2 gui)
        {
            gui = default(Vector2);

            Vector3 vp;
            try { vp = _cam.WorldToViewportPoint(world); }
            catch { return false; }

            if (vp.z < 0f) return false; // behind camera

            // Map viewport [0..1] to *output rect in screen pixels* (origin bottom-left), then flip Y for OnGUI.
            float sx = outRect.xMin + vp.x * outRect.width;
            float sy = outRect.yMin + vp.y * outRect.height;
            gui = new Vector2(sx, Screen.height - sy);

            return true;
        }

        // ---------- Visual helpers ----------

        private void EnsureTexture()
        {
            if (_tex != null) return;
            try
            {
                _tex = new Texture2D(1, 1);
                _tex.SetPixel(0, 0, Color.white);
                _tex.Apply();
            }
            catch { _tex = null; }
        }

        private bool EnsureLabelReady()
        {
            if (_label != null && _centeredLabel != null) return true;
            if (_lastLabelTryFrame == Time.frameCount) return false;
            _lastLabelTryFrame = Time.frameCount;

            try
            {
                GUISkin skin = GUI.skin;
                if (skin == null) return false;
                GUIStyle baseLabel = skin.label;
                if (baseLabel == null) return false;

                _label = new GUIStyle(baseLabel) { fontSize = 14, alignment = TextAnchor.UpperLeft };
                _centeredLabel = new GUIStyle(baseLabel) { fontSize = 14, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                return true;
            }
            catch { _label = null; _centeredLabel = null; return false; }
        }

        private static void DrawBox(Vector2 center, float size, Color c)
        {
            if (_tex == null) return;
            Rect r = new Rect(center.x - size / 2f, center.y - size / 2f, size, size);
            GUI.color = c; GUI.DrawTexture(r, _tex); GUI.color = Color.white;
        }

        private static void DrawCross(Vector2 center, float size, Color c)
        {
            if (_tex == null) return;
            GUI.color = c;
            float h = size / 2f;
            GUI.DrawTexture(new Rect(center.x - h, center.y - 1f, size, 2f), _tex);
            GUI.DrawTexture(new Rect(center.x - 1f, center.y - h, 2f, size), _tex);
            GUI.color = Color.white;
        }

        private static void DrawLine(Vector2 a, Vector2 b, float thickness, Color c)
        {
            if (_tex == null) return;
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 0.5f) return;
            float ang = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;

            GUI.color = c;
            Matrix4x4 m = GUI.matrix;
            GUIUtility.RotateAroundPivot(ang, a);
            GUI.DrawTexture(new Rect(a.x, a.y - thickness / 2f, len, thickness), _tex);
            GUI.matrix = m;
            GUI.color = Color.white;
        }

        private void DrawScreenRectOutline(Rect r, Color c)
        {
            // r is in screen coords, origin bottom-left. Convert to GUI (top-left) when drawing.
            Vector2 tl = new Vector2(r.xMin, Screen.height - r.yMax);
            Vector2 tr = new Vector2(r.xMax, Screen.height - r.yMax);
            Vector2 bl = new Vector2(r.xMin, Screen.height - r.yMin);
            Vector2 br = new Vector2(r.xMax, Screen.height - r.yMin);

            DrawLine(tl, tr, 1.5f, c);
            DrawLine(tr, br, 1.5f, c);
            DrawLine(br, bl, 1.5f, c);
            DrawLine(bl, tl, 1.5f, c);
        }

        private void DrawWorldRectOutlineInRect(Rect rWorld, Rect outRect, Color c)
        {
            Vector3 a = new Vector3(rWorld.xMin, rWorld.yMin, 0f);
            Vector3 b = new Vector3(rWorld.xMax, rWorld.yMin, 0f);
            Vector3 c3 = new Vector3(rWorld.xMax, rWorld.yMax, 0f);
            Vector3 d = new Vector3(rWorld.xMin, rWorld.yMax, 0f);

            Vector2 ag, bg, cg, dg;
            if (!WorldToGuiInOutputRect(a, outRect, out ag)) return;
            if (!WorldToGuiInOutputRect(b, outRect, out bg)) return;
            if (!WorldToGuiInOutputRect(c3, outRect, out cg)) return;
            if (!WorldToGuiInOutputRect(d, outRect, out dg)) return;

            DrawLine(ag, bg, 1.2f, c);
            DrawLine(bg, cg, 1.2f, c);
            DrawLine(cg, dg, 1.2f, c);
            DrawLine(dg, ag, 1.2f, c);
        }

        private void DrawWorldCircleInRect(Vector2 centerWorld, float radiusWorld, int segments, Rect outRect, Color c)
        {
            if (segments < 8) segments = 8;

            Vector2 prevGui = default(Vector2);
            bool havePrev = false;
            GUI.color = c;
            for (int i = 0; i <= segments; i++)
            {
                float t = (i / (float)segments) * Mathf.PI * 2f;
                Vector2 pW = centerWorld + new Vector2(Mathf.Cos(t), Mathf.Sin(t)) * radiusWorld;
                Vector2 pG;
                if (!WorldToGuiInOutputRect(new Vector3(pW.x, pW.y, 0f), outRect, out pG)) continue;

                if (havePrev) GUI.DrawTexture(LineRect(prevGui, pG, 1.5f), _tex);
                prevGui = pG;
                havePrev = true;
            }
            GUI.color = Color.white;
        }

        private static Rect LineRect(Vector2 a, Vector2 b, float thickness)
        {
            Vector2 d = b - a;
            float len = Mathf.Max(0f, d.magnitude);
            float ang = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
            Matrix4x4 m = GUI.matrix;
            GUIUtility.RotateAroundPivot(ang, a);
            Rect rect = new Rect(a.x, a.y - thickness / 2f, len, thickness);
            GUI.matrix = m;
            return rect;
        }

        private void TryFindPlayerCameraTarget()
        {
            try
            {
                GameObject go = GameObject.Find("PlayerCameraTarget")
                      ?? GameObject.Find("Player Camera Target")
                      ?? GameObject.Find("CameraTarget")
                      ?? GameObject.Find("Camera Target");
                _playerCamTarget = go != null ? go.transform : null;
            }
            catch
            {
                _playerCamTarget = null;
            }
        }
    }
}
