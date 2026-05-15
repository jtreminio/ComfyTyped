using System.Runtime.CompilerServices;
using ComfyTyped.Types;
using Newtonsoft.Json.Linq;

namespace ComfyTyped.Core;

// ── Shared slot supertype ───────────────────────────────────────────

/// <summary>
/// Anything on a <see cref="ComfyNode"/> that contributes to the workflow JSON <c>inputs</c>
/// object. Either a singular <see cref="INodeInput"/> (one wire key) or a multi-key
/// <see cref="INodeInputList"/> (autogrow expansion under a shared <c>SlotName</c>).
/// </summary>
public interface INodeSlot
{
    /// <summary>Logical slot name. For singular inputs, this equals the wire key. For input lists,
    /// this is the outer prefix (e.g. <c>"images"</c>) — children fan out as <c>"{SlotName}.{Prefix}{i}"</c>.</summary>
    string SlotName { get; }

    bool IsRequired { get; }

    /// <summary>For singular slots, the slot's own type. For lists, the element type.</summary>
    string TypeName { get; }

    /// <summary>Write this slot's contribution into the inputs JObject. Singular slots write one key;
    /// list slots fan out to N keys. Implementations must not write keys belonging to other slots.</summary>
    void SerializeInto(JObject inputs);
}

// ── Non-generic interfaces (for runtime graph operations) ───────────

/// <summary>A node output slot, non-generic for runtime graph walking and serialization.</summary>
public interface INodeOutput
{
    ComfyNode Node { get; }
    int SlotIndex { get; }
    string SlotName { get; }
    string TypeName { get; }
}

/// <summary>A node input slot, non-generic for runtime graph walking and serialization.</summary>
public interface INodeInput : INodeSlot
{
    /// <summary>The concrete wire key for this input. For top-level inputs, equals <see cref="INodeSlot.SlotName"/>.
    /// For list children, equals <c>"{ParentList.SlotName}.{ParentList.Prefix}{index}"</c>.</summary>
    string Name { get; }

    /// <summary>The connected output, if any.</summary>
    INodeOutput? Connection { get; }

    /// <summary>The literal value, if any. Null when connected or unset.</summary>
    object? LiteralValue { get; }

    bool IsConnected { get; }
    bool HasValue { get; }

    /// <summary>
    /// Connect to a non-generic output. Throws <see cref="ArgumentNullException"/> on null
    /// (use <see cref="TryConnectToUntyped"/> for resolver-style sources that may legitimately
    /// return null) and <see cref="InvalidOperationException"/> if types are incompatible at runtime.
    /// </summary>
    void ConnectToUntyped(INodeOutput output, [CallerArgumentExpression(nameof(output))] string? sourceExpr = null);

    /// <summary>
    /// Connect to a non-generic output if <paramref name="output"/> is non-null. Returns
    /// <c>true</c> on success; returns <c>false</c> (no-op, leaves the slot's existing
    /// state unchanged) when <paramref name="output"/> is null. Intended for resolver-style
    /// sources (e.g. <see cref="WorkflowBridge.ResolvePath"/>) that legitimately return
    /// null when a path does not resolve. Type mismatches still throw — null tolerance
    /// is the only soft failure.
    /// </summary>
    /// <returns><c>true</c> if connected; <c>false</c> if <paramref name="output"/> was null.</returns>
    bool TryConnectToUntyped(INodeOutput? output);

    /// <summary>Set a literal value. Throws if this input only accepts connections (e.g. list children).</summary>
    void SetUntyped(object? value);

    /// <summary>Disconnect and clear any literal value. Throws on list children — use
    /// <see cref="INodeInputList"/>.<c>RemoveAt</c> to drop a list slot instead.</summary>
    void Clear();
}

/// <summary>
/// A typed, ordered collection of input connections for ComfyUI's <c>COMFY_AUTOGROW_V3</c>
/// pattern. Each item corresponds to one wire key under the shared <see cref="INodeSlot.SlotName"/>
/// (e.g. <c>"images.image0"</c>, <c>"images.image1"</c>). Use to model nodes like
/// <c>BatchImagesNode</c> or <c>HiDreamO1ReferenceImages</c> whose <c>images</c> input
/// accepts N typed connections.
/// </summary>
public interface INodeInputList : INodeSlot
{
    /// <summary>The per-child key prefix from the schema's <c>template.prefix</c> (e.g. <c>"image"</c>).</summary>
    string Prefix { get; }

    /// <summary>Lower-bound hint from the schema. Informational — not enforced on serialize.</summary>
    int Min { get; }

