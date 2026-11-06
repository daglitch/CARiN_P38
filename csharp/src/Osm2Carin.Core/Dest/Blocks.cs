using System.Buffers.Binary;
using System.Text;

namespace Osm2Carin.Core.Dest;

/// <summary>A block we write: its size is fixed when it is planned (layouts depend only on contents),
/// its ID when units are allocated, and its bytes are written once every ID is known.</summary>
public abstract class Blk
{
    public uint Id;
    public abstract int Type { get; }
    public abstract int Size { get; }                  // bytes, header included
    public int Units => Math.Max(1, (Size + Fmt.Unit - 1) / Fmt.Unit);
    public abstract void Write(byte[] b);              // b: Units * 2048 zero bytes

    public byte[] Bytes()
    {
        var b = new byte[Units * Fmt.Unit];
        BinaryPrimitives.WriteUInt32BigEndian(b, Id);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)Type);
        Write(b);
        return b;
    }

    public const int MaxSize = 8 * 2048;              // as the disc's own 0x0C/0x0E/0x0F/0x10/0x11 blocks

    protected static void W16(byte[] b, int o, int v) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(o), (ushort)v);
    protected static void W32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o), v);
    protected static void Wi32(byte[] b, int o, long v) => BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(o), (int)v);
}

/// <summary>Latin-1 NUL-terminated strings of one block, each stored once.</summary>
public sealed class TextPool
{
    readonly Dictionary<string, int> at = new();
    readonly List<byte> bytes = new();
    public int Length => bytes.Count;

    /// <summary>Offset of s from the start of the text (0-based); "" or null -> -1 (absent).</summary>
    public int Add(string? s)
    {
        if (string.IsNullOrEmpty(s)) return -1;
        if (at.TryGetValue(s, out int o)) return o;
        o = at[s] = bytes.Count;
        bytes.AddRange(Encoding.Latin1.GetBytes(s));
        bytes.Add(0);
        return o;
    }

    public int Measure(string? s) => string.IsNullOrEmpty(s) || at.ContainsKey(s) ? 0 : Encoding.Latin1.GetByteCount(s) + 1;
    public void CopyTo(byte[] b, int offset) => bytes.CopyTo(b, offset);
}

/// <summary>Letter tries (0x0D cities, 0x0F streets, 0x11 POIs): 12-byte records
/// u32 BLOCK_ID | u8 letter | u8 leaf | u16 offset | u16 count | u16 0 (01-architecture.md §4.4.1).
/// A leaf points at `count` consecutive target records; an inner record at the next level.</summary>
public sealed class Trie
{
    public sealed class Node
    {
        public readonly List<Rec> Recs = new();
        public TrieBlk? Blk;
        public int Index;                              // first record's index in Blk
        public int Offset => TrieBlk.First + 12 * Index;
    }

    public sealed class Rec
    {
        public byte Letter;
        public Blk? Target;                            // leaf: the target block
        public int TargetOffset, Count;                // leaf: first record and count
        public Node? Child;                            // inner
    }

    public readonly struct Item(string name, Blk blk, int offset, int recSize)
    {
        public readonly string Name = name;
        public readonly Blk Blk = blk;
        public readonly int Offset = offset, RecSize = recSize;
    }

    /// <summary>The trie over `items`, sorted by name (ordinal), each a target record. As the disc's
    /// compiler (OpenCarin §4.4.1): group by the next letter ('@' where a name ends); a group whose
    /// records are consecutive in one block is a leaf, else it is split on the following letter.
    /// oneLevel: first letters only (the 0x11 POI tries; the caller keeps each letter in one block).</summary>
    public static Node Build(IReadOnlyList<Item> items, bool oneLevel = false) => Level(items, 0, oneLevel);

