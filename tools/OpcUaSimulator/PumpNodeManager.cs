using Opc.Ua;
using Opc.Ua.Server;

namespace ScadaDarbox.Tools.OpcUaSimulator;

/// <summary>
/// The simulated address space: one pump's pressure and run state.
/// </summary>
/// <remarks>
/// Values are published with an explicit source timestamp, because that is the thing
/// worth exercising — an OPC UA server states when the value was captured, and the
/// driver is supposed to carry that through rather than substitute its own read time.
/// </remarks>
public sealed class PumpNodeManager : CustomNodeManager2
{
    public const string NamespaceUri = "urn:scadadarbox:simulator";

    private BaseDataVariableState? _pressure;
    private BaseDataVariableState? _running;

    public PumpNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        : base(server, configuration, NamespaceUri)
    {
    }

    /// <summary>Publishes a new reading, stamped with the moment it was "captured".</summary>
    public void Publish(double pressureBar, bool running, DateTime capturedAtUtc)
    {
        lock (Lock)
        {
            Apply(_pressure, pressureBar, capturedAtUtc);
            Apply(_running, running, capturedAtUtc);
        }
    }

    /// <summary>Marks the pressure as unreadable, without changing its last value.</summary>
    /// <remarks>
    /// Lets a test drive the case that matters most: a server that answers, but says the
    /// value is not trustworthy. The driver must report that rather than the number.
    /// </remarks>
    public void PublishBadPressure()
    {
        lock (Lock)
        {
            if (_pressure is null)
            {
                return;
            }

            _pressure.StatusCode = StatusCodes.BadNoCommunication;
            _pressure.Timestamp = DateTime.UtcNow;
            _pressure.ClearChangeMasks(SystemContext, false);
        }
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            var folder = new FolderState(null)
            {
                SymbolicName = "Pump1",
                NodeId = new NodeId("Pump1", NamespaceIndex),
                BrowseName = new QualifiedName("Pump1", NamespaceIndex),
                DisplayName = "Pump1",
                TypeDefinitionId = ObjectTypeIds.FolderType,
                EventNotifier = EventNotifiers.None,
            };

            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
            {
                externalReferences[ObjectIds.ObjectsFolder] = references = [];
            }

            folder.AddReference(ReferenceTypeIds.Organizes, isInverse: true, ObjectIds.ObjectsFolder);
            references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, folder.NodeId));

            _pressure = Variable(folder, "Pressure", DataTypeIds.Double, 0.0);
            _running = Variable(folder, "Running", DataTypeIds.Boolean, false);

            AddPredefinedNode(SystemContext, folder);
        }
    }

    private BaseDataVariableState Variable(NodeState parent, string name, NodeId dataType, object initialValue)
    {
        var variable = new BaseDataVariableState(parent)
        {
            SymbolicName = name,
            // The identifier a tag's source address points at, e.g. ns=2;s=Pump1.Pressure.
            NodeId = new NodeId($"Pump1.{name}", NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = name,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            ReferenceTypeId = ReferenceTypeIds.Organizes,
            DataType = dataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentReadOrWrite,
            UserAccessLevel = AccessLevels.CurrentReadOrWrite,
            Value = initialValue,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow,
        };

        parent.AddChild(variable);
        return variable;
    }

    private void Apply(BaseDataVariableState? variable, object value, DateTime capturedAtUtc)
    {
        if (variable is null)
        {
            return;
        }

        variable.Value = value;
        variable.StatusCode = StatusCodes.Good;
        variable.Timestamp = capturedAtUtc;
        variable.ClearChangeMasks(SystemContext, false);
    }
}