    /// <summary>Upper-bound from the schema. <see cref="NodeInputList{T}.Add(NodeOutput{T})"/> throws past this.</summary>
    int Max { get; }

    int Count { get; }

    /// <summary>The element type's <c>TypeName</c>, matching <see cref="INodeSlot.TypeName"/>.</summary>
    string ElementTypeName { get; }

    /// <summary>Read-only view of the child slots as <see cref="INodeInput"/>s, in order.</summary>
    IReadOnlyList<INodeInput> Items { get; }

    /// <summary>If <paramref name="wireKey"/> belongs to this list (matches
    /// <c>"{SlotName}.{Prefix}{n}"</c> for a non-negative integer <c>n</c>), returns
    /// the parsed index; otherwise <c>-1</c>. Used by <see cref="ComfyGraph.FromWorkflow"/>
    /// and <see cref="WorkflowBridge"/> to route wire keys to the right list.</summary>
    int TryParseKey(string wireKey);

    /// <summary>Append a fresh child slot at the end and return it for deserialization wiring.
    /// Does not fire change events. Intended for <see cref="ComfyGraph.FromWorkflow"/>.</summary>
    INodeInput AppendUnsetSlot();
}

// ── Typed output slot ───────────────────────────────────────────────

/// <summary>A typed output slot on a ComfyUI node. Connects to <see cref="NodeInput{T}"/> of the same type.</summary>
public sealed class NodeOutput<T> : INodeOutput where T : IComfyType
{
    public ComfyNode Node { get; }
    public int SlotIndex { get; }
    public string SlotName { get; private set; }
    public string TypeName => T.TypeName;

    internal NodeOutput(ComfyNode node, int slotIndex, string slotName)
    {
        Node = node;
        SlotIndex = slotIndex;
        SlotName = slotName;
    }

    /// <summary>
    /// Rename this slot in place. Used by <see cref="UnknownNode.WithOutputs"/>
    /// to apply declarative slot-name updates to existing slots without forcing
    /// re-allocation. Internal because typed (generated) nodes set their slot
    /// names at construction and shouldn't be renamed; the rename path is only
    /// meaningful for <see cref="UnknownNode"/>.
    /// </summary>
    internal void RenameSlot(string newName) => SlotName = newName;
}

// ── Typed input slot (union: connection OR literal value) ───────────

/// <summary>A typed input slot on a ComfyUI node. Can hold either a connection to a <see cref="NodeOutput{T}"/> or a literal value.</summary>
public sealed class NodeInput<T> : INodeInput where T : IComfyType
{
    /// <summary>Concrete wire key. Mutable only via internal renumber paths on
    /// <see cref="NodeInputList{T}"/> when items shift after <c>RemoveAt</c>.</summary>
    public string Name { get; internal set; }
    public string SlotName => ParentList?.SlotName ?? Name;
    public bool IsRequired { get; }
    public string TypeName => T.TypeName;

    private NodeOutput<T>? _connection;
    private INodeOutput? _untypedConnection;
    private object? _literal;
    private readonly bool _connectionOnly;

    /// <summary>The list this child belongs to, or null for top-level inputs.
    /// Mutations on list children route their change notifications through the
    /// parent list (which fires <c>InputListChanged</c>) rather than firing the
    /// node's regular <c>InputChanged</c> event — keeps the bridge's two handlers
    /// cleanly disjoint.</summary>
    internal INodeInputList? ParentList { get; set; }

    public NodeOutput<T>? TypedConnection => _connection;
    public INodeOutput? Connection => (INodeOutput?)_connection ?? _untypedConnection;
    public object? LiteralValue => _literal;
    public bool IsConnected => _connection is not null || _untypedConnection is not null;
    public bool HasValue => IsConnected || _literal is not null;

    /// <summary>The node that owns this input slot. Used to bubble change events for auto-sync.</summary>
    internal ComfyNode Owner { get; }

    internal NodeInput(string name, bool required, ComfyNode owner, bool connectionOnly = false)
    {
        Name = name;
        IsRequired = required;
        Owner = owner;
        _connectionOnly = connectionOnly;
    }

    /// <summary>Connect this input to a typed output.</summary>
    public void ConnectTo(NodeOutput<T> output)
    {
        _connection = output;
        _untypedConnection = null;
        _literal = null;
        RaiseChanged();
    }

    /// <summary>Set a literal value (for primitive/combo inputs). Throws on list children.</summary>
    public void Set(object? value)
    {
        if (_connectionOnly)
        {
            throw new InvalidOperationException(
                $"Input '{Name}' on {Owner.GetType().Name}#{Owner.Id} is connection-only "
                + "(list child); use ConnectTo to bind an output, or remove via the parent list.");
        }
        _literal = value;
        _connection = null;
        _untypedConnection = null;
        RaiseChanged();
    }

