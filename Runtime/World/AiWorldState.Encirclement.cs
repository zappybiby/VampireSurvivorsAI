using System;
using System.Collections.Generic;
using UnityEngine;

namespace AI_Mod.Runtime
{
    internal sealed partial class AiWorldState
    {
        private void RefreshEncirclement()
        {
            _encirclement = EncirclementSnapshot.Empty;
            if (!Player.IsValid || _enemies.Count == 0)
            {
                return;
            }

            var playerPosition = Player.Position;
            var playerRadius = Mathf.Max(Player.Radius, 0.1f);
            var enemyCount = _enemies.Count;
            if (enemyCount < 4)
            {
                return;
            }

            Span<int> binCounts = stackalloc int[EncirclementSnapshot.BinCount];
            Span<float> binNearest = stackalloc float[EncirclementSnapshot.BinCount];
            for (var i = 0; i < EncirclementSnapshot.BinCount; i++)
            {
                binCounts[i] = 0;
                binNearest[i] = float.PositiveInfinity;
            }

            var distances = new List<float>(enemyCount);
            for (var i = 0; i < enemyCount; i++)
            {
                var enemy = _enemies[i];
                var offset = enemy.Position - playerPosition;
                var distance = offset.magnitude;
                distances.Add(distance);

                var angle = Mathf.Atan2(offset.y, offset.x);
                var normalized = (angle + Mathf.PI) / (Mathf.PI * 2f);
                if (normalized < 0f)
                {
                    normalized += 1f;
                }
                else if (normalized >= 1f)
                {
                    normalized -= 1f;
                }

                var binIndex = Mathf.Clamp((int)(normalized * EncirclementSnapshot.BinCount), 0, EncirclementSnapshot.BinCount - 1);
                binCounts[binIndex]++;
                if (distance < binNearest[binIndex])
                {
                    binNearest[binIndex] = distance;
                }
            }

            distances.Sort();
            var medianDistance = ComputeMedian(distances);
            var radialDeviation = ComputeMedianAbsoluteDeviation(distances, medianDistance);
            var ringHalfWidth = Mathf.Max(playerRadius, radialDeviation * 1.5f);
            var exitRadius = medianDistance + ringHalfWidth;

            var occupiedBins = 0;
            var maxBinCount = 0;
            for (var i = 0; i < EncirclementSnapshot.BinCount; i++)
            {
                var count = binCounts[i];
                if (count > 0)
                {
                    maxBinCount = Mathf.Max(maxBinCount, count);
                    var nearest = binNearest[i];
                    if (nearest <= medianDistance + ringHalfWidth)
                    {
                        occupiedBins++;
                    }
                }
            }

            if (occupiedBins == 0)
            {
                return;
            }

            var coverage = occupiedBins / (float)EncirclementSnapshot.BinCount;
            var tightness = medianDistance > 0f
                ? Mathf.Clamp01(1f - (radialDeviation / (medianDistance + 0.0001f)))
                : 0f;
            var closeness = Mathf.Clamp01((playerRadius + ringHalfWidth) / (playerRadius + medianDistance + ringHalfWidth));
            var intensity = Mathf.Clamp01(coverage * tightness * closeness);
            if (intensity <= 0f)
            {
                return;
            }

            var bestGapIndex = -1;
            var bestGapScore = float.NegativeInfinity;
            for (var i = 0; i < EncirclementSnapshot.BinCount; i++)
            {
                var count = binCounts[i];
                var normalizedCount = maxBinCount > 0 ? 1f - (count / (float)maxBinCount) : 1f;
                var nearest = binNearest[i];
                if (float.IsPositiveInfinity(nearest))
                {
                    nearest = exitRadius;
                }

                var radialGap = Mathf.Clamp01((nearest - medianDistance) / (ringHalfWidth + 0.0001f));
                var gapScore = normalizedCount + radialGap;
                if (gapScore > bestGapScore)
                {
                    bestGapScore = gapScore;
                    bestGapIndex = i;
                }
            }

            if (bestGapIndex < 0)
            {
                return;
            }

            var gapAngle = ((bestGapIndex + 0.5f) / EncirclementSnapshot.BinCount) * Mathf.PI * 2f - Mathf.PI;
            var breakoutDirection = new Vector2(Mathf.Cos(gapAngle), Mathf.Sin(gapAngle));
            if (breakoutDirection.sqrMagnitude > 0.0001f)
            {
                breakoutDirection.Normalize();
            }
            else
            {
                breakoutDirection = Vector2.zero;
            }

            var gapOccupancy = bestGapScore > 0f ? Mathf.Clamp01(1f - (bestGapScore * 0.5f)) : 1f;

            _encirclement = new EncirclementSnapshot(
                true,
                coverage,
                tightness,
                closeness,
                intensity,
                breakoutDirection,
                gapOccupancy,
                medianDistance,
                ringHalfWidth,
                exitRadius);
        }

        private static float ComputeMedian(IReadOnlyList<float> samples)
        {
            var count = samples.Count;
            if (count == 0)
            {
                return 0f;
            }

            var mid = count / 2;
            if ((count & 1) == 0)
            {
                return 0.5f * (samples[mid - 1] + samples[mid]);
            }

            return samples[mid];
        }

        private static float ComputeMedianAbsoluteDeviation(IReadOnlyList<float> orderedSamples, float median)
        {
            var count = orderedSamples.Count;
            if (count == 0)
            {
                return 0f;
            }

            var deviations = new float[count];
            for (var i = 0; i < count; i++)
            {
                deviations[i] = Mathf.Abs(orderedSamples[i] - median);
            }

            Array.Sort(deviations);
            return ComputeMedian(deviations);
        }
    }
}