    static Node Level(IReadOnlyList<Item> items, int depth, bool oneLevel)
    {
        var node = new Node();
        int i = 0;
        while (i < items.Count)
        {
            char key = depth < items[i].Name.Length ? items[i].Name[depth] : '@';
            int j = i + 1;
            while (j < items.Count && (depth < items[j].Name.Length ? items[j].Name[depth] : '@') == key) j++;
            var g = new List<Item>(j - i);
            for (int k = i; k < j; k++) g.Add(items[k]);
            byte letter = (byte)(key < 256 ? key : '?');
            if (Consecutive(g) || oneLevel)
                foreach (var run in Runs(g))
                    node.Recs.Add(new Rec { Letter = letter, Target = run[0].Blk, TargetOffset = run[0].Offset, Count = run.Count });
            else if (key == '@' || depth >= 60)
                foreach (var run in Runs(g))      // the same name in two blocks: one leaf per block
                    node.Recs.Add(new Rec { Letter = letter, Target = run[0].Blk, TargetOffset = run[0].Offset, Count = run.Count });
            else
                node.Recs.Add(new Rec { Letter = letter, Child = Level(g, depth + 1, false) });
            i = j;
        }
        return node;
    }

    static bool Consecutive(List<Item> g)
    {
        for (int k = 1; k < g.Count; k++)
            if (!ReferenceEquals(g[k].Blk, g[0].Blk) || g[k].Offset != g[k - 1].Offset + g[k].RecSize) return false;
        return true;
    }

    static IEnumerable<List<Item>> Runs(List<Item> g)
    {
        var cur = new List<Item> { g[0] };
        for (int k = 1; k < g.Count; k++)
        {
            if (ReferenceEquals(g[k].Blk, cur[0].Blk) && g[k].Offset == cur[^1].Offset + g[k].RecSize) cur.Add(g[k]);
            else { yield return cur; cur = new List<Item> { g[k] }; }
        }
        yield return cur;
    }

    public static IEnumerable<Node> Nodes(Node root)
    {
        var q = new Queue<Node>();
        q.Enqueue(root);
        while (q.Count > 0)
        {
            var n = q.Dequeue();
            yield return n;
            foreach (var r in n.Recs) if (r.Child != null) q.Enqueue(r.Child);
        }
    }

    /// <summary>Place the nodes of `roots` (each node's records together) in blocks of `type`.</summary>
    public static List<TrieBlk> Pack(int type, IEnumerable<Node> roots)
    {
        var blocks = new List<TrieBlk>();
        TrieBlk? cur = null;
        foreach (var root in roots)
            foreach (var n in Nodes(root))
            {
                if (cur == null || cur.Count + n.Recs.Count > TrieBlk.MaxRecs)
                {
                    if (n.Recs.Count > TrieBlk.MaxRecs) throw new InvalidOperationException("trie node too large");
                    blocks.Add(cur = new TrieBlk(type));
                }
                n.Blk = cur; n.Index = cur.Count;
                cur.Nodes.Add(n); cur.Count += n.Recs.Count;
            }
        return blocks;
    }
}

public sealed class TrieBlk(int type) : Blk
{
    public const int First = 0x0C;
    public const int MaxRecs = (MaxSize - First) / 12;
    public readonly List<Trie.Node> Nodes = new();
    public int Count;
    public override int Type => type;
    public override int Size => First + 12 * Count;

    public override void Write(byte[] b)
    {
        W16(b, 8, First); W16(b, 10, Count);
        foreach (var n in Nodes)
            for (int i = 0; i < n.Recs.Count; i++)
            {
                var r = n.Recs[i];
                int o = First + 12 * (n.Index + i);
                if (r.Child != null)
                {
                    W32(b, o, r.Child.Blk!.Id); b[o + 4] = r.Letter; b[o + 5] = 0;
                    W16(b, o + 6, r.Child.Offset); W16(b, o + 8, r.Child.Recs.Count);
                }
                else
                {
                    W32(b, o, r.Target!.Id); b[o + 4] = r.Letter; b[o + 5] = 1;
                    W16(b, o + 6, r.TargetOffset); W16(b, o + 8, r.Count);
                }
            }
    }
}

/// <summary>An unreferenced block that fills a gap so the block chain stays contiguous: an empty
/// trie block (S0 count 0).</summary>
public sealed class FillerBlk(int units) : Blk
{
    public override int Type => 0x0F;
    public override int Size => units * Fmt.Unit;
    public override void Write(byte[] b) { W16(b, 8, TrieBlk.First); W16(b, 10, 0); }
}