    public void ConnectToUntyped(INodeOutput output, [CallerArgumentExpression(nameof(output))] string? sourceExpr = null)
    {
        if (output is null)
        {
            throw new ArgumentNullException(nameof(output),
                $"Cannot connect null ({sourceExpr}) to input '{Name}' on {Owner.GetType().Name}#{Owner.Id}.");
        }
        if (output is NodeOutput<T> typed)
        {
            ConnectTo(typed);
            return;
        }
        // Allow wildcard connections (AnyType from UnknownNode outputs, ComfyMatchTypeV3 from V3 wildcard slots).
        if (IsWildcard(typeof(T)) || IsWildcard(OutputMarkerType(output)))
        {
            _connection = null;
            _literal = null;
            _untypedConnection = output;
            RaiseChanged();
            return;
        }

        throw new InvalidOperationException(
            $"Cannot connect output of type '{output.TypeName}' to input '{Name}' of type '{T.TypeName}'.");
    }

    public bool TryConnectToUntyped(INodeOutput? output)
    {
        if (output is null)
        {
            return false;
        }
        ConnectToUntyped(output);
        return true;
    }

    private static bool IsWildcard(Type? markerType) =>
        markerType == typeof(AnyType) || markerType == typeof(ComfyMatchTypeV3);

    private static Type? OutputMarkerType(INodeOutput output)
    {
        Type t = output.GetType();
        return t.IsGenericType && t.GetGenericTypeDefinition() == typeof(NodeOutput<>)
            ? t.GetGenericArguments()[0]
            : null;
    }

    /// <summary>The effective connection, typed or untyped.</summary>
    internal INodeOutput? EffectiveConnection => (INodeOutput?)_connection ?? _untypedConnection;

    public void SetUntyped(object? value) => Set(value);

    public void Clear()
    {
        if (ParentList is not null)
        {
            throw new InvalidOperationException(
                $"Input '{Name}' on {Owner.GetType().Name}#{Owner.Id} is a list child; "
                + "use the parent NodeInputList's RemoveAt or Clear to drop slots.");
        }
        _connection = null;
        _untypedConnection = null;
        _literal = null;
        RaiseChanged();
    }

    /// <summary>Bubble a value change to the right place: parent list (for list children)
    /// or the node's regular InputChanged event (for top-level inputs).</summary>
    private void RaiseChanged()
    {
        if (ParentList is NodeInputListChangeSink sink)
        {
            sink.OnChildValueChanged();
        }
        else
        {
            Owner.RaiseInputChanged(this);
        }
    }

    public void SerializeInto(JObject inputs)
    {
        JToken? value = SerializeValue();
        if (value is not null)
        {
            inputs[Name] = value;
        }
    }

    /// <summary>Compute the JToken representation of this slot's current state, without
    /// writing anywhere. Used by <see cref="SerializeInto"/> and by
    /// <see cref="NodeInputList{T}.SerializeInto"/> for child fan-out.</summary>
    internal JToken? SerializeValue()
    {
        INodeOutput? conn = EffectiveConnection;
        if (conn is not null)
        {
            return new JArray(conn.Node.Id, conn.SlotIndex);
        }
        if (_literal is not null)
        {
            return JToken.FromObject(_literal);
        }

        return null;
    }
}

// ── Typed input list ────────────────────────────────────────────────

/// <summary>Internal contract for routing list-child value changes back to the parent list.
/// Implemented by <see cref="NodeInputList{T}"/>; exposed via <see cref="NodeInput{T}.ParentList"/>
/// so <see cref="NodeInput{T}.RaiseChanged"/> can dispatch without knowing the element type.</summary>
internal interface NodeInputListChangeSink
{
    void OnChildValueChanged();
}

/// <summary>A typed, ordered list of <see cref="NodeInput{T}"/> children that share a
/// wire-key prefix. Models ComfyUI's <c>COMFY_AUTOGROW_V3</c> pattern. List children are
/// connection-only and route mutations through the list (which fires
/// <see cref="ComfyNode.InputListChanged"/>) instead of <see cref="ComfyNode.InputChanged"/>.</summary>
public sealed class NodeInputList<T> : INodeInputList, NodeInputListChangeSink where T : IComfyType
{
    public string SlotName { get; }
    public string Prefix { get; }
    public int Min { get; }
    public int Max { get; }
    public bool IsRequired { get; }
    public string TypeName => T.TypeName;
    public string ElementTypeName => T.TypeName;

    private readonly List<NodeInput<T>> _items = [];
    private bool _suppressEvents;

