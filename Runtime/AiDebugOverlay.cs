using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using MelonLoader;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AI_Mod.Runtime
{
    internal sealed class AiDebugOverlay : MonoBehaviour
    {
        private const KeyCode ToggleKey = KeyCode.F6;
        private const float PanelWidth = 320f;
        private const float PanelMargin = 12f;
        private const float PanelBackgroundAlpha = 0.55f;
        private const float LineThickness = 2f;

        private readonly Color _panelColor = new Color(0f, 0f, 0f, PanelBackgroundAlpha);
        private readonly Color _textColor = Color.white;
        private readonly Color _pathColor = new Color(0.1f, 0.85f, 1f, 0.95f);
        private readonly Color _enemyColor = new Color(0.9f, 0.25f, 0.25f, 0.95f);
        private readonly Color _bulletColor = new Color(1f, 0.65f, 0.1f, 0.9f);
        private readonly Color _gemColor = new Color(0.2f, 0.95f, 0.2f, 0.95f);
        private readonly Color _playerColor = new Color(0.95f, 0.95f, 0.95f, 0.95f);
        private readonly Color _breakoutColor = new Color(1f, 0.35f, 0.6f, 0.95f);
        private readonly StringBuilder _buffer = new StringBuilder(256);
        private readonly FallbackLogger _fallbacks = new FallbackLogger();

        private bool _visible;
        private Camera? _targetCamera;
        private Texture2D? _pixel;
        private GUIStyle? _labelStyle;

        public AiDebugOverlay(IntPtr pointer) : base(pointer)
        {
        }

        public AiDebugOverlay() : base(ClassInjector.DerivedConstructorPointer<AiDebugOverlay>())
        {
            ClassInjector.DerivedConstructorBody(this);
        }

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            gameObject.hideFlags = HideFlags.HideAndDontSave;
            _pixel = CreatePixelTexture();
            MelonLogger.Msg("AI debug overlay ready.");
        }

        private void OnDestroy()
        {
            if (_pixel != null)
            {
                Destroy(_pixel);
                _pixel = null;
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(ToggleKey))
            {
                _visible = !_visible;
                MelonLogger.Msg($"AI debug overlay toggled {( _visible ? "on" : "off" )} via {ToggleKey}.");
            }

            if (_visible)
            {
                RefreshCamera();
            }
        }

        private void OnGUI()
        {
            if (!_visible || Event.current == null || Event.current.type != EventType.Repaint)
            {
                return;
            }

            var controller = AiRuntime.Controller;
            if (controller == null)
            {
                DrawStatusBanner("Controller not available. TODO: ensure AiController is attached.");
                return;
            }

            EnsureLabelStyle();
            DrawSummaryPanel(controller);

            var camera = _targetCamera;
            if (camera == null)
            {
                DrawStatusBanner("Camera unresolved. TODO: verify camera discovery.");
                return;
            }

            if (!TryComputeRenderMapping(camera, out var renderRect, out var sourceSize))
            {
                DrawStatusBanner("Render mapping unavailable. TODO: inspect camera render texture.");
                return;
            }

            DrawWorldMarkers(controller, camera, renderRect, sourceSize);
        }

        private void DrawSummaryPanel(AiController controller)
        {
            var world = controller.WorldState;
            var debug = controller.PlannerDebug;
            var planDirection = controller.LastPlan.Direction;
            var encirclement = world.Encirclement;

            _buffer.Clear();
            _buffer.AppendLine("AI Debug Overlay");
            _buffer.Append("Gameplay Active: ").Append(controller.IsGameplayActive).AppendLine();
            _buffer.Append("Game State: ").Append(controller.CurrentGameState ?? "<unknown>").AppendLine();
            _buffer.Append("Player Known: ").Append(world.Player.IsValid).AppendLine();
            _buffer.Append("Player Pos: ").Append(world.Player.Position.x.ToString("F2")).Append(", ").Append(world.Player.Position.y.ToString("F2")).AppendLine();
            _buffer.Append("Player Vel: ").Append(world.Player.Velocity.x.ToString("F2")).Append(", ").Append(world.Player.Velocity.y.ToString("F2")).AppendLine();
            _buffer.Append("Desired Dir: ").Append(planDirection.x.ToString("F2")).Append(", ").Append(planDirection.y.ToString("F2")).AppendLine();
            _buffer.Append("Plan Mode: ").Append(controller.LastPlan.Mode).AppendLine();
            _buffer.Append("Planner Score: ").Append(debug.HasBest ? debug.BestScore.ToString("F2") : "n/a").AppendLine();
            _buffer.Append("Candidates: ").Append(debug.Candidates.Count).AppendLine();
            _buffer.Append("Enemies: ").Append(world.EnemyObstacles.Count).Append(" | Bullets: ").Append(world.BulletObstacles.Count).AppendLine();
            _buffer.Append("Gems: ").Append(world.Gems.Count).Append(" | Wall Maps: ").Append(world.WallTilemaps.Count).AppendLine();
            if (debug.HasBest)
            {
                _buffer.Append("Overlap s (E/B): ")
                    .Append(debug.BestEnemyOverlapSeconds.ToString("F3")).Append(" / ")
                    .Append(debug.BestBulletOverlapSeconds.ToString("F3")).AppendLine();
            }
            _buffer.Append("Encircled: ").Append(encirclement.HasRing).Append(" | Intensity: ").Append(encirclement.Intensity.ToString("F2")).AppendLine();
            if (debug.HasBest)
            {
                _buffer.Append("Breakout Active: ").Append(debug.BreakoutActive)
                    .Append(" | Exit t: ");
                if (float.IsPositiveInfinity(debug.BestBreakoutExitTime))
                {
                    _buffer.Append("n/a");
                }
                else
                {
                    _buffer.Append(debug.BestBreakoutExitTime.ToString("F2"));
                }
                _buffer.AppendLine();
            }

            var panelHeight = 210f;
            var rect = new Rect(PanelMargin, PanelMargin, PanelWidth, panelHeight);
            DrawFilledRect(rect, _panelColor);
            GUI.Label(new Rect(rect.x + 8f, rect.y + 6f, rect.width - 12f, rect.height - 12f), _buffer.ToString(), _labelStyle);
        }

        private void DrawWorldMarkers(AiController controller, Camera camera, Rect renderRect, Vector2 sourceSize)
        {
            var world = controller.WorldState;
            var debug = controller.PlannerDebug;

            Vector2 playerScreen = default;
            var hasPlayerScreen = world.Player.IsValid && TryWorldToGui(world.Player.Position, camera, renderRect, sourceSize, out playerScreen);
            if (hasPlayerScreen)
            {
                var radius = ResolveScreenRadius(world.Player.Position, world.Player.Radius, camera, renderRect, sourceSize, 6f, "OverlayPlayerRadiusFallback", "player marker");
                DrawDisc(playerScreen, radius, _playerColor);
                if (debug.HasBest && debug.BestDirection.sqrMagnitude > 0.0001f)
                {
                    var pathStart = playerScreen;
                    var direction = debug.BestDirection.normalized;
                    const float arrowLength = 65f;
                    var pathEnd = pathStart + direction * arrowLength;
                    DrawLine(pathStart, pathEnd, _pathColor, LineThickness);
                }

                var encirclement = world.Encirclement;
                if (encirclement.HasRing && encirclement.RingRadius > 0f)
                {
                    var ringPixels = ResolveScreenRadius(world.Player.Position, encirclement.RingRadius, camera, renderRect, sourceSize, 12f, "OverlayEncirclementRadiusFallback", "encirclement ring");
                    if (ringPixels > 0f)
                    {
                        DrawCircle(playerScreen, ringPixels, _breakoutColor, 40);
                        if (encirclement.HasBreakoutDirection)
                        {
                            var breakoutLength = Mathf.Max(ringPixels + 30f, 60f);
                            var breakoutEnd = playerScreen + encirclement.BreakoutDirection * breakoutLength;
                            DrawLine(playerScreen, breakoutEnd, _breakoutColor, LineThickness * 1.1f);
                            DrawDisc(breakoutEnd, 6f, _breakoutColor);
                        }
                    }
                }
            }

            if (debug.HasBest)
            {
                DrawTrajectory(debug.BestTrajectory, camera, _pathColor, renderRect, sourceSize);
            }

            for (var i = 0; i < world.EnemyObstacles.Count; i++)
            {
                var obstacle = world.EnemyObstacles[i];
                if (TryWorldToGui(obstacle.Position, camera, renderRect, sourceSize, out var enemyScreen))
                {
                    var radius = ResolveScreenRadius(obstacle.Position, obstacle.Radius, camera, renderRect, sourceSize, 4f, "OverlayEnemyRadiusFallback", "enemy marker");
                    DrawDisc(enemyScreen, radius, _enemyColor);
                }
            }

            for (var i = 0; i < world.BulletObstacles.Count; i++)
            {
                var obstacle = world.BulletObstacles[i];
                if (TryWorldToGui(obstacle.Position, camera, renderRect, sourceSize, out var bulletScreen))
                {
                    var radius = ResolveScreenRadius(obstacle.Position, obstacle.Radius, camera, renderRect, sourceSize, 3f, "OverlayBulletRadiusFallback", "bullet marker");
                    DrawDisc(bulletScreen, radius, _bulletColor);
                }
            }

            for (var i = 0; i < world.Gems.Count; i++)
            {
                var gem = world.Gems[i];
                if (!gem.IsCollectible)
                {
                    continue;
                }

                if (TryWorldToGui(gem.Position, camera, renderRect, sourceSize, out var gemScreen))
                {
                    var radius = ResolveScreenRadius(gem.Position, gem.Radius, camera, renderRect, sourceSize, 3f, "OverlayGemRadiusFallback", "gem marker");
                    DrawDisc(gemScreen, radius, _gemColor);
                }
            }

        }

        private void DrawCircle(Vector2 center, float radius, Color color, int segments)
        {
            if (_pixel == null || radius <= 0f || segments < 3)
            {
                return;
            }

            var previous = center + new Vector2(radius, 0f);
            var step = (Mathf.PI * 2f) / segments;
            for (var i = 1; i <= segments; i++)
            {
                var angle = step * i;
                var current = center + new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
                DrawLine(previous, current, color, LineThickness * 0.6f);
                previous = current;
            }
        }

        [HideFromIl2Cpp]
        private void DrawTrajectory(IReadOnlyList<Vector2> trajectory, Camera camera, Color color, Rect renderRect, Vector2 sourceSize)
        {
            if (trajectory == null || trajectory.Count < 2)
            {
                return;
            }

            Vector2? previous = null;
            for (var i = 0; i < trajectory.Count; i++)
            {
                var node = trajectory[i];
                if (!TryWorldToGui(node, camera, renderRect, sourceSize, out var screen))
                {
                    previous = null;
                    continue;
                }

                DrawDisc(screen, 4f, color);

                if (previous.HasValue)
                {
                    DrawLine(previous.Value, screen, color, LineThickness * 0.75f);
                }

                previous = screen;
            }
        }

        private void DrawDisc(Vector2 center, float radius, Color color)
        {
            if (_pixel == null)
            {
                return;
            }

            var size = radius * 2f;
            var rect = new Rect(center.x - radius, center.y - radius, size, size);
            var cachedColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, _pixel);
            GUI.color = cachedColor;
        }

        private void DrawLine(Vector2 from, Vector2 to, Color color, float thickness)
        {
            if (_pixel == null)
            {
                return;
            }

            var delta = to - from;
            var length = delta.magnitude;
            if (length <= 0.001f)
            {
                return;
            }

            var angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
            var rect = new Rect(from.x, from.y - thickness * 0.5f, length, thickness);

            var matrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, from);

            var cachedColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, _pixel);
            GUI.color = cachedColor;

            GUI.matrix = matrix;
        }

        private void DrawStatusBanner(string message)
        {
            var rect = new Rect(PanelMargin, PanelMargin, PanelWidth, 40f);
            DrawFilledRect(rect, _panelColor);
            GUI.Label(new Rect(rect.x + 8f, rect.y + 10f, rect.width - 16f, rect.height - 20f), message, _labelStyle);
        }

        private void DrawFilledRect(Rect rect, Color color)
        {
            if (_pixel == null)
            {
                return;
            }

            var cachedColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, _pixel);
            GUI.color = cachedColor;
        }

        private void EnsureLabelStyle()
        {
            if (_labelStyle != null)
            {
                return;
            }

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                normal = { textColor = _textColor },
                richText = false,
                alignment = TextAnchor.UpperLeft
            };
        }

        private void RefreshCamera()
        {
            if (_targetCamera != null && !_targetCamera.Equals(null) && _targetCamera.isActiveAndEnabled)
            {
                return;
            }

            var camera = Camera.main;
            if (camera != null)
            {
                _targetCamera = camera;
                return;
            }

            _fallbacks.WarnOnce("CameraMainMissing", "Camera.main missing; searching all cameras for fallback. TODO: confirm camera tagging.");

            var cameras = UnityEngine.Object.FindObjectsOfType<Camera>();
            for (var i = 0; i < cameras.Length; i++)
            {
                var candidate = cameras[i];
                if (candidate != null && candidate.enabled && candidate.gameObject != null && candidate.gameObject.activeInHierarchy)
                {
                    _targetCamera = candidate;
                    _fallbacks.InfoOnce("CameraFallback", $"Using fallback camera '{candidate.gameObject.name}'.");
                    return;
                }
            }

            _fallbacks.WarnOnce("CameraUnavailable", "No active camera located for overlay rendering.");
        }

        private bool TryComputeRenderMapping(Camera camera, out Rect renderRect, out Vector2 sourceSize)
        {
            renderRect = default;
            sourceSize = default;

            var screenWidth = (float)Screen.width;
            var screenHeight = (float)Screen.height;
            if (screenWidth <= 0f || screenHeight <= 0f)
            {
                _fallbacks.WarnOnce("OverlayScreenSizeInvalid", $"Screen dimensions invalid ({screenWidth}x{screenHeight}).");
                return false;
            }

            float sourceWidth;
            float sourceHeight;

            var target = camera.targetTexture;
            if (target != null)
            {
                sourceWidth = target.width;
                sourceHeight = target.height;
            }
            else if (camera.pixelWidth > 0 && camera.pixelHeight > 0)
            {
                sourceWidth = camera.pixelWidth;
                sourceHeight = camera.pixelHeight;
                _fallbacks.InfoOnce("OverlayTargetTextureMissing", "Camera targetTexture missing; using pixelWidth/Height fallback for overlay mapping.");
            }
            else
            {
                sourceWidth = screenWidth;
                sourceHeight = screenHeight;
                _fallbacks.WarnOnce("OverlaySourceSizeFallbackScreen", "Unable to resolve camera render dimensions; falling back to Screen dimensions for overlay mapping.");
            }

            if (sourceWidth <= 0f || sourceHeight <= 0f)
            {
                _fallbacks.WarnOnce("OverlaySourceSizeInvalid", $"Camera render dimensions invalid ({sourceWidth}x{sourceHeight}).");
                return false;
            }

            var widthScale = screenWidth / sourceWidth;
            var heightScale = screenHeight / sourceHeight;
            var scale = Mathf.Min(widthScale, heightScale);

            var mappedWidth = sourceWidth * scale;
            var mappedHeight = sourceHeight * scale;
            var offsetX = (screenWidth - mappedWidth) * 0.5f;
            var offsetY = (screenHeight - mappedHeight) * 0.5f;

            renderRect = new Rect(offsetX, offsetY, mappedWidth, mappedHeight);
            sourceSize = new Vector2(sourceWidth, sourceHeight);
            return true;
        }

        private float ResolveScreenRadius(Vector2 worldPos, float worldRadius, Camera camera, Rect renderRect, Vector2 sourceSize, float minimumPixels, string fallbackKey, string description)
        {
            if (TryWorldRadiusToPixels(worldPos, worldRadius, camera, renderRect, sourceSize, out var pixels))
            {
                return Mathf.Max(minimumPixels, pixels);
            }

            _fallbacks.InfoOnce(fallbackKey, $"Overlay fallback: unable to compute screen radius for {description}; using {minimumPixels}px marker. TODO: verify camera projection.");
            return minimumPixels;
        }

        private static Texture2D CreatePixelTexture()
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixel(0, 0, Color.white);
            texture.Apply();
            return texture;
        }

        private static bool TryWorldToGui(Vector2 worldPos, Camera camera, Rect renderRect, Vector2 sourceSize, out Vector2 guiPos)
        {
            var screen = camera.WorldToScreenPoint(new Vector3(worldPos.x, worldPos.y, 0f));
            if (screen.z < 0f)
            {
                guiPos = Vector2.zero;
                return false;
            }

            if (sourceSize.x <= 0f || sourceSize.y <= 0f || renderRect.width <= 0f || renderRect.height <= 0f)
            {
                guiPos = Vector2.zero;
                return false;
            }

            var normalizedX = screen.x / sourceSize.x;
            var normalizedY = screen.y / sourceSize.y;
            var x = renderRect.x + normalizedX * renderRect.width;
            var y = renderRect.y + (1f - normalizedY) * renderRect.height;
            guiPos = new Vector2(x, y);
            return true;
        }

        private static bool TryWorldRadiusToPixels(Vector2 worldPos, float radius, Camera camera, Rect renderRect, Vector2 sourceSize, out float pixelRadius)
        {
            pixelRadius = 0f;
            if (radius <= 0f)
            {
                return false;
            }

            if (!TryWorldToGui(worldPos, camera, renderRect, sourceSize, out var center) ||
                !TryWorldToGui(worldPos + new Vector2(radius, 0f), camera, renderRect, sourceSize, out var edge))
            {
                return false;
            }

            pixelRadius = Mathf.Abs(edge.x - center.x);
            return pixelRadius > 0.1f;
        }
    }
}
