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
/// pattern. Each item corresponds to one wire key under the shared <see cref="INodeSlot.SlotName"/>.
/// Two naming strategies are supported (set at construction; opaque to callers):
/// <list type="bullet">
///   <item><b>Prefix</b> — generated names, sequential, 0-indexed. Used by
///         <c>BatchImagesNode</c> (<c>images.image0</c>, <c>images.image1</c>, …).</item>
///   <item><b>Names</b> — explicit names from the schema. Used by
///         <c>HiDreamO1ReferenceImages</c> (<c>images.image_1</c>, <c>images.image_2</c>, …).</item>
/// </list>
/// </summary>
public interface INodeInputList : INodeSlot
{
    /// <summary>Lower-bound hint from the schema. Informational — not enforced on serialize.</summary>
    int Min { get; }

    /// <summary>Upper-bound from the schema. <see cref="NodeInputList{T}.Add(NodeOutput{T})"/> throws past this.</summary>
    int Max { get; }

    int Count { get; }

    /// <summary>The element type's <c>TypeName</c>, matching <see cref="INodeSlot.TypeName"/>.</summary>
    string ElementTypeName { get; }

    /// <summary>Read-only view of the child slots as <see cref="INodeInput"/>s, in order.</summary>
    IReadOnlyList<INodeInput> Items { get; }

    /// <summary>If <paramref name="wireKey"/> belongs to this list, returns the 0-based
    /// position in <see cref="Items"/> that owns it; otherwise <c>-1</c>. Used by
    /// <see cref="ComfyGraph.FromWorkflow"/> and <see cref="WorkflowBridge"/> to route wire keys.</summary>
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
    /// <summary>Concrete wire key for this input.
    /// <para>For top-level inputs this is the schema slot name (e.g. <c>"ckpt_name"</c>) and is
    /// immutable for the lifetime of the slot.</para>
    /// <para>For list children, this is the wire key at the moment the child was appended
    /// (e.g. <c>"images.image2"</c>). It is <em>not</em> kept in sync with the child's position
    /// after sibling <see cref="NodeInputList{T}.RemoveAt"/> calls — the parent list is the
    /// source of truth for current wire keys and regenerates them on serialize. If you need to
    /// look a child up, use the <see cref="NodeInputList{T}"/> indexer; don't cache
    /// <see cref="Name"/> across structural mutations.</para></summary>
    public string Name { get; }
    public string SlotName => ParentList?.SlotName ?? Name;
    public bool IsRequired { get; }
    public string TypeName => T.TypeName;

    private NodeOutput<T>? _connection;
    private INodeOutput? _untypedConnection;
    private object? _literal;

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

    internal NodeInput(string name, bool required, ComfyNode owner)
    {
        Name = name;
        IsRequired = required;
        Owner = owner;
    }

    /// <summary>Connect this input to a typed output.</summary>
    public void ConnectTo(NodeOutput<T> output)
    {
        _connection = output;
        _untypedConnection = null;
        _literal = null;
        RaiseChanged();
    }