    internal ComfyNode Owner { get; }

    public int Count => _items.Count;
    public IReadOnlyList<INodeInput> Items => _items;
    public NodeInput<T> this[int index] => _items[index];

    internal NodeInputList(string slotName, string prefix, int min, int max, bool required, ComfyNode owner)
    {
        SlotName = slotName;
        Prefix = prefix;
        Min = min;
        Max = max;
        IsRequired = required;
        Owner = owner;
    }

    /// <summary>Append a child connected to <paramref name="output"/>. Throws if at <see cref="Max"/>.
    /// Fires <see cref="ComfyNode.InputListChanged"/> once.</summary>
    public NodeInput<T> Add(NodeOutput<T> output)
    {
        NodeInput<T> child = AppendChildSilent();
        _suppressEvents = true;
        try { child.ConnectTo(output); }
        finally { _suppressEvents = false; }
        RaiseListChanged();
        return child;
    }

    /// <summary>Append a child connected to a non-generic <paramref name="output"/> (e.g. wildcard
    /// from <see cref="UnknownNode"/>). Type compatibility check matches
    /// <see cref="NodeInput{T}.ConnectToUntyped"/>'s rules.</summary>
    public NodeInput<T> AddFromUntyped(INodeOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        NodeInput<T> child = AppendChildSilent();
        _suppressEvents = true;
        try { child.ConnectToUntyped(output); }
        finally { _suppressEvents = false; }
        RaiseListChanged();
        return child;
    }

    /// <summary>Append every output in <paramref name="outputs"/> in order. Single
    /// <see cref="ComfyNode.InputListChanged"/> fire at the end (not one per element).</summary>
    public void AddRange(IEnumerable<NodeOutput<T>> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        _suppressEvents = true;
        try
        {
            foreach (NodeOutput<T> output in outputs)
            {
                NodeInput<T> child = AppendChildSilent();
                child.ConnectTo(output);
            }
        }
        finally { _suppressEvents = false; }
        RaiseListChanged();
    }

    /// <summary>Remove the child at <paramref name="index"/>. Tail children are
    /// renumbered to keep wire keys contiguous (<c>image2</c> becomes <c>image1</c> if
    /// <c>image1</c> was removed). Fires <see cref="ComfyNode.InputListChanged"/>.</summary>
    public void RemoveAt(int index)
    {
        NodeInput<T> removed = _items[index];
        removed.ParentList = null;
        _items.RemoveAt(index);
        RenumberFrom(index);
        RaiseListChanged();
    }

    public void Clear()
    {
        foreach (NodeInput<T> child in _items)
        {
            child.ParentList = null;
        }
        _items.Clear();
        RaiseListChanged();
    }

    public IEnumerator<NodeInput<T>> GetEnumerator() => _items.GetEnumerator();

    public int TryParseKey(string wireKey)
    {
        ArgumentNullException.ThrowIfNull(wireKey);
        string keyPrefix = $"{SlotName}.{Prefix}";
        if (!wireKey.StartsWith(keyPrefix, StringComparison.Ordinal))
        {
            return -1;
        }
        string indexStr = wireKey[keyPrefix.Length..];
        if (indexStr.Length == 0 || !int.TryParse(indexStr, out int idx) || idx < 0)
        {
            return -1;
        }

        return idx;
    }

    public INodeInput AppendUnsetSlot() => AppendChildSilent();

    public void SerializeInto(JObject inputs)
    {
        foreach (NodeInput<T> child in _items)
        {
            JToken? value = child.SerializeValue();
            if (value is not null)
            {
                inputs[child.Name] = value;
            }
        }
    }

    void NodeInputListChangeSink.OnChildValueChanged()
    {
        if (!_suppressEvents)
        {
            RaiseListChanged();
        }
    }

    private NodeInput<T> AppendChildSilent()
    {
        if (_items.Count >= Max)
        {
            throw new InvalidOperationException(
                $"NodeInputList<{T.TypeName}> '{SlotName}' on "
                + $"{Owner.GetType().Name}#{Owner.Id} is at max capacity ({Max}).");
        }
        NodeInput<T> child = new(WireKeyAt(_items.Count), required: true, Owner, connectionOnly: true)
        {
            ParentList = this,
        };
        _items.Add(child);

        return child;
    }

    private void RenumberFrom(int startIndex)
    {
        for (int i = startIndex; i < _items.Count; i++)
        {
            _items[i].Name = WireKeyAt(i);
        }
    }

    private string WireKeyAt(int index) => $"{SlotName}.{Prefix}{index}";

    private void RaiseListChanged() => Owner.RaiseInputListChanged(this);
}
