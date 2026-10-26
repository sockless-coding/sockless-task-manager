namespace Sockless.TaskManager.Features.Processes.Dotnet;

/// <summary>
/// The heap as a graph: objects, the references between them, and the roots that keep them alive,
/// as a heap walk describes them. From it, how much each type keeps alive: an object's retained
/// size is what would be freed if it were gone, its subtree in the dominator tree. Used once:
/// working that out releases the graph as it goes.
/// </summary>
internal sealed class HeapGraph
{
    /// <summary>
    /// Beyond this many objects the graph isn't kept. Collecting it and working out the dominators
    /// peaks at about 135 bytes an object (measured: 473 MB for 3.5 million), so some 650 MB at
    /// the limit, for a few seconds; it's all released once the totals are in.
    /// </summary>
    internal const int MaxObjects = 5_000_000;

    private readonly List<ulong> _addresses = [];
    private readonly List<long> _sizes = [];
    private readonly List<int> _types = [];
    private readonly List<int> _edgeCounts = [];
    private readonly List<ulong> _edgeTargets = [];
    private readonly List<ulong> _roots = [];
    private readonly List<(ulong Key, ulong Value)> _dependents = [];
    private readonly Dictionary<ulong, int> _typeIndex = [];
    private readonly List<ulong> _typeIds = [];

    /// <summary>Too many objects: nothing is kept.</summary>
    public bool IsTooLarge { get; private set; }

    /// <summary>Objects arrive in order; their references follow, in the same order.</summary>
    public void AddObject(ulong address, long size, ulong type, int edges)
    {
        if (IsTooLarge)
            return;
        if (_sizes.Count >= MaxObjects)
        {
            IsTooLarge = true;
            Release();
            return;
        }

        if (!_typeIndex.TryGetValue(type, out var typeIndex))
        {
            _typeIndex[type] = typeIndex = _typeIds.Count;
            _typeIds.Add(type);
        }

        _addresses.Add(address);
        _sizes.Add(size);
        _types.Add(typeIndex);
        _edgeCounts.Add(edges);
    }

    public void AddEdge(ulong target)
    {
        if (!IsTooLarge)
            _edgeTargets.Add(target);
    }

    public void AddRoot(ulong address) => _roots.Add(address);

    /// <summary>A conditional weak table entry: the value lives as long as the key does.</summary>
    public void AddDependent(ulong key, ulong value) => _dependents.Add((key, value));

    /// <summary>
    /// What the objects of each type keep alive, by type id. An object inside another of its own
    /// type (a linked list's nodes) counts once, through the outermost.
    /// </summary>
    public Dictionary<ulong, long> RetainedByType()
    {
        // Node 0 is a virtual root over every root; objects are 1..n.
        var n = _sizes.Count + 1;
        var (vertex, idom) = Dominators(n);

        // Retained sizes: each object's own, plus what it dominates. A dominator comes before
        // what it dominates in preorder, so going backwards finishes each subtree first.
        var retained = new long[n];
        for (var i = n - 1; i > 0; i--)
        {
            retained[i] += _sizes[vertex[i] - 1];
            retained[idom[i]] += retained[i];
        }

        var byType = SumByType(n, idom, retained, vertex);
        Release();
        return byType;
    }

    private void Release()
    {
        Free(_addresses);
        Free(_sizes);
        Free(_types);
        Free(_edgeCounts);
        Free(_edgeTargets);
        Free(_roots);
        Free(_dependents);
    }

    private static void Free<T>(List<T> list)
    {
        list.Clear();
        list.TrimExcess();
    }

