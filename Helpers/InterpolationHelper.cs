using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace EFieldSimulation.Helpers;

/// <summary>
/// KD-tree for O(log N) nearest neighbor lookups on unstructured grids.
/// Used as fallback when field data is not on a structured grid.
/// </summary>
public sealed class KdTree3D
{
    private readonly int[] _indices;
    private readonly float[,] _points; // [N,3]
    private readonly int _count;

    private struct Node
    {
        public int PointIndex;
        public int SplitAxis;
        public float SplitValue;
        public int Left;   // -1 = none
        public int Right;  // -1 = none
    }

    private readonly Node[] _nodes;
    private int _nodeCount;

    public KdTree3D(float[,] points)
    {
        _points = points;
        _count = points.GetLength(0);
        _indices = Enumerable.Range(0, _count).ToArray();
        _nodes = new Node[_count];
        _nodeCount = 0;
        Build(0, _count, 0);
    }

    private int Build(int lo, int hi, int depth)
    {
        if (lo >= hi) return -1;

        int axis = depth % 3;
        int mid = (lo + hi) / 2;

        // Partial sort to find median
        NthElement(lo, hi, mid, axis);

        int nodeIdx = _nodeCount++;
        _nodes[nodeIdx] = new Node
        {
            PointIndex = _indices[mid],
            SplitAxis = axis,
            SplitValue = _points[_indices[mid], axis],
            Left = Build(lo, mid, depth + 1),
            Right = Build(mid + 1, hi, depth + 1)
        };

        return nodeIdx;
    }

    private void NthElement(int lo, int hi, int n, int axis)
    {
        while (lo < hi - 1)
        {
            //int pivot = lo;
            float pivotVal = _points[_indices[lo], axis];
            int store = lo + 1;

            for (int i = lo + 1; i < hi; i++)
            {
                if (_points[_indices[i], axis] < pivotVal)
                {
                    (_indices[i], _indices[store]) = (_indices[store], _indices[i]);
                    store++;
                }
            }

            (_indices[lo], _indices[store - 1]) = (_indices[store - 1], _indices[lo]);

            if (store - 1 == n) return;
            else if (n < store - 1) hi = store - 1;
            else lo = store;
        }
    }

    public int FindNearest(Vector3 query)
    {
        float bestDist = float.MaxValue;
        int bestIdx = 0;
        SearchNearest(0, query, ref bestDist, ref bestIdx);
        return bestIdx;
    }

    private void SearchNearest(int nodeIdx, Vector3 query,
        ref float bestDist, ref int bestIdx)
    {
        if (nodeIdx < 0) return;

        ref Node node = ref _nodes[nodeIdx];
        int pi = node.PointIndex;

        float dx = query.X - _points[pi, 0];
        float dy = query.Y - _points[pi, 1];
        float dz = query.Z - _points[pi, 2];
        float dist = dx * dx + dy * dy + dz * dz;

        if (dist < bestDist) { bestDist = dist; bestIdx = pi; }

        float queryVal = node.SplitAxis switch
        {
            0 => query.X,
            1 => query.Y,
            _ => query.Z
        };

        float diff = queryVal - node.SplitValue;
        int first = diff < 0 ? node.Left : node.Right;
        int second = diff < 0 ? node.Right : node.Left;

        SearchNearest(first, query, ref bestDist, ref bestIdx);

        if (diff * diff < bestDist)
            SearchNearest(second, query, ref bestDist, ref bestIdx);
    }
}