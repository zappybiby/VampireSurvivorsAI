using UnityEngine;

namespace AI_Mod.Runtime.Geometry
{
    internal static class WallGeometry
    {
        internal static bool CircleIntersectsBounds(Vector2 center, float radius, Bounds bounds)
        {
            var query = new Vector3(center.x, center.y, bounds.center.z);
            if (radius <= 0f)
            {
                return bounds.Contains(query);
            }

            var closest = bounds.ClosestPoint(query);
            var dx = closest.x - center.x;
            var dy = closest.y - center.y;
            var radiusSquared = radius * radius;
            return dx * dx + dy * dy <= radiusSquared;
        }

        internal static float DistanceSquaredToRect(Rect rect, Vector2 point)
        {
            var dx = 0f;
            if (point.x < rect.xMin)
            {
                dx = rect.xMin - point.x;
            }
            else if (point.x > rect.xMax)
            {
                dx = point.x - rect.xMax;
            }

            var dy = 0f;
            if (point.y < rect.yMin)
            {
                dy = rect.yMin - point.y;
            }
            else if (point.y > rect.yMax)
            {
                dy = point.y - rect.yMax;
            }

            return dx * dx + dy * dy;
        }

        internal static Vector2 ClosestPointOnRect(Rect rect, Vector2 point)
        {
            var clampedX = Mathf.Clamp(point.x, rect.xMin, rect.xMax);
            var clampedY = Mathf.Clamp(point.y, rect.yMin, rect.yMax);
            return new Vector2(clampedX, clampedY);
        }
    }
}