    /// <summary>
    /// Objects in preorder from the roots, and each one's immediate dominator by preorder number.
    /// Objects the roots don't reach (held by a root of a kind the walk doesn't report) hang off
    /// the virtual root too.
    /// </summary>
    private (int[] Vertex, int[] Idom) Dominators(int n)
    {
        var (successorStart, successors) = Successors(n);

        var number = new int[n];
        Array.Fill(number, -1);
        var vertex = new int[n];  // node by preorder number
        var parent = new int[n];  // by number
        var extraRoots = new List<int>();
        var count = 0;
        var stack = new Stack<(int Node, int Next)>();
        for (var start = 0; start < n; start++)
        {
            if (number[start] >= 0)
                continue;
            if (start != 0)
                extraRoots.Add(start);

            parent[count] = 0;
            number[start] = count;
            vertex[count++] = start;
            stack.Push((start, successorStart[start]));
            while (stack.Count > 0)
            {
                var (node, next) = stack.Pop();
                if (next >= successorStart[node + 1])
                    continue;

                stack.Push((node, next + 1));
                var child = successors[next];
                if (number[child] >= 0)
                    continue;

                parent[count] = number[node];
                number[child] = count;
                vertex[count++] = child;
                stack.Push((child, successorStart[child]));
            }
        }

        var (predecessorStart, predecessors) = Predecessors(n, successorStart, successors, extraRoots);
        return (vertex, LengauerTarjan(n, number, vertex, parent, predecessorStart, predecessors));
    }

    /// <summary>
    /// The references, by object, as offsets into one array, with addresses resolved to objects.
    /// References to anything the walk didn't describe are dropped. Releases the references.
    /// </summary>
    private (int[] Start, int[] Targets) Successors(int n)
    {
        // Objects by address, for a binary search: the walk goes heap region by heap region,
        // which needn't be in address order.
        var addresses = _addresses.ToArray();
        Free(_addresses);
        var nodes = new int[addresses.Length];
        for (var i = 0; i < nodes.Length; i++)
            nodes[i] = i + 1;
        Array.Sort(addresses, nodes);

        int Find(ulong address)
        {
            var i = Array.BinarySearch(addresses, address);
            return i >= 0 ? nodes[i] : -1;
        }

        var dependentsOf = new Dictionary<int, List<int>>();
        foreach (var (key, value) in _dependents)
        {
            if (Find(key) is var k and > 0 && Find(value) is var v and > 0)
            {
                if (!dependentsOf.TryGetValue(k, out var list))
                    dependentsOf[k] = list = [];
                list.Add(v);
            }
        }

        var start = new int[n + 1];
        var targets = new int[_roots.Count + _edgeTargets.Count + _dependents.Count];
        var count = 0;
        foreach (var root in _roots)
        {
            if (Find(root) is var r and > 0)
                targets[count++] = r;
        }

        var edge = 0;
        for (var node = 1; node < n; node++)
        {
            start[node] = count;
            var end = Math.Min(edge + _edgeCounts[node - 1], _edgeTargets.Count);
            for (; edge < end; edge++)
            {
                if (Find(_edgeTargets[edge]) is var t and > 0)
                    targets[count++] = t;
            }

            if (dependentsOf.TryGetValue(node, out var dependents))
            {
                foreach (var d in dependents)
                    targets[count++] = d;
            }
        }

        start[n] = count;
        Free(_edgeTargets);
        Free(_edgeCounts);
        return (start, targets);
    }

    private static (int[] Start, int[] Sources) Predecessors(int n, int[] successorStart, int[] successors, List<int> extraRoots)
    {
        var start = new int[n + 1];
        for (var e = 0; e < successorStart[n]; e++)
            start[successors[e] + 1]++;
        foreach (var root in extraRoots)
            start[root + 1]++;
        for (var i = 0; i < n; i++)
            start[i + 1] += start[i];

        var fill = (int[])start.Clone();
        var sources = new int[start[n]];
        for (var node = 0; node < n; node++)
        {
            for (var e = successorStart[node]; e < successorStart[node + 1]; e++)
                sources[fill[successors[e]]++] = node;
        }

        foreach (var root in extraRoots)
            sources[fill[root]++] = 0;
        return (start, sources);
    }