    /// <summary>Set a literal value. Valid for both top-level inputs and list children
    /// whose element type is a primitive (e.g. <see cref="NodeInputList{T}"/> over
    /// <c>FloatType</c>/<c>IntType</c>/<c>BoolType</c>/<c>StringType</c> on a node like
    /// <c>GLSLShader</c>).</summary>
    public void Set(object? value)
    {
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

/// <summary>A typed, ordered list of <see cref="NodeInput{T}"/> children whose wire-key
/// suffixes are derived from the schema. Models ComfyUI's <c>COMFY_AUTOGROW_V3</c> pattern
/// in either of its two shapes (prefix-generated or explicit-names). List children are
/// connection-only and route mutations through the list (which fires
/// <see cref="ComfyNode.InputListChanged"/>) instead of <see cref="ComfyNode.InputChanged"/>.</summary>
public sealed class NodeInputList<T> : INodeInputList, NodeInputListChangeSink where T : IComfyType
{
    public string SlotName { get; }
    public int Min { get; }
    public int Max { get; }
    public bool IsRequired { get; }
    public string TypeName => T.TypeName;
    public string ElementTypeName => T.TypeName;

    // Exactly one of these is non-null. Naming strategy is hidden from callers:
    // _prefix → child name = $"{_prefix}{index}" (generated, 0-indexed, no separator).
    // _names  → child name = _names[index] (explicit, schema-provided, any format).
    private readonly string? _prefix;
    private readonly IReadOnlyList<string>? _names;

    private readonly List<NodeInput<T>> _items = [];
    private bool _suppressEvents;

    internal ComfyNode Owner { get; }

    public int Count => _items.Count;
    public IReadOnlyList<INodeInput> Items => _items;
    public NodeInput<T> this[int index] => _items[index];

    /// <summary>Construct a prefix-shape list (generated child names: <c>{prefix}0</c>, <c>{prefix}1</c>, …).</summary>
    internal NodeInputList(string slotName, string prefix, int min, int max, bool required, ComfyNode owner)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            throw new ArgumentException("Prefix must be non-empty.", nameof(prefix));
        }
        SlotName = slotName;
        _prefix = prefix;
        _names = null;
        Min = min;
        Max = max;
        IsRequired = required;
        Owner = owner;
    }