    /// <summary>
    /// Immediate dominators, by preorder number: Lengauer and Tarjan, "A Fast Algorithm for
    /// Finding Dominators in a Flowgraph", the simple version with path compression. Unlike the
    /// iterative algorithms it stays near-linear on long chains, which heaps have (linked lists).
    /// </summary>
    private static int[] LengauerTarjan(int n, int[] number, int[] vertex, int[] parent, int[] predecessorStart, int[] predecessors)
    {
        var semi = new int[n];
        var label = new int[n];
        var ancestor = new int[n];
        var idom = new int[n];
        var bucketHead = new int[n];
        var bucketNext = new int[n];
        var path = new List<int>();
        for (var i = 0; i < n; i++)
        {
            semi[i] = label[i] = i;
            ancestor[i] = bucketHead[i] = -1;
        }

        for (var w = n - 1; w > 0; w--)
        {
            // Semidominator: the earliest vertex with a path to w through later ones.
            var node = vertex[w];
            for (var e = predecessorStart[node]; e < predecessorStart[node + 1]; e++)
            {
                var u = Eval(number[predecessors[e]]);
                if (semi[u] < semi[w])
                    semi[w] = semi[u];
            }

            bucketNext[w] = bucketHead[semi[w]];
            bucketHead[semi[w]] = w;
            ancestor[w] = parent[w];

            var p = parent[w];
            for (var v = bucketHead[p]; v >= 0; v = bucketNext[v])
            {
                var u = Eval(v);
                idom[v] = semi[u] < semi[v] ? u : p;
            }

            bucketHead[p] = -1;
        }

        for (var w = 1; w < n; w++)
        {
            if (idom[w] != semi[w])
                idom[w] = idom[idom[w]];
        }

        idom[0] = 0;
        return idom;

        // The vertex with the smallest semidominator on the forest path up from v, compressing it.
        int Eval(int v)
        {
            if (ancestor[v] < 0)
                return v;

            path.Clear();
            for (var x = v; ancestor[ancestor[x]] >= 0; x = ancestor[x])
                path.Add(x);
            for (var i = path.Count - 1; i >= 0; i--)
            {
                var x = path[i];
                if (semi[label[ancestor[x]]] < semi[label[x]])
                    label[x] = label[ancestor[x]];
                ancestor[x] = ancestor[ancestor[x]];
            }

            return label[v];
        }
    }

    /// <summary>
    /// Down the dominator tree, adding an object's retained size to its type unless an ancestor
    /// of the same type already counted it. By preorder number.
    /// </summary>
    private Dictionary<ulong, long> SumByType(int n, int[] idom, long[] retained, int[] vertex)
    {
        var childStart = new int[n + 1];
        for (var i = 1; i < n; i++)
            childStart[idom[i] + 1]++;
        for (var i = 0; i < n; i++)
            childStart[i + 1] += childStart[i];

        var fill = (int[])childStart.Clone();
        var children = new int[n - 1];
        for (var i = 1; i < n; i++)
            children[fill[idom[i]]++] = i;

        var byType = new long[_typeIds.Count];
        var open = new int[_typeIds.Count];
        var stack = new Stack<(int Number, bool Leaving)>();
        for (var c = childStart[0]; c < childStart[1]; c++)
            stack.Push((children[c], false));
        while (stack.Count > 0)
        {
            var (i, leaving) = stack.Pop();
            var type = _types[vertex[i] - 1];
            if (leaving)
            {
                open[type]--;
                continue;
            }

            if (open[type]++ == 0)
                byType[type] += retained[i];

            stack.Push((i, true));
            for (var c = childStart[i]; c < childStart[i + 1]; c++)
                stack.Push((children[c], false));
        }

        var byTypeId = new Dictionary<ulong, long>(_typeIds.Count);
        for (var t = 0; t < byType.Length; t++)
            byTypeId[_typeIds[t]] = byType[t];
        return byTypeId;
    }
}