    /// <summary>Construct a names-shape list (explicit child names from the schema).
    /// <paramref name="max"/> is capped at <c>names.Count</c> — there's no way to
    /// add more children than there are names available.</summary>
    internal NodeInputList(string slotName, IReadOnlyList<string> names, int min, int max, bool required, ComfyNode owner)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
        {
            throw new ArgumentException("Names list must be non-empty.", nameof(names));
        }
        SlotName = slotName;
        _prefix = null;
        _names = names;
        Min = min;
        Max = Math.Min(max, names.Count);
        IsRequired = required;
        Owner = owner;
    }

    /// <summary>Append a child connected to <paramref name="output"/>. Throws if at <see cref="Max"/>.
    /// Atomic: if the connection fails, no child is added and no event fires. Fires
    /// <see cref="ComfyNode.InputListChanged"/> once on success.</summary>
    public NodeInput<T> Add(NodeOutput<T> output)
    {
        NodeInput<T> child = BuildDetachedChild();
        _suppressEvents = true;
        try
        {
            child.ConnectTo(output);
        }
        finally
        {
            _suppressEvents = false;
        }
        Attach(child);

        return child;
    }

    /// <summary>Append a child connected to a non-generic <paramref name="output"/> (e.g. wildcard
    /// from <see cref="UnknownNode"/>). Type compatibility check matches
    /// <see cref="NodeInput{T}.ConnectToUntyped"/>'s rules. Atomic: type-mismatch throw leaves
    /// the list unchanged.</summary>
    public NodeInput<T> AddFromUntyped(INodeOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        NodeInput<T> child = BuildDetachedChild();
        _suppressEvents = true;
        try
        {
            child.ConnectToUntyped(output);
        }
        finally
        {
            _suppressEvents = false;
        }
        Attach(child);

        return child;
    }

    /// <summary>Append every output in <paramref name="outputs"/> in order. Single
    /// <see cref="ComfyNode.InputListChanged"/> fire at the end (not one per element).
    /// Atomic per element — a mid-stream throw leaves prior appends in place but does
    /// not append the failed one. The single ListChanged fires only if at least one
    /// element succeeded.</summary>
    public void AddRange(IEnumerable<NodeOutput<T>> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        _suppressEvents = true;
        bool anyAttached = false;
        try
        {
            foreach (NodeOutput<T> output in outputs)
            {
                NodeInput<T> child = BuildDetachedChild();
                child.ConnectTo(output);
                Attach(child);
                anyAttached = true;
            }
        }
        finally
        {
            _suppressEvents = false;
            if (anyAttached)
            {
                RaiseListChanged();
            }
        }
    }

    /// <summary>Remove the child at <paramref name="index"/>. Surviving items shift down
    /// by one position; their schema wire-keys regenerate on the next serialize from
    /// position-in-list (so for the names variant, item at old position 2 now occupies
    /// the schema name at position 1). Fires <see cref="ComfyNode.InputListChanged"/>.</summary>
    public void RemoveAt(int index)
    {
        NodeInput<T> removed = _items[index];
        removed.ParentList = null;
        _items.RemoveAt(index);
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
        string slotKeyPrefix = $"{SlotName}.";
        if (!wireKey.StartsWith(slotKeyPrefix, StringComparison.Ordinal))
        {
            return -1;
        }
        string suffix = wireKey[slotKeyPrefix.Length..];
        if (_names is not null)
        {
            for (int i = 0; i < _names.Count; i++)
            {
                if (_names[i] == suffix)
                {
                    return i;
                }
            }

            return -1;
        }
        if (!suffix.StartsWith(_prefix!, StringComparison.Ordinal))
        {
            return -1;
        }
        string indexStr = suffix[_prefix!.Length..];
        if (indexStr.Length == 0 || !int.TryParse(indexStr, out int idx) || idx < 0)
        {
            return -1;
        }

        return idx;
    }

    /// <summary>Append a child without firing events, for deserialization use.
    /// The returned child is fully attached (in <c>_items</c>, <c>ParentList</c> set);
    /// caller wires the value via <see cref="INodeInput.ConnectToUntyped"/> /
    /// <see cref="INodeInput.SetUntyped"/>. Throws if at <see cref="Max"/>.</summary>
    public INodeInput AppendUnsetSlot()
    {
        NodeInput<T> child = BuildDetachedChild();
        _suppressEvents = true;
        try
        {
            Attach(child);
        }
        finally
        {
            _suppressEvents = false;
        }

        return child;
    }

    public void SerializeInto(JObject inputs)
    {
        // Wire keys are derived from current position-in-list, not from any stored
        // Name on the child — child.Name reflects the wire key at append time only.
        for (int i = 0; i < _items.Count; i++)
        {
            JToken? value = _items[i].SerializeValue();
            if (value is not null)
            {
                inputs[WireKeyAt(i)] = value;
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

    /// <summary>Construct a child with <see cref="NodeInput{T}.ParentList"/> already set
    /// (so any mutations during wiring route through the parent list's sink, where
    /// <c>_suppressEvents</c> can silence them), but not yet attached to <c>_items</c>.
    /// Atomicity: callers wrap the wiring step in <c>_suppressEvents</c> and call
    /// <see cref="Attach"/> only on success — if wiring throws, no child reaches the list.
    /// </summary>
    private NodeInput<T> BuildDetachedChild()
    {
        if (_items.Count >= Max)
        {
            throw new InvalidOperationException(
                $"NodeInputList<{T.TypeName}> '{SlotName}' on "
                + $"{Owner.GetType().Name}#{Owner.Id} is at max capacity ({Max}).");
        }

        // Name is informational on list children — stamped with the wire key at append
        // time but not kept in sync after sibling removals (see the Name property docstring
        // on NodeInput<T>). Serialization derives keys from position instead.
        return new NodeInput<T>(WireKeyAt(_items.Count), required: true, Owner)
        {
            ParentList = this,
        };
    }

    /// <summary>Commit a built+wired child to <c>_items</c> and fire
    /// <see cref="ComfyNode.InputListChanged"/> (suppressed during <c>AddRange</c>'s batch).</summary>
    private void Attach(NodeInput<T> child)
    {
        _items.Add(child);
        if (!_suppressEvents)
        {
            RaiseListChanged();
        }
    }

    private string WireKeyAt(int index) =>
        _names is not null
            ? $"{SlotName}.{_names[index]}"
            : $"{SlotName}.{_prefix}{index}";

    private void RaiseListChanged() => Owner.RaiseInputListChanged(this);
}
